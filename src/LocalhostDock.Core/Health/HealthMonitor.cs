using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using LocalhostDock.Config;

namespace LocalhostDock.Health;

/// <summary>Resolves an app's <c>health</c> setting to a URL on this machine (never anywhere else).</summary>
public static class HealthUrl
{
    public static Uri? Resolve(AppEntry app)
    {
        string? h = app.Health?.Trim();
        if (string.IsNullOrEmpty(h)) return null;
        if (h.StartsWith('/'))
            return app.Port is int p && Uri.TryCreate($"http://localhost:{p}{h}", UriKind.Absolute, out var rel) ? rel : null;
        if (!Uri.TryCreate(h, UriKind.Absolute, out var abs) || abs.Scheme is not ("http" or "https")) return null;
        return IsLocal(abs) ? abs : null;
    }

    /// <summary>localhost, *.localhost, 127.0.0.0/8 or ::1 only: health checks never leave the machine.</summary>
    public static bool IsLocal(Uri uri)
    {
        string host = uri.IdnHost.Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);
    }
}

/// <summary>One health check result.</summary>
public sealed record HealthSample(DateTime AtUtc, bool Ok, int? Status, double LatencyMs, string? Error);

/// <summary>Immutable view of an app's health for the UI/CLI.</summary>
public sealed record HealthSnapshot(HealthSample? Last, int ConsecutiveFailures, int Samples, double? AvgLatencyMs, double Availability)
{
    /// <summary>null until the first check has finished.</summary>
    public bool? Healthy => Last is null ? null : Last.Ok;
    public string Describe() => Last is null ? "checking…"
        : Last.Ok ? $"healthy · {Last.LatencyMs:0} ms"
        : Last.Status is int s ? $"unhealthy: HTTP {s}" : $"unhealthy: {Last.Error}";
}

/// <summary>
/// HTTP health/readiness checks for apps that have a <c>health</c> setting. Checks run in the background at
/// each app's interval while it's up, with a per-check timeout; the last 120 results are kept (bounded history
/// for latency and availability). No proxies, no redirects followed, and only URLs on this machine.
/// </summary>
public sealed class HealthMonitor : IDisposable
{
    public const int HistorySize = 120;

    private sealed class State
    {
        public readonly object Lock = new();
        public readonly Queue<HealthSample> History = new();
        public int ConsecutiveFailures;
        public HealthSample? Last;
        public bool InFlight;
        public DateTime NextDueUtc;
    }

    private readonly ConcurrentDictionary<string, State> _states = new();
    private readonly HttpClient _http;

    public HealthMonitor()
    {
        _http = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false, // a 3xx counts as healthy; never follow it off-machine
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.Zero, // each result must probe the current listener after port reuse
            ConnectCallback = ConnectLoopbackAsync,
        })
        { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("LocalhostDock-health/1");
    }

    public HealthSnapshot? Get(string appId)
    {
        if (!_states.TryGetValue(appId, out var s)) return null;
        lock (s.Lock)
        {
            var samples = s.History.ToArray();
            var ok = samples.Where(x => x.Ok).ToArray();
            return new HealthSnapshot(s.Last, s.ConsecutiveFailures, samples.Length,
                ok.Length > 0 ? ok.Average(x => x.LatencyMs) : null,
                samples.Length > 0 ? (double)ok.Length / samples.Length : 0);
        }
    }

    public IReadOnlyList<HealthSample> History(string appId)
    {
        if (!_states.TryGetValue(appId, out var s)) return Array.Empty<HealthSample>();
        lock (s.Lock) return s.History.ToArray();
    }

    /// <summary>Starts a background check if one is due (or <paramref name="urgent"/>) and none is running.</summary>
    public void MaybeCheck(AppEntry app, bool urgent = false)
    {
        if (HealthUrl.Resolve(app) is null) return;
        var s = _states.GetOrAdd(app.Id, _ => new State());
        lock (s.Lock)
        {
            if (s.InFlight) return;
            if (!urgent && DateTime.UtcNow < s.NextDueUtc) return;
            s.InFlight = true;
        }
        var copy = app.Clone();
        _ = Task.Run(async () =>
        {
            try { await CheckNowAsync(copy, CancellationToken.None, s); }
            catch { /* recorded as a failed sample */ }
        });
    }

    /// <summary>
    /// "localhost" can be 127.0.0.1 or ::1 depending on how the dev server bound. Trying them one after the other
    /// costs ~2 s on Windows when the first is refused, so race both and use whichever connects first.
    /// </summary>
    private static async ValueTask<Stream> ConnectLoopbackAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var endpoint = context.DnsEndPoint;
        IPAddress[] targets = endpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || endpoint.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            ? new[] { IPAddress.Loopback, IPAddress.IPv6Loopback }
            : IPAddress.TryParse(endpoint.Host.Trim('[', ']'), out var ip) ? new[] { ip } : await System.Net.Dns.GetHostAddressesAsync(endpoint.Host, ct);

        using var race = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var attempts = targets.Select(async address =>
        {
            var socket = new System.Net.Sockets.Socket(address.AddressFamily, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), race.Token);
                return socket;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }).ToList();

        Exception? last = null;
        while (attempts.Count > 0)
        {
            var done = await Task.WhenAny(attempts);
            attempts.Remove(done);
            try
            {
                var socket = await done;
                race.Cancel(); // abandon the slower attempt
                foreach (var other in attempts) _ = other.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); }, TaskScheduler.Default);
                return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) { last = ex; }
        }
        throw last ?? new HttpRequestException("Couldn't connect.");
    }

    /// <summary>Runs one check now and records it.</summary>
    public Task<HealthSample> CheckNowAsync(AppEntry app, CancellationToken ct) => CheckNowAsync(app, ct, _states.GetOrAdd(app.Id, _ => new State()));

    private async Task<HealthSample> CheckNowAsync(AppEntry app, CancellationToken ct, State s)
    {
        lock (s.Lock) s.InFlight = true;

        HealthSample sample;
        var uri = HealthUrl.Resolve(app);
        if (uri is null)
        {
            sample = new HealthSample(DateTime.UtcNow, false, null, 0, "no valid health URL");
        }
        else
        {
            var sw = Stopwatch.StartNew();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(app.HealthTimeoutMs);
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, uri);
                using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                int code = (int)res.StatusCode;
                sample = new HealthSample(DateTime.UtcNow, code is >= 200 and < 400, code, sw.Elapsed.TotalMilliseconds, null);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                sample = new HealthSample(DateTime.UtcNow, false, null, sw.Elapsed.TotalMilliseconds, $"no answer within {app.HealthTimeoutMs} ms");
            }
            catch (HttpRequestException ex)
            {
                sample = new HealthSample(DateTime.UtcNow, false, null, sw.Elapsed.TotalMilliseconds, ex.InnerException?.Message ?? ex.Message);
            }
            catch (OperationCanceledException)
            {
                lock (s.Lock) s.InFlight = false;
                throw;
            }
        }

        lock (s.Lock)
        {
            s.Last = sample;
            s.ConsecutiveFailures = sample.Ok ? 0 : s.ConsecutiveFailures + 1;
            s.History.Enqueue(sample);
            while (s.History.Count > HistorySize) s.History.Dequeue();
            s.NextDueUtc = DateTime.UtcNow.AddSeconds(app.HealthIntervalSec);
            s.InFlight = false;
        }
        return sample;
    }

    /// <summary>Forget an app's state (it stopped, or its health setting changed).</summary>
    public void Reset(string appId) => _states.TryRemove(appId, out _);

    public void Dispose() => _http.Dispose();
}
