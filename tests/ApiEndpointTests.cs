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
                webBuilder.ConfigureServices((context, services) =>
                {
                    services.AddRouting();
                    services.Configure<StepSolveOptions>(context.Configuration.GetSection(StepSolveOptions.Section));
                    services.Configure<SolverOptions>(context.Configuration.GetSection(SolverOptions.Section));
                    services.Configure<CameraOptions>(context.Configuration.GetSection(CameraOptions.Section));
                    services.Configure<OnStepOptions>(context.Configuration.GetSection(OnStepOptions.Section));
                    services.AddSingleton<SolveState>();
                    services.AddSingleton<ISolver, AstrometrySolver>();
                    services.AddSingleton<OnStepClient>();
                    services.AddSingleton<IOnStepCalibrationController, TestCalibrationController>();
                    services.AddSingleton<WebSocketBroadcaster>();
                    services.AddSingleton<SettingsService>();
                });
                webBuilder.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/status", (SolveState state, IConfiguration config, IOptionsMonitor<OnStepOptions> onstepOpts, OnStepClient onstepClient, IOnStepCalibrationController calibrationController) =>
                        {
                            var (result, timestamp, currentState) = state.Current;
                            return Results.Ok(new
                            {
                                mode = (config["StepSolve:Mode"] ?? "demo").ToLowerInvariant(),
                                state = currentState,
                                ra = result?.RaDeg,
                                dec = result?.DecDeg,
                                onstep = new { calibration = calibrationController.Status },
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

                            if (mode is not ("solve" or "demo" or "idle" or "calibrate"))
                                return Results.BadRequest(new { error = "Mode must be solve, demo, idle, or calibrate" });

                            config["StepSolve:Mode"] = mode;
                            return Results.Ok(new { mode });
                        });

                        endpoints.MapGet("/settings", (
                            IOptionsMonitor<StepSolveOptions> stepOpts,
                            IOptionsMonitor<SolverOptions> solverOpts,
                            IOptionsMonitor<CameraOptions> cameraOpts,
                            IOptionsMonitor<OnStepOptions> onstepOpts) =>
                        {
                            return Results.Ok(new
                            {
                                stepSolve = stepOpts.CurrentValue,
                                solver = solverOpts.CurrentValue,
                                camera = cameraOpts.CurrentValue,
                                onstep = onstepOpts.CurrentValue,
                            });
                        });

                        endpoints.MapPost("/solve", (HttpContext ctx, SolveState state) =>
                        {
                            if (ctx.Request.Query.ContainsKey("demo"))
                            {
                                var demoResult = new SolveResult(123.45, -67.89, null, null, 0.99, TimeSpan.FromMilliseconds(12), "demo");
                                state.UpdateResult(demoResult);
                                return Results.Ok(new { ra = demoResult.RaDeg, dec = demoResult.DecDeg, confidence = demoResult.Confidence, solver = demoResult.SolverName, imageUrl = state.LastImagePath != null ? "/solve/image" : (string?)null });
                            }
                            return Results.BadRequest(new { error = "Provide an image file or use ?demo=1" });
                        });

                        endpoints.MapPost("/settings", async (HttpContext ctx, SettingsService settingsSvc) =>
                        {
                            var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, Dictionary<string, object>>>();
                            if (body == null)
                                return Results.BadRequest(new { error = "Expected JSON object with section keys" });

                            var error = settingsSvc.ApplyAndPersist(body);
                            if (error != null)
                                return Results.BadRequest(new { error });

                            return Results.Ok(new { updated = true });
                        });

                        endpoints.MapGet("/onstep/calibration", (IOnStepCalibrationController controller) =>
                            Results.Ok(controller.Status));

                        endpoints.MapPost("/onstep/calibration/reconnect", async (IConfiguration config, IOnStepCalibrationController controller, HttpContext ctx) =>
                        {
                            var result = await controller.ReconnectAsync((config["StepSolve:Mode"] ?? "demo").ToLowerInvariant(), ctx.RequestAborted);
                            return result.Success
                                ? Results.Ok(new { calibration = controller.Status })
                                : Results.BadRequest(new { error = result.Error, calibration = controller.Status });
                        });

                        endpoints.MapPost("/onstep/alignment/start", async (StartAlignmentRequest? request, IConfiguration config, IOnStepCalibrationController controller, HttpContext ctx) =>
                        {
                            if (request?.Confirmed != true)
                                return Results.BadRequest(new { error = "Starting alignment requires explicit confirmation" });

                            var homeStrategy = request.HomeStrategy?.ToLowerInvariant() switch
                            {
                                "at-home" => CalibrationHomeStrategy.AtHome,
                                "return-home" => CalibrationHomeStrategy.ReturnToHome,
                                "recover-home" => CalibrationHomeStrategy.RecoverHome,
                                _ => (CalibrationHomeStrategy?)null,
                            };
                            if (homeStrategy == null)
                                return Results.BadRequest(new { error = "Choose a Home strategy" });

                            var result = await controller.StartAsync(request.Confirmed, homeStrategy.Value, (config["StepSolve:Mode"] ?? "demo").ToLowerInvariant(), ctx.RequestAborted);
                            return result.Success
                                ? Results.Ok(new { calibration = controller.Status })
                                : Results.BadRequest(new { error = result.Error, calibration = controller.Status });
                        });

                        endpoints.MapPost("/onstep/alignment/accept", async (IConfiguration config, IOnStepCalibrationController controller, HttpContext ctx) =>
                        {
                            var result = await controller.AcceptAsync((config["StepSolve:Mode"] ?? "demo").ToLowerInvariant(), ctx.RequestAborted);
                            return result.Success
                                ? Results.Ok(new { calibration = controller.Status })
                                : Results.BadRequest(new { error = result.Error, calibration = controller.Status });
                        });

                        endpoints.MapPost("/onstep/alignment/abort", async (IConfiguration config, IOnStepCalibrationController controller, HttpContext ctx) =>
                        {
                            var result = await controller.AbortAsync((config["StepSolve:Mode"] ?? "demo").ToLowerInvariant(), ctx.RequestAborted);
                            return result.Success
                                ? Results.Ok(new { calibration = controller.Status })
                                : Results.BadRequest(new { error = result.Error, calibration = controller.Status });
                        });

                        endpoints.MapPost("/onstep/calibration/simulation", async (SimulationRequest? request, IConfiguration config, IOnStepCalibrationController controller, HttpContext ctx) =>
                        {
                            if (request == null)
                                return Results.BadRequest(new { error = "Expected a simulation enabled value" });

                            var result = await controller.SetSimulationAsync(request.Enabled, (config["StepSolve:Mode"] ?? "demo").ToLowerInvariant(), ctx.RequestAborted);
                            return result.Success
                                ? Results.Ok(new { calibration = controller.Status })
                                : Results.BadRequest(new { error = result.Error, calibration = controller.Status });
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
    public async Task GetOnStepCalibration_ReturnsControllerStatus()
    {
        var host = CreateTestHost();
        await host.StartAsync();
        try
        {
            var client = host.GetTestServer().CreateClient();
            var response = await client.GetAsync("/onstep/calibration");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("idle", json.GetProperty("state").GetString());
            Assert.True(json.GetProperty("isConnected").GetBoolean());
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task PostOnStepAlignmentStart_RequiresExplicitConfirmation()
    {
        var host = CreateTestHost();
        await host.StartAsync();
        try
        {
            var client = host.GetTestServer().CreateClient();
            var response = await client.PostAsync("/onstep/alignment/start", new StringContent("{}", Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Contains("explicit confirmation", json.GetProperty("error").GetString());
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task PostOnStepAlignmentStart_RequiresHomeStrategy()
    {
        var host = CreateTestHost();
        await host.StartAsync();
        try
        {
            var client = host.GetTestServer().CreateClient();
            await client.PostAsync("/mode", new StringContent("{\"mode\":\"calibrate\"}", Encoding.UTF8, "application/json"));

            var response = await client.PostAsync("/onstep/alignment/start",
                new StringContent("{\"confirmed\":true}", Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Contains("Home strategy", json.GetProperty("error").GetString());
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task PostOnStepAlignmentStart_InCalibrateMode_ReturnsUpdatedStatus()
    {
        var host = CreateTestHost();
        await host.StartAsync();
        try
        {
            var client = host.GetTestServer().CreateClient();
            var setMode = await client.PostAsync("/mode", new StringContent("{\"mode\":\"calibrate\"}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, setMode.StatusCode);

            var response = await client.PostAsync("/onstep/alignment/start", new StringContent("{\"confirmed\":true,\"homeStrategy\":\"at-home\"}", Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("moving", json.GetProperty("calibration").GetProperty("state").GetString());
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task PostOnStepSimulation_IsSessionOnlyAndRequiresCalibrateMode()
    {
        var host = CreateTestHost();
        await host.StartAsync();
        try
        {
            var client = host.GetTestServer().CreateClient();
            // The dashboard has already fetched status before the user changes
            // modes, which previously left the API guard on a cached idle option.
            await client.GetAsync("/status");

            var payload = new StringContent("{\"enabled\":true}", Encoding.UTF8, "application/json");
            var rejected = await client.PostAsync("/onstep/calibration/simulation", payload);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

            var setMode = await client.PostAsync("/mode", new StringContent("{\"mode\":\"calibrate\"}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, setMode.StatusCode);
            var accepted = await client.PostAsync("/onstep/calibration/simulation", new StringContent("{\"enabled\":true}", Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            var json = await accepted.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(json.GetProperty("calibration").GetProperty("simulationEnabled").GetBoolean());
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
    public async Task GetSettings_ReturnsReloadedOnStepConfiguration()
    {
        var host = CreateTestHost();
        await host.StartAsync();
        try
        {
            var configuration = host.Services.GetRequiredService<IConfiguration>();
            configuration["OnStep:Enabled"] = "true";
            configuration["OnStep:Host"] = "192.168.4.1";
            host.Services.GetRequiredService<IOptionsMonitorCache<OnStepOptions>>().TryRemove(Options.DefaultName);

            var client = host.GetTestServer().CreateClient();
            var response = await client.GetAsync("/settings");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            var onstep = json.GetProperty("onstep");
            Assert.True(onstep.GetProperty("enabled").GetBoolean());
            Assert.Equal("192.168.4.1", onstep.GetProperty("host").GetString());
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

    [Fact]
    public async Task PostSettings_ValidSettings_ReturnsOk()
    {
        var host = CreateTestHost();
        await host.StartAsync();
        try
        {
            var client = host.GetTestServer().CreateClient();
            var content = new StringContent(
                "{\"Solver\":{\"Backend\":\"cedar\"}}",
                Encoding.UTF8, "application/json");
            var response = await client.PostAsync("/settings", content);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(json.GetProperty("updated").GetBoolean());
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task PostSettings_InvalidBackend_ReturnsBadRequest()
    {
        var host = CreateTestHost();
        await host.StartAsync();
        try
        {
            var client = host.GetTestServer().CreateClient();
            var content = new StringContent(
                "{\"Solver\":{\"Backend\":\"invalid\"}}",
                Encoding.UTF8, "application/json");
            var response = await client.PostAsync("/settings", content);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(json.TryGetProperty("error", out _));
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task PostSettings_NegativeShutter_ReturnsBadRequest()
    {
        var host = CreateTestHost();
        await host.StartAsync();
        try
        {
            var client = host.GetTestServer().CreateClient();
            var content = new StringContent(
                "{\"Camera\":{\"ShutterUs\":\"-100\"}}",
                Encoding.UTF8, "application/json");
            var response = await client.PostAsync("/settings", content);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }
}

record ModeRequest(string? Mode);
record StartAlignmentRequest(bool Confirmed, string? HomeStrategy);
record SimulationRequest(bool Enabled);

sealed class TestCalibrationController : IOnStepCalibrationController
{
    private OnStepCalibrationStatus _status = new(
        State: "idle",
        IsConnected: true,
        IsSafe: true,
        SimulationEnabled: false,
        Message: "Ready to align",
        CurrentPoint: 0,
        Attempt: 0,
        RequestedAzimuthDeg: null,
        RequestedAltitudeDeg: null,
        CandidateRaDeg: null,
        CandidateDecDeg: null,
        LastReply: null);

    public OnStepCalibrationStatus Status => _status;

    public Task<CalibrationActionResult> StartAsync(bool confirmed, CalibrationHomeStrategy homeStrategy, string currentMode, CancellationToken ct)
    {
        if (!confirmed)
            return Task.FromResult(new CalibrationActionResult(false, "Confirmation required"));
        if (!string.Equals(currentMode, "calibrate", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(new CalibrationActionResult(false, "Calibrate mode is required"));

        _status = _status with { State = "moving", CurrentPoint = 1, RequestedAzimuthDeg = 0, RequestedAltitudeDeg = 45 };
        return Task.FromResult(new CalibrationActionResult(true, null));
    }

    public Task<CalibrationActionResult> AcceptAsync(string currentMode, CancellationToken ct) =>
        Task.FromResult(new CalibrationActionResult(false, "No candidate is ready"));

    public Task<CalibrationActionResult> AbortAsync(string currentMode, CancellationToken ct)
    {
        _status = _status with { State = "aborted", Message = "Alignment aborted" };
        return Task.FromResult(new CalibrationActionResult(true, null));
    }

    public Task<CalibrationActionResult> ReconnectAsync(string currentMode, CancellationToken ct)
    {
        if (!string.Equals(currentMode, "calibrate", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(new CalibrationActionResult(false, "Calibrate mode is required"));

        _status = _status with { State = "idle", IsConnected = false, IsSafe = false, Message = "Reconnect requested" };
        return Task.FromResult(new CalibrationActionResult(true, null));
    }

    public Task<CalibrationActionResult> SetSimulationAsync(bool enabled, string currentMode, CancellationToken ct)
    {
        if (!string.Equals(currentMode, "calibrate", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(new CalibrationActionResult(false, "Calibrate mode is required"));

        _status = _status with { SimulationEnabled = enabled };
        return Task.FromResult(new CalibrationActionResult(true, null));
    }
}
