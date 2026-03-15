using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StepSolve;

namespace StepSolve.Tests;

public class WebSocketBroadcasterTests
{
    [Fact]
    public void NewBroadcaster_HasNoClients()
    {
        var broadcaster = new WebSocketBroadcaster();
        Assert.Equal(0, broadcaster.ClientCount);
    }

    [Fact]
    public async Task BroadcastSolve_WithNoClients_DoesNotThrow()
    {
        var broadcaster = new WebSocketBroadcaster();
        var result = new SolveResult(100, 50, null, null, 0.95, TimeSpan.FromSeconds(2), "test");
        await broadcaster.BroadcastSolve(result);
    }

    [Fact]
    public async Task BroadcastStatus_WithNoClients_DoesNotThrow()
    {
        var broadcaster = new WebSocketBroadcaster();
        await broadcaster.BroadcastStatus("solve", "solving");
    }

    [Fact]
    public async Task BroadcastLog_WithNoClients_DoesNotThrow()
    {
        var broadcaster = new WebSocketBroadcaster();
        await broadcaster.BroadcastLog("INFO", "Test message");
    }

    private static (IHost Host, WebSocketBroadcaster Broadcaster) CreateTestServer()
    {
        var broadcaster = new WebSocketBroadcaster();
        var host = new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    services.AddSingleton(broadcaster);
                });
                webBuilder.Configure(app =>
                {
                    app.UseWebSockets();
                    app.Use(async (ctx, next) =>
                    {
                        if (ctx.Request.Path == "/ws" && ctx.WebSockets.IsWebSocketRequest)
                        {
                            var ws = await ctx.WebSockets.AcceptWebSocketAsync();
                            var b = ctx.RequestServices.GetRequiredService<WebSocketBroadcaster>();
                            await b.HandleAsync(ws, ctx.RequestAborted);
                        }
                        else
                        {
                            await next();
                        }
                    });
                });
            })
            .Build();

        return (host, broadcaster);
    }

    [Fact]
    public async Task BroadcastSolve_SendsCorrectJsonStructure()
    {
        var (host, broadcaster) = CreateTestServer();
        await host.StartAsync();
        try
        {
            var server = host.GetTestServer();
            var wsClient = server.CreateWebSocketClient();
            var ws = await wsClient.ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None);

            await Task.Delay(100);
            Assert.Equal(1, broadcaster.ClientCount);

            var result = new SolveResult(296.94, 42.69, 1.5, 2.1, 0.95, TimeSpan.FromMilliseconds(2340), "astrometry");
            await broadcaster.BroadcastSolve(result);

            var buffer = new byte[4096];
            var recv = await ws.ReceiveAsync(buffer, CancellationToken.None);
            Assert.Equal(WebSocketMessageType.Text, recv.MessageType);

            var json = Encoding.UTF8.GetString(buffer, 0, recv.Count);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.Equal("solve", root.GetProperty("type").GetString());
            Assert.Equal(296.94, root.GetProperty("ra").GetDouble(), 2);
            Assert.Equal(42.69, root.GetProperty("dec").GetDouble(), 2);
            Assert.Equal(0.95, root.GetProperty("confidence").GetDouble(), 2);
            Assert.Equal("astrometry", root.GetProperty("solver").GetString());
            Assert.Equal(2340, root.GetProperty("solveTimeMs").GetDouble(), 0);
            Assert.True(root.TryGetProperty("timestamp", out _));

            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task BroadcastStatus_SendsCorrectJsonStructure()
    {
        var (host, broadcaster) = CreateTestServer();
        await host.StartAsync();
        try
        {
            var server = host.GetTestServer();
            var wsClient = server.CreateWebSocketClient();
            var ws = await wsClient.ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None);

            await Task.Delay(100);

            await broadcaster.BroadcastStatus("demo", "solving");

            var buffer = new byte[4096];
            var recv = await ws.ReceiveAsync(buffer, CancellationToken.None);
            var json = Encoding.UTF8.GetString(buffer, 0, recv.Count);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.Equal("status", root.GetProperty("type").GetString());
            Assert.Equal("demo", root.GetProperty("mode").GetString());
            Assert.Equal("solving", root.GetProperty("state").GetString());
            Assert.True(root.GetProperty("uptime").GetInt32() >= 0);

            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task BroadcastLog_SendsCorrectJsonStructure()
    {
        var (host, broadcaster) = CreateTestServer();
        await host.StartAsync();
        try
        {
            var server = host.GetTestServer();
            var wsClient = server.CreateWebSocketClient();
            var ws = await wsClient.ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None);

            await Task.Delay(100);

            await broadcaster.BroadcastLog("ERROR", "Something went wrong");

            var buffer = new byte[4096];
            var recv = await ws.ReceiveAsync(buffer, CancellationToken.None);
            var json = Encoding.UTF8.GetString(buffer, 0, recv.Count);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.Equal("log", root.GetProperty("type").GetString());
            Assert.Equal("ERROR", root.GetProperty("level").GetString());
            Assert.Equal("Something went wrong", root.GetProperty("message").GetString());
            Assert.True(root.TryGetProperty("timestamp", out _));

            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task MultipleClients_AllReceiveBroadcast()
    {
        var (host, broadcaster) = CreateTestServer();
        await host.StartAsync();
        try
        {
            var server = host.GetTestServer();
            var wsClient = server.CreateWebSocketClient();
            var ws1 = await wsClient.ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None);
            var ws2 = await wsClient.ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None);

            await Task.Delay(100);
            Assert.Equal(2, broadcaster.ClientCount);

            await broadcaster.BroadcastLog("INFO", "hello");

            var buf1 = new byte[4096];
            var buf2 = new byte[4096];
            var recv1 = await ws1.ReceiveAsync(buf1, CancellationToken.None);
            var recv2 = await ws2.ReceiveAsync(buf2, CancellationToken.None);

            var json1 = Encoding.UTF8.GetString(buf1, 0, recv1.Count);
            var json2 = Encoding.UTF8.GetString(buf2, 0, recv2.Count);
            Assert.Contains("hello", json1);
            Assert.Contains("hello", json2);

            await ws1.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            await ws2.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task ClientDisconnect_RemovesFromCount()
    {
        var (host, broadcaster) = CreateTestServer();
        await host.StartAsync();
        try
        {
            var server = host.GetTestServer();
            var wsClient = server.CreateWebSocketClient();
            var ws = await wsClient.ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None);

            await Task.Delay(100);
            Assert.Equal(1, broadcaster.ClientCount);

            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);

            // Small delay for the server handler to process the close
            await Task.Delay(200);

            Assert.Equal(0, broadcaster.ClientCount);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }
}
