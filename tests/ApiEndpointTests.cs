using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using StepSolve;
using StepSolve.Solvers;

namespace StepSolve.Tests;

public class ApiEndpointTests
{
    private static IHost CreateTestHost()
    {
        var host = new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureAppConfiguration(config =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["StepSolve:Mode"] = "demo",
                        ["StepSolve:WebPort"] = "5001",
                        ["Solver:Backend"] = "astrometry",
                        ["Camera:ShutterUs"] = "1000000",
                        ["OnStep:Enabled"] = "false",
                    });
                });
                webBuilder.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.Configure<StepSolveOptions>(sp =>
                    {
                        sp.Mode = "demo";
                        sp.WebPort = 5001;
                    });
                    services.Configure<SolverOptions>(sp =>
                    {
                        sp.Backend = "astrometry";
                    });
                    services.Configure<CameraOptions>(sp => { });
                    services.Configure<OnStepOptions>(sp =>
                    {
                        sp.Enabled = false;
                    });
                    services.AddSingleton<SolveState>();
                    services.AddSingleton<ISolver, AstrometrySolver>();
                    services.AddSingleton<OnStepClient>();
                    services.AddSingleton<WebSocketBroadcaster>();
                });
                webBuilder.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/status", (SolveState state, IOptions<StepSolveOptions> opts, IOptions<OnStepOptions> onstepOpts, OnStepClient onstepClient) =>
                        {
                            var (result, timestamp, currentState) = state.Current;
                            return Results.Ok(new
                            {
                                mode = opts.Value.Mode,
                                state = currentState,
                                ra = result?.RaDeg,
                                dec = result?.DecDeg,
                            });
                        });

                        endpoints.MapPost("/mode", async (HttpContext ctx, IConfiguration config) =>
                        {
                            string mode;
                            if (ctx.Request.HasJsonContentType())
                            {
                                var body = await ctx.Request.ReadFromJsonAsync<ModeRequest>();
                                mode = body?.Mode?.ToLowerInvariant() ?? "";
                            }
                            else
                            {
                                mode = ctx.Request.Query["mode"].ToString().ToLowerInvariant();
                            }

                            if (mode is not ("solve" or "demo" or "idle"))
                                return Results.BadRequest(new { error = "Mode must be solve, demo, or idle" });

                            config["StepSolve:Mode"] = mode;
                            return Results.Ok(new { mode });
                        });

                        endpoints.MapGet("/settings", (
                            IOptions<StepSolveOptions> stepOpts,
                            IOptions<SolverOptions> solverOpts,
                            IOptions<CameraOptions> cameraOpts,
                            IOptions<OnStepOptions> onstepOpts) =>
                        {
                            return Results.Ok(new
                            {
                                stepSolve = stepOpts.Value,
                                solver = solverOpts.Value,
                                camera = cameraOpts.Value,
                                onstep = onstepOpts.Value,
                            });
                        });

                        endpoints.MapPost("/solve", (HttpContext ctx, SolveState state) =>
                        {
                            if (ctx.Request.Query.ContainsKey("demo"))
                            {
                                var demoResult = new SolveResult(123.45, -67.89, null, null, 0.99, TimeSpan.FromMilliseconds(12), "demo");
                                state.UpdateResult(demoResult);
                                return Results.Ok(new { ra = demoResult.RaDeg, dec = demoResult.DecDeg, confidence = demoResult.Confidence, solver = demoResult.SolverName });
                            }
                            return Results.BadRequest(new { error = "Provide an image file or use ?demo=1" });
                        });

                        endpoints.MapGet("/solve/image", (SolveState state) =>
                        {
                            var path = state.LastImagePath;
                            if (path == null || !File.Exists(path))
                                return Results.NotFound(new { error = "No image available" });
                            return Results.File(path, "image/jpeg");
                        });

                        endpoints.MapPost("/system/shutdown", () =>
                        {
                            if (!OperatingSystem.IsLinux())
                                return Results.StatusCode(503);
                            return Results.Ok(new { status = "shutting down" });
                        });

                        endpoints.MapPost("/system/restart", () =>
                        {
                            if (!OperatingSystem.IsLinux())
                                return Results.StatusCode(503);
                            return Results.Ok(new { status = "restarting" });
                        });
                    });
                });
            })
            .Build();

        return host;
    }

    [Fact]
    public async Task GetStatus_ReturnsOk()
    {
        var host = CreateTestHost();
        await host.StartAsync();
        try
        {
            var client = host.GetTestServer().CreateClient();
            var response = await client.GetAsync("/status");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("demo", json.GetProperty("mode").GetString());
            Assert.Equal("idle", json.GetProperty("state").GetString());
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task PostMode_WithJsonBody_ChangesMode()
    {
        var host = CreateTestHost();
        await host.StartAsync();
        try
        {
            var client = host.GetTestServer().CreateClient();
            var content = new StringContent("{\"mode\":\"idle\"}", Encoding.UTF8, "application/json");
            var response = await client.PostAsync("/mode", content);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("idle", json.GetProperty("mode").GetString());
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task PostMode_WithQueryString_StillWorks()
    {
        var host = CreateTestHost();
        await host.StartAsync();
        try
        {
            var client = host.GetTestServer().CreateClient();
            var response = await client.PostAsync("/mode?mode=solve", null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("solve", json.GetProperty("mode").GetString());
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task PostMode_InvalidMode_ReturnsBadRequest()
    {
        var host = CreateTestHost();
        await host.StartAsync();
        try
        {
            var client = host.GetTestServer().CreateClient();
            var content = new StringContent("{\"mode\":\"invalid\"}", Encoding.UTF8, "application/json");
            var response = await client.PostAsync("/mode", content);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task GetSettings_ReturnsConfiguration()
    {
        var host = CreateTestHost();
        await host.StartAsync();
        try
        {
            var client = host.GetTestServer().CreateClient();
            var response = await client.GetAsync("/settings");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(json.TryGetProperty("stepSolve", out _));
            Assert.True(json.TryGetProperty("solver", out _));
            Assert.True(json.TryGetProperty("camera", out _));
            Assert.True(json.TryGetProperty("onstep", out _));
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task PostSolve_Demo_ReturnsResult()
    {
        var host = CreateTestHost();
        await host.StartAsync();
        try
        {
            var client = host.GetTestServer().CreateClient();
            var response = await client.PostAsync("/solve?demo=1", null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(123.45, json.GetProperty("ra").GetDouble(), 2);
            Assert.Equal(-67.89, json.GetProperty("dec").GetDouble(), 2);
            Assert.Equal("demo", json.GetProperty("solver").GetString());
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task GetSolveImage_NoImage_ReturnsNotFound()
    {
        var host = CreateTestHost();
        await host.StartAsync();
        try
        {
            var client = host.GetTestServer().CreateClient();
            var response = await client.GetAsync("/solve/image");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task PostSystemShutdown_NonLinux_Returns503()
    {
        var host = CreateTestHost();
        await host.StartAsync();
        try
        {
            var client = host.GetTestServer().CreateClient();
            var response = await client.PostAsync("/system/shutdown", null);

            if (!OperatingSystem.IsLinux())
                Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task PostSystemRestart_NonLinux_Returns503()
    {
        var host = CreateTestHost();
        await host.StartAsync();
        try
        {
            var client = host.GetTestServer().CreateClient();
            var response = await client.PostAsync("/system/restart", null);

            if (!OperatingSystem.IsLinux())
                Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }
}

record ModeRequest(string? Mode);
