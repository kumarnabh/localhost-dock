using System.IO.Pipes;
using System.Text;

namespace LocalhostDock.Integration;

/// <summary>
/// One dock per Windows user. A second launch (e.g. Explorer's "Add to Localhost Dock") forwards its
/// command-line arguments to the running instance over a named pipe and exits.
///
/// Hardening: the pipe only accepts connections from the same Windows user (PipeOptions.CurrentUserOnly, which
/// also stops another user from squatting the name), messages are capped at 8 KB with a read timeout, and only
/// known arguments survive <see cref="Sanitize"/>.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const int MaxMessageBytes = 8 * 1024;

    // Must be identical across processes (string.GetHashCode is randomized per process, so don't use it).
    private static readonly string Id = "LocalhostDock-" + string.Concat(Environment.UserName.Where(char.IsLetterOrDigit));
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cts = new();

    public bool IsFirst { get; }

    public SingleInstance()
    {
        _mutex = new Mutex(true, @"Local\" + Id, out bool first);
        IsFirst = first;
    }

    /// <summary>Sends args to the running instance. Returns false if it couldn't be reached.</summary>
    public static bool Forward(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", Id, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(2000);
            byte[] payload = Encoding.UTF8.GetBytes(string.Join("\n", Sanitize(args)));
            if (payload.Length > MaxMessageBytes) return false;
            client.Write(payload, 0, payload.Length);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Listens for forwarded args; <paramref name="onArgs"/> is invoked on a background thread.</summary>
    public void Listen(Action<string[]> onArgs)
    {
        Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(Id, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(_cts.Token);

                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(3));
                    var buffer = new byte[MaxMessageBytes + 1];
                    int total = 0;
                    while (total < buffer.Length)
                    {
                        int n = await server.ReadAsync(buffer.AsMemory(total), timeout.Token);
                        if (n == 0) break;
                        total += n;
                    }
                    if (total > MaxMessageBytes) continue; // oversized: drop it

                    var args = Sanitize(Encoding.UTF8.GetString(buffer, 0, total).Split('\n', StringSplitOptions.RemoveEmptyEntries));
                    if (args.Length > 0) onArgs(args);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested) { break; }
                catch { await Task.Delay(250); }
            }
        });
    }

    /// <summary>Keeps only "--show", "--exit" and "--add &lt;existing file or folder&gt;"; everything else is dropped.</summary>
    public static string[] Sanitize(IEnumerable<string> raw)
    {
        var input = raw.Select(a => a.Trim()).Where(a => a.Length is > 0 and < 1024).ToList();
        var output = new List<string>();
        for (int i = 0; i < input.Count; i++)
        {
            if (input[i].Equals("--show", StringComparison.OrdinalIgnoreCase)) output.Add("--show");
            else if (input[i].Equals("--exit", StringComparison.OrdinalIgnoreCase)) output.Add("--exit");
            else if (input[i].Equals("--add", StringComparison.OrdinalIgnoreCase) && i + 1 < input.Count)
            {
                string path = input[++i].Trim('"');
                if (path.IndexOfAny(Path.GetInvalidPathChars()) < 0 && Path.IsPathFullyQualified(path) && (File.Exists(path) || Directory.Exists(path)))
                {
                    output.Add("--add");
                    output.Add(path);
                }
            }
        }
        return output.ToArray();
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (IsFirst) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
