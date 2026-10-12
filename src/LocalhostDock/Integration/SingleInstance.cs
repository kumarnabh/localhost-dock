using System.IO.Pipes;
using System.Text;

namespace LocalhostDock.Integration;

/// <summary>
/// One dock per Windows user. A second launch (e.g. Explorer's "Add to Localhost Dock") forwards its
/// command-line arguments to the running instance over a named pipe and exits.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
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
            using var client = new NamedPipeClientStream(".", Id, PipeDirection.Out);
            client.Connect(2000);
            byte[] payload = Encoding.UTF8.GetBytes(string.Join("\n", args));
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
                    using var server = new NamedPipeServerStream(Id, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(_cts.Token);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    string text = await reader.ReadToEndAsync(_cts.Token);
                    onArgs(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
                }
                catch (OperationCanceledException) { break; }
                catch { await Task.Delay(500); }
            }
        });
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (IsFirst) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
