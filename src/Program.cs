using StepSolve;
using StepSolve.Solvers;
using Microsoft.Extensions.Options;
using System.Text.Json;

// Read version from file written by release.sh; "dev" for local builds that lack it.
var versionPath = Path.Combine(AppContext.BaseDirectory, "version.txt");
var currentVersion = File.Exists(versionPath)
    ? File.ReadAllText(versionPath).Trim()
    : "dev";

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// Runtime settings file — highest priority, overrides appsettings.Development.json.
// Written by SettingsService when the user saves from the dashboard.
builder.Configuration.AddJsonFile(
    Path.Combine(AppContext.BaseDirectory, "appsettings.runtime.json"),
    optional: true, reloadOnChange: true);

// Bind configuration sections
builder.Services.Configure<StepSolveOptions>(builder.Configuration.GetSection(StepSolveOptions.Section));
builder.Services.Configure<SolverOptions>(builder.Configuration.GetSection(SolverOptions.Section));
builder.Services.Configure<CameraOptions>(builder.Configuration.GetSection(CameraOptions.Section));
builder.Services.Configure<OnStepOptions>(builder.Configuration.GetSection(OnStepOptions.Section));

// Shared state — singleton, thread-safe
builder.Services.AddSingleton<SolveState>();

// HttpClient for GitHub Releases update checks
builder.Services.AddHttpClient();

// Camera capture
builder.Services.AddSingleton<ICameraCapture, CameraCapture>();

// Solver — routes to astrometry/cedar/tetra3 based on Solver:Backend at call time
SolverRegistration.Register(builder.Services);

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

// Check for updates once at startup (fire-and-forget; result cached in UpdateState).
// Suppressed for dev builds (version == "dev") and on non-Linux (no-op update anyway).
var updateState = new UpdateState(currentVersion);
if (currentVersion != "dev")
{
    var httpFactory = app.Services.GetRequiredService<IHttpClientFactory>();
    _ = updateState.CheckAsync(httpFactory.CreateClient());
}

// GET /status — current solve state and configuration
app.MapGet("/status", (SolveState state, IOptions<StepSolveOptions> opts, IOptions<OnStepOptions> onstepOpts, OnStepClient onstepClient) =>
{
    var (result, timestamp, currentState) = state.Current;
    return Results.Ok(new
    {
        version = currentVersion,
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

// GET /system/update/check — cached result from startup check (no per-request network call)
app.MapGet("/system/update/check", () => Results.Ok(updateState.ToResponse()));

// POST /system/update — download latest release, extract in-place, restart (Linux only)
app.MapPost("/system/update", async (HttpContext ctx) =>
{
    if (!OperatingSystem.IsLinux())
        return Results.StatusCode(503);

    var info = updateState.ToResponse();
    if (!info.HasUpdate || info.DownloadUrl == null)
        return Results.BadRequest(new { error = "No update available" });

    var tmpPath = Path.Combine(Path.GetTempPath(), $"stepsolve-update-{DateTimeOffset.UtcNow.Ticks}.tar.gz");
    var installDir = AppContext.BaseDirectory;
    var httpFactory = ctx.RequestServices.GetRequiredService<IHttpClientFactory>();

    _ = Task.Run(async () =>
    {
        try
        {
            using var client = httpFactory.CreateClient();
            // Follow GitHub redirects (release asset URLs redirect to CDN)
            client.DefaultRequestHeaders.Add("User-Agent", "StepSolve-Updater");
            using var response = await client.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            await using (var fs = File.Create(tmpPath))
                await response.Content.CopyToAsync(fs);

            // Validate: tarball must contain the stepsolve binary
            var check = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "tar",
                Arguments = $"-tzf {tmpPath}",
                RedirectStandardOutput = true,
                UseShellExecute = false,
            })!;
            var listing = await check.StandardOutput.ReadToEndAsync();
            await check.WaitForExitAsync();
            if (!listing.Contains("stepsolve"))
            {
                File.Delete(tmpPath);
                return;
            }

            // Extract over the install directory (safe on Linux — binary is already mapped into memory)
            var extract = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "tar",
                Arguments = $"-xzf {tmpPath} -C {installDir}",
                UseShellExecute = false,
            })!;
            await extract.WaitForExitAsync();
            File.Delete(tmpPath);

            await Task.Delay(500);
            System.Diagnostics.Process.Start("sudo", "systemctl restart stepsolve");
        }
        catch
        {
            if (File.Exists(tmpPath)) File.Delete(tmpPath);
        }
    });

    return Results.Ok(new { status = "update downloading, will restart shortly" });
});

// GET / — serve the dashboard (falls through to static files wwwroot/index.html)
app.MapFallbackToFile("index.html");

app.Run();

// Request DTOs
record ModeRequest(string? Mode);

// Update check state — populated once at startup, read by /system/update/check
sealed class UpdateState(string currentVersion)
{
    private volatile UpdateResponse _response = new(currentVersion, null, false, null);

    public UpdateResponse ToResponse() => _response;

    public async Task CheckAsync(HttpClient client)
    {
        const string ApiUrl = "https://api.github.com/repos/tomotvos/stepsolve/releases/latest";
        try
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "StepSolve-Updater");
            var json = await client.GetStringAsync(ApiUrl);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var latest = root.GetProperty("tag_name").GetString() ?? "";
            string? downloadUrl = null;
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? "";
                    if (name.Contains("arm64") && name.EndsWith(".tar.gz"))
                    {
                        downloadUrl = asset.GetProperty("browser_download_url").GetString();
                        break;
                    }
                }
            }

            var hasUpdate = latest != currentVersion && downloadUrl != null;
            _response = new UpdateResponse(currentVersion, latest, hasUpdate, downloadUrl);
        }
        catch
        {
            // Leave response as default (hasUpdate: false) if check fails (no internet, etc.)
        }
    }
}

record UpdateResponse(
    string CurrentVersion,
    string? LatestVersion,
    bool HasUpdate,
    string? DownloadUrl);
