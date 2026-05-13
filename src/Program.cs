using StepSolve;
using StepSolve.Solvers;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Bind configuration sections
builder.Services.Configure<StepSolveOptions>(builder.Configuration.GetSection(StepSolveOptions.Section));
builder.Services.Configure<SolverOptions>(builder.Configuration.GetSection(SolverOptions.Section));
builder.Services.Configure<CameraOptions>(builder.Configuration.GetSection(CameraOptions.Section));
builder.Services.Configure<OnStepOptions>(builder.Configuration.GetSection(OnStepOptions.Section));

// Shared state — singleton, thread-safe
builder.Services.AddSingleton<SolveState>();

// Camera capture
builder.Services.AddSingleton<ICameraCapture, CameraCapture>();

// Solver — selected by Solver:Backend config (astrometry | cedar | tetra3)
var backend = builder.Configuration.GetValue<string>("Solver:Backend") ?? "astrometry";
SolverRegistration.Register(builder.Services, backend);

// OnStep client for mount sync
builder.Services.AddSingleton<OnStepClient>();

// WebSocket broadcaster for real-time dashboard updates
builder.Services.AddSingleton<WebSocketBroadcaster>();

// Settings service — validation and persistence to disk
builder.Services.AddSingleton<SettingsService>();

// Wire application logs to WebSocket for real-time dashboard log stream.
// AddFilter ensures Debug messages from StepSolve.* reach this provider even
// though the global minimum level in appsettings.json is Information.
builder.Services.AddSingleton<ILoggerProvider, WebSocketLoggerProvider>();
builder.Logging.AddFilter<WebSocketLoggerProvider>("StepSolve", LogLevel.Debug);

// Background solve loop
builder.Services.AddHostedService<StepSolveService>();

// LX200 TCP server for SkySafari
builder.Services.AddHostedService<Lx200Server>();

// Configure Kestrel to listen on the configured web port
builder.WebHost.ConfigureKestrel((context, options) =>
{
    var port = context.Configuration.GetValue("StepSolve:WebPort", 5001);
    options.ListenAnyIP(port);
});

var app = builder.Build();

app.UseWebSockets();
app.UseStaticFiles();

// GET /status — current solve state and configuration
app.MapGet("/status", (SolveState state, IOptions<StepSolveOptions> opts, IOptions<OnStepOptions> onstepOpts, OnStepClient onstepClient) =>
{
    var (result, timestamp, currentState) = state.Current;
    return Results.Ok(new
    {
        mode = opts.Value.Mode,
        state = currentState,
        ra = result?.RaDeg,
        dec = result?.DecDeg,
        confidence = result?.Confidence,
        solver = result?.SolverName,
        solveTimeMs = result?.SolveTime.TotalMilliseconds,
        lastSolveTimestamp = timestamp != default ? timestamp : (DateTimeOffset?)null,
        onstep = new
        {
            enabled = onstepOpts.Value.Enabled,
            host = onstepOpts.Value.Host,
            port = onstepOpts.Value.Port,
            lastSyncTimestamp = onstepClient.LastSyncTime != default ? onstepClient.LastSyncTime : (DateTimeOffset?)null,
            lastSyncResult = onstepClient.LastSyncResult,
        }
    });
});

// WebSocket endpoint — real-time solve/status/log stream
app.Map("/ws", async (HttpContext ctx, WebSocketBroadcaster broadcaster) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = 400;
        return;
    }
    var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    await broadcaster.HandleAsync(ws, ctx.RequestAborted);
});

// POST /mode — change operating mode (solve/demo/idle)
// Accepts JSON body { "mode": "solve" } per PRD, or query string for convenience
app.MapPost("/mode", async (HttpContext ctx, IConfiguration config) =>
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

// GET /settings — current configuration
app.MapGet("/settings", (
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

// POST /settings — validate, apply, and persist configuration changes
app.MapPost("/settings", async (HttpContext ctx, SettingsService settingsSvc) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, Dictionary<string, object>>>();
    if (body == null)
        return Results.BadRequest(new { error = "Expected JSON object with section keys" });

    var error = settingsSvc.ApplyAndPersist(body);
    if (error != null)
        return Results.BadRequest(new { error });

    return Results.Ok(new { updated = true });
});

// POST /solve — on-demand solve: demo pulse (?demo=1) or uploaded image
app.MapPost("/solve", async (HttpContext ctx, ISolver solver, SolveState state, WebSocketBroadcaster ws, OnStepClient onstep, IOptions<StepSolveOptions> opts) =>
{
    if (ctx.Request.Query.ContainsKey("demo"))
    {
        // Demo solve — generate a fake result
        var demoResult = new SolveResult(
            RaDeg: Random.Shared.NextDouble() * 360.0,
            DecDeg: Random.Shared.NextDouble() * 180.0 - 90.0,
            RollDeg: null,
            PlateScaleArcsecPerPx: null,
            Confidence: 0.99,
            SolveTime: TimeSpan.FromMilliseconds(12),
            SolverName: "demo"
        );
        state.UpdateResult(demoResult);
        _ = ws.BroadcastSolve(demoResult, hasImage: false);
        return Results.Ok(new
        {
            ra = demoResult.RaDeg,
            dec = demoResult.DecDeg,
            confidence = demoResult.Confidence,
            solver = demoResult.SolverName,
            solveTimeMs = demoResult.SolveTime.TotalMilliseconds,
            imageUrl = (string?)null,
        });
    }

    // Image upload solve
    if (!ctx.Request.HasFormContentType || ctx.Request.Form.Files.Count == 0)
        return Results.BadRequest(new { error = "Provide an image file or use ?demo=1" });

    var file = ctx.Request.Form.Files[0];
    var tempDir = Path.Combine(AppContext.BaseDirectory, "images");
    Directory.CreateDirectory(tempDir);
    var tempPath = Path.Combine(tempDir, $"upload_{DateTimeOffset.UtcNow.Ticks}.jpg");

    await using (var stream = File.Create(tempPath))
    {
        await file.CopyToAsync(stream, ctx.RequestAborted);
    }

    var result = await solver.SolveAsync(tempPath, hints: null, ctx.RequestAborted);
    if (result.IsValid)
    {
        state.UpdateResult(result, tempPath);
        _ = ws.BroadcastSolve(result, hasImage: true);
        _ = ws.BroadcastStatus(opts.Value.Mode, "solved", onstep);
    }

    return Results.Ok(new
    {
        ra = result.RaDeg,
        dec = result.DecDeg,
        confidence = result.Confidence,
        solver = result.SolverName,
        solveTimeMs = result.SolveTime.TotalMilliseconds,
        valid = result.IsValid,
        imageUrl = result.IsValid ? "/solve/image" : (string?)null,
    });
});

// GET /solve/image — last solved image
app.MapGet("/solve/image", (SolveState state) =>
{
    var path = state.LastImagePath;
    if (path == null || !File.Exists(path))
        return Results.NotFound(new { error = "No image available" });

    return Results.File(path, "image/jpeg");
});

// POST /system/shutdown — graceful shutdown (Linux only)
app.MapPost("/system/shutdown", (IHostApplicationLifetime lifetime) =>
{
    if (!OperatingSystem.IsLinux())
        return Results.StatusCode(503);

    lifetime.StopApplication();
    return Results.Ok(new { status = "shutting down" });
});

// POST /system/restart — graceful reboot (Linux only)
app.MapPost("/system/restart", () =>
{
    if (!OperatingSystem.IsLinux())
        return Results.StatusCode(503);

    // Schedule reboot then return response
    _ = Task.Run(async () =>
    {
        await Task.Delay(1000);
        System.Diagnostics.Process.Start("sudo", "reboot");
    });

    return Results.Ok(new { status = "restarting" });
});

// GET / — serve the dashboard (falls through to static files wwwroot/index.html)
app.MapFallbackToFile("index.html");

app.Run();

// Request DTOs
record ModeRequest(string? Mode);
