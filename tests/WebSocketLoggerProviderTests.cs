using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StepSolve;

namespace StepSolve.Tests;

public class WebSocketLoggerProviderTests
{
    [Fact]
    public void CreateLogger_ReturnsLogger()
    {
        var broadcaster = new WebSocketBroadcaster();
        var provider = new WebSocketLoggerProvider(broadcaster);

        var logger = provider.CreateLogger("StepSolve.Tests.SomeClass");
        Assert.NotNull(logger);
    }

    [Fact]
    public void Logger_IsEnabled_ForInfoAndAbove()
    {
        var broadcaster = new WebSocketBroadcaster();
        var provider = new WebSocketLoggerProvider(broadcaster);
        var logger = provider.CreateLogger("Test");

        Assert.True(logger.IsEnabled(LogLevel.Information));
        Assert.True(logger.IsEnabled(LogLevel.Warning));
        Assert.True(logger.IsEnabled(LogLevel.Error));
        Assert.True(logger.IsEnabled(LogLevel.Critical));
        Assert.False(logger.IsEnabled(LogLevel.Debug));
        Assert.False(logger.IsEnabled(LogLevel.Trace));
    }

    [Fact]
    public async Task Logger_BroadcastsLogEntry_ViaWebSocket()
    {
        var broadcaster = new WebSocketBroadcaster();
        var provider = new WebSocketLoggerProvider(broadcaster);

        // Set up a test WebSocket server
        var host = new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services => services.AddSingleton(broadcaster));
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
                        else await next();
                    });
                });
            })
            .Build();

        await host.StartAsync();
        try
        {
            var server = host.GetTestServer();
            var wsClient = server.CreateWebSocketClient();
            var ws = await wsClient.ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None);

            await Task.Delay(100);

            // Log a message through the provider
            var logger = provider.CreateLogger("StepSolve.MyService");
            logger.LogInformation("Solve completed successfully");

            // Read the broadcast message
            var buffer = new byte[4096];
            var recv = await ws.ReceiveAsync(buffer, CancellationToken.None);
            var json = Encoding.UTF8.GetString(buffer, 0, recv.Count);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.Equal("log", root.GetProperty("type").GetString());
            Assert.Equal("INFO", root.GetProperty("level").GetString());
            Assert.Contains("Solve completed successfully", root.GetProperty("message").GetString());
            Assert.Contains("MyService", root.GetProperty("message").GetString());

            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task Logger_MapsLogLevels_Correctly()
    {
        var broadcaster = new WebSocketBroadcaster();
        var provider = new WebSocketLoggerProvider(broadcaster);

        var host = new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services => services.AddSingleton(broadcaster));
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
                        else await next();
                    });
                });
            })
            .Build();

        await host.StartAsync();
        try
        {
            var server = host.GetTestServer();
            var wsClient = server.CreateWebSocketClient();
            var ws = await wsClient.ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None);
            await Task.Delay(100);

            var logger = provider.CreateLogger("Test");

            // Log a warning
            logger.LogWarning("Something unexpected");

            var buffer = new byte[4096];
            var recv = await ws.ReceiveAsync(buffer, CancellationToken.None);
            var json = Encoding.UTF8.GetString(buffer, 0, recv.Count);
            var doc = JsonDocument.Parse(json);
            Assert.Equal("WARNING", doc.RootElement.GetProperty("level").GetString());

            // Log an error
            logger.LogError("Something broke");

            recv = await ws.ReceiveAsync(buffer, CancellationToken.None);
            json = Encoding.UTF8.GetString(buffer, 0, recv.Count);
            doc = JsonDocument.Parse(json);
            Assert.Equal("ERROR", doc.RootElement.GetProperty("level").GetString());

            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }
}
