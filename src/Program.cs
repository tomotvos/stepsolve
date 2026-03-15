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

// Solver — currently only astrometry, will add Cedar/Tetra3 later
builder.Services.AddSingleton<ISolver, AstrometrySolver>();

// OnStep client for mount sync
builder.Services.AddSingleton<OnStepClient>();

// WebSocket broadcaster for real-time dashboard updates
builder.Services.AddSingleton<WebSocketBroadcaster>();

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
app.MapPost("/mode", (HttpContext ctx, IConfiguration config) =>
{
    var mode = ctx.Request.Query["mode"].ToString().ToLowerInvariant();
    if (mode is not ("solve" or "demo" or "idle"))
        return Results.BadRequest(new { error = "Mode must be solve, demo, or idle" });

    config["StepSolve:Mode"] = mode;
    return Results.Ok(new { mode });
});

// GET / — serve the dashboard (falls through to static files wwwroot/index.html)
app.MapFallbackToFile("index.html");

app.Run();
