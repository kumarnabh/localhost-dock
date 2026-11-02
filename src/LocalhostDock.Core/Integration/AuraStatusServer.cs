using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalhostDock.Config;
using LocalhostDock.Core;

namespace LocalhostDock.Integration;

/// <summary>
/// Small single-request HTTP/1.1 endpoint. Explicit IPv4 loopback binding, bounded headers/concurrency/time,
/// exact Host and bearer checks, no browser origins, no request bodies, no command/controller references.
/// Requests read a published immutable snapshot; they never poll, probe, or control services.
/// </summary>
public sealed class AuraStatusServer : IDisposable
{
    public sealed record Endpoint(string Schema, string Url, string Token, int Pid);
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _slots = new(8);
    private readonly Task _loop;
    private readonly string _endpointFile;
    private readonly string _discovery;
    private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly TimeSpan _maxAge;
    private (byte[] Json, DateTime At)? _snapshot;
    private readonly object _lock = new();
    private bool _disposed;
    public string Url { get; }
    public static string EndpointPath => Path.Combine(Registry.Folder, "aura-endpoint.json");

    public AuraStatusServer(int port = 0, string? endpointFile = null, TimeSpan? maxAge = null)
    {
        _endpointFile = endpointFile ?? EndpointPath;
        _maxAge = maxAge ?? TimeSpan.FromSeconds(15);
        _listener = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
        _listener.Start(8);
        Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/v1/status";
        _discovery = JsonSerializer.Serialize(new Endpoint("localhost-dock/aura-endpoint@1", Url, _token, Environment.ProcessId), AuraStatus.Json);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_endpointFile))!);
            string temporary = _endpointFile + ".tmp";
            File.WriteAllText(temporary, _discovery);
            File.Move(temporary, _endpointFile, overwrite: true);
        }
        catch { _listener.Stop(); throw; }
        _loop = AcceptAsync();
    }

    public void Publish(Snapshot snapshot)
    {
        var status = AuraStatus.From(snapshot);
        lock (_lock) _snapshot = (JsonSerializer.SerializeToUtf8Bytes(status, AuraStatus.Json), status.GeneratedAt);
    }

    public void Invalidate() { lock (_lock) _snapshot = null; }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                if (!_slots.Wait(0)) { client.Dispose(); continue; }
                _ = ServeAsync(client);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                var stream = client.GetStream();
                byte[] buffer = new byte[8192];
                int length = 0;
                while (length < buffer.Length)
                {
                    int n = await stream.ReadAsync(buffer.AsMemory(length), deadline.Token);
                    if (n == 0) return;
                    length += n;
                    string header = Encoding.ASCII.GetString(buffer, 0, length);
                    int end = header.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (end < 0) continue;
                    var (code, body) = Handle(header[..end]);
                    await ReplyAsync(stream, code, body, deadline.Token);
                    return;
                }
                await ReplyAsync(stream, 431, "{}"u8.ToArray(), deadline.Token);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
            finally { _slots.Release(); }
        }
    }

    private (int, byte[]) Handle(string text)
    {
        var lines = text.Split("\r\n", StringSplitOptions.None);
        var request = lines[0].Split(' ');
        if (request.Length != 3 || request[2] != "HTTP/1.1") return Error(400);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0 || line[..colon].Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') ||
                !headers.TryAdd(line[..colon], line[(colon + 1)..].Trim())) return Error(400);
        }
        if (headers.GetValueOrDefault("Host") != new Uri(Url).Authority || headers.ContainsKey("Origin") ||
            headers.ContainsKey("Transfer-Encoding") || headers.GetValueOrDefault("Content-Length", "0") != "0") return Error(403);
        string supplied = headers.GetValueOrDefault("Authorization", "");
        byte[] expected = Encoding.ASCII.GetBytes("Bearer " + _token);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(supplied), expected)) return Error(401);
        if (request[0] != "GET") return Error(405);
        if (request[1] != "/v1/status") return Error(404);
        lock (_lock)
            return _snapshot is { } s && DateTime.UtcNow - s.At <= _maxAge
                ? (200, s.Json) : (503, "{\"error\":\"status-unavailable\"}"u8.ToArray());
    }

    private static (int, byte[]) Error(int code) => (code, "{}"u8.ToArray());

    private static Task ReplyAsync(NetworkStream stream, int code, byte[] body, CancellationToken ct)
    {
        string reason = code switch { 200 => "OK", 400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden",
            404 => "Not Found", 405 => "Method Not Allowed", 431 => "Request Header Fields Too Large", _ => "Service Unavailable" };
        byte[] header = Encoding.ASCII.GetBytes($"HTTP/1.1 {code} {reason}\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n");
        return WriteAsync();
        async Task WriteAsync()
        {
            await stream.WriteAsync(header, ct);
            await stream.WriteAsync(body, ct);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        _listener.Stop();
        try
        {
            if (File.Exists(_endpointFile) && File.ReadAllText(_endpointFile) == _discovery) File.Delete(_endpointFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
