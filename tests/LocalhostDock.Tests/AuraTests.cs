using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using LocalhostDock.Config;
using LocalhostDock.Core;
using LocalhostDock.Integration;
using Xunit;

namespace LocalhostDock.Tests;

public class AuraTests
{
    [Fact]
    public void Occupied_status_port_never_stops_holder_or_replaces_discovery()
    {
        using var tp = new TestProcesses();
        int port = TestProcesses.FreePort();
        var holder = tp.Listen(port, tp.TempDir("occupied-status"));
        string file = Path.Combine(tp.TempDir("discovery"), "endpoint.json");
        File.WriteAllText(file, "existing-discovery");
        Assert.Throws<System.Net.Sockets.SocketException>(() => new AuraStatusServer(port, file));
        Assert.True(holder.IsAlive());
        Assert.Equal("existing-discovery", File.ReadAllText(file));
    }

    [Fact]
    public async Task Oversized_and_ambiguous_headers_are_rejected()
    {
        using var tp = new TestProcesses();
        string file = Path.Combine(tp.TempDir("headers"), "endpoint.json");
        using var server = new AuraStatusServer(endpointFile: file);
        var uri = new Uri(server.Url);
        async Task<string> Raw(string request)
        {
            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, uri.Port);
            await client.GetStream().WriteAsync(System.Text.Encoding.ASCII.GetBytes(request));
            using var reader = new StreamReader(client.GetStream());
            return (await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)))!;
        }
        Assert.Contains("400", await Raw($"GET /v1/status HTTP/1.1\r\nHost: {uri.Authority}\r\nHost: {uri.Authority}\r\n\r\n"));
        Assert.Contains("431", await Raw($"GET /v1/status HTTP/1.1\r\nHost: {uri.Authority}\r\nX-Huge: {new string('a', 9000)}"));
    }

    private static AuraStatusServer.Endpoint ReadEndpoint(string file) => JsonSerializer.Deserialize<AuraStatusServer.Endpoint>(File.ReadAllText(file),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    private static Snapshot Sample() => new(new[] { new AppStatus(new AppEntry { Id = "api", Name = "API", Port = 3000,
        Start = "echo secret-token", Cwd = @"C:\private", Url = "http://localhost/?password=secret", Health = "/health?token=secret" },
        AppState.Down, Array.Empty<OwnedListener>(), null, 0, null, "Secret exception C:\\private secret-token") },
        Array.Empty<WorkspaceStatus>(), Array.Empty<DiscoveredPort>(), Array.Empty<LocalhostDock.Net.Listener>());

    [Fact]
    public void Integration_defaults_off_and_invalid_ports_disable_it()
    {
        var registry = new Registry();
        Assert.False(registry.Aura.Enabled);
        registry.Aura.Enabled = true;
        registry.Aura.Port = -1;
        Assert.Contains(registry.Normalize(), n => n.Contains("Aura"));
        Assert.False(registry.Aura.Enabled);
    }

    [Fact]
    public async Task Authenticated_status_is_allowlisted_cached_and_read_only()
    {
        using var tp = new TestProcesses();
        string file = Path.Combine(tp.TempDir("aura"), "endpoint.json");
        using var server = new AuraStatusServer(endpointFile: file);
        var endpoint = ReadEndpoint(file);
        Assert.Equal("127.0.0.1", new Uri(endpoint.Url).Host);
        Assert.Matches("^[A-F0-9]{64}$", endpoint.Token);
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync(server.Url)).StatusCode);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await http.GetAsync(server.Url)).StatusCode);
        server.Publish(Sample());
        var response = await http.GetAsync(server.Url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("start", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(endpoint.Token, body);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await http.PostAsync(server.Url, null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(server.Url + "/start")).StatusCode);
        using var origin = new HttpRequestMessage(HttpMethod.Get, server.Url);
        origin.Headers.Add("Origin", "http://127.0.0.1:4780");
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(origin)).StatusCode);
        using var rebound = new HttpRequestMessage(HttpMethod.Get, server.Url);
        rebound.Headers.Host = "attacker.example";
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(rebound)).StatusCode);
        server.Invalidate();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await http.GetAsync(server.Url)).StatusCode);
    }

    [Fact]
    public async Task Stale_snapshots_fail_closed_and_restart_rotates_credentials()
    {
        using var tp = new TestProcesses();
        string file = Path.Combine(tp.TempDir("aura-restart"), "endpoint.json");
        string oldToken;
        using (var first = new AuraStatusServer(endpointFile: file, maxAge: TimeSpan.FromMilliseconds(50)))
        {
            oldToken = ReadEndpoint(file).Token;
            first.Publish(Sample());
            await Task.Delay(100);
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", oldToken);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await http.GetAsync(first.Url)).StatusCode);
        }
        Assert.False(File.Exists(file));
        using var second = new AuraStatusServer(endpointFile: file);
        Assert.NotEqual(oldToken, ReadEndpoint(file).Token);
        using var rejected = new HttpClient(new HttpClientHandler { UseProxy = false });
        rejected.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", oldToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await rejected.GetAsync(second.Url)).StatusCode);
    }

    [Fact]
    public async Task Node_adapter_reads_real_endpoint_without_leaking_discovery_credentials()
    {
        using var tp = new TestProcesses();
        string home = tp.TempDir("node-client");
        using var server = new AuraStatusServer(endpointFile: Path.Combine(home, "aura-endpoint.json"));
        server.Publish(Sample());
        string adapter = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../integrations/aura/localhost-dock.mjs"));
        var psi = new ProcessStartInfo("node") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(adapter);
        psi.Environment["LOCALHOST_DOCK_HOME"] = home;
        using var process = Process.Start(psi)!;
        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(process.ExitCode == 0, error);
        Assert.Contains("localhost-dock/aura-status@1", output);
        Assert.DoesNotContain(ReadEndpoint(Path.Combine(home, "aura-endpoint.json")).Token, output + error);
    }
}
