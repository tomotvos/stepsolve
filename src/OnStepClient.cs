using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace StepSolve;

/// <summary>
/// TCP client that syncs solved coordinates to an OnStepX mount controller.
/// Sends :Sr, :Sd, :CM# after each successful solve.
/// Includes safety threshold to prevent wild jumps from faulty solves.
/// </summary>
public sealed class OnStepClient
{
    private readonly IOptionsMonitor<OnStepOptions> _options;
    private readonly ILogger<OnStepClient> _logger;

    private DateTimeOffset _lastSyncTime;
    private string? _lastSyncResult;

    // Retry backoff state
    private int _consecutiveFailures;
    private static readonly int[] BackoffSeconds = [1, 2, 4, 8, 16, 30];

    public OnStepClient(IOptionsMonitor<OnStepOptions> options, ILogger<OnStepClient> logger)
    {
        _options = options;
        _logger = logger;
    }

    public DateTimeOffset LastSyncTime => _lastSyncTime;
    public string? LastSyncResult => _lastSyncResult;

    /// <summary>
    /// Sync the solved coordinates to OnStep, if enabled and within safety threshold.
    /// This method does not throw — errors are logged and tracked.
    /// </summary>
    public async Task SyncAsync(SolveResult result, SolveState state, CancellationToken ct)
    {
        var opts = _options.CurrentValue;
        if (!opts.Enabled)
            return;

        // Safety check: if we have a previous position, verify the delta isn't too large
        var (prevRa, prevDec) = state.GetCoordinates();
        if (prevRa != 0.0 || prevDec != 0.0)
        {
            var delta = AngularDistance(prevRa, prevDec, result.RaDeg, result.DecDeg);
            if (delta > opts.MaxSyncDeltaDeg)
            {
                _logger.LogWarning(
                    "OnStep sync skipped: angular delta {Delta:F2}° exceeds threshold {Max:F1}°. " +
                    "Previous: ({PrevRa:F4}, {PrevDec:F4}), Solved: ({Ra:F4}, {Dec:F4})",
                    delta, opts.MaxSyncDeltaDeg, prevRa, prevDec, result.RaDeg, result.DecDeg);
                _lastSyncResult = $"skipped: delta {delta:F2}° > {opts.MaxSyncDeltaDeg:F1}°";
                return;
            }
        }

        try
        {
            await SendSyncCommands(opts, result, ct);
            _consecutiveFailures = 0;
            _lastSyncTime = DateTimeOffset.UtcNow;
            _lastSyncResult = "ok";
            _logger.LogInformation("OnStep sync: RA={Ra:F4}° Dec={Dec:F4}°", result.RaDeg, result.DecDeg);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _consecutiveFailures++;
            var backoff = BackoffSeconds[Math.Min(_consecutiveFailures - 1, BackoffSeconds.Length - 1)];
            _lastSyncResult = $"error: {ex.Message}";
            _logger.LogError(ex, "OnStep sync failed (attempt {N}, next retry in {Backoff}s)",
                _consecutiveFailures, backoff);
        }
    }

    internal async Task SendSyncCommands(OnStepOptions opts, SolveResult result, CancellationToken ct)
    {
        using var client = new TcpClient();
        client.SendTimeout = 3000;
        client.ReceiveTimeout = 3000;

        await client.ConnectAsync(opts.Host, opts.Port, ct);
        using var stream = client.GetStream();

        var raCmd = $":Sr{FormatRa(result.RaDeg)}#";
        var decCmd = $":Sd{FormatDec(result.DecDeg)}#";
        var cmCmd = ":CM#";

        await SendCommand(stream, raCmd, ct);
        await SendCommand(stream, decCmd, ct);
        await SendCommand(stream, cmCmd, ct);
    }

    private static async Task SendCommand(NetworkStream stream, string command, CancellationToken ct)
    {
        var bytes = Encoding.ASCII.GetBytes(command);
        await stream.WriteAsync(bytes, ct);
    }

    /// <summary>
    /// Compute angular distance between two sky positions in degrees.
    /// Uses the Vincenty formula for numerical stability near poles and antipodes.
    /// </summary>
    internal static double AngularDistance(double ra1, double dec1, double ra2, double dec2)
    {
        var ra1Rad = ra1 * Math.PI / 180.0;
        var dec1Rad = dec1 * Math.PI / 180.0;
        var ra2Rad = ra2 * Math.PI / 180.0;
        var dec2Rad = dec2 * Math.PI / 180.0;
        var dRa = ra2Rad - ra1Rad;

        var sinDec1 = Math.Sin(dec1Rad);
        var cosDec1 = Math.Cos(dec1Rad);
        var sinDec2 = Math.Sin(dec2Rad);
        var cosDec2 = Math.Cos(dec2Rad);

        var a = cosDec2 * Math.Sin(dRa);
        var b = cosDec1 * sinDec2 - sinDec1 * cosDec2 * Math.Cos(dRa);
        var c = sinDec1 * sinDec2 + cosDec1 * cosDec2 * Math.Cos(dRa);

        return Math.Atan2(Math.Sqrt(a * a + b * b), c) * 180.0 / Math.PI;
    }

    /// <summary>
    /// RA degrees → HH:MM:SS (same format as LX200 server)
    /// </summary>
    internal static string FormatRa(double raDeg)
    {
        var hours = raDeg / 15.0;
        var h = (int)hours;
        var m = (int)((hours - h) * 60);
        var s = (int)(((hours - h) * 60 - m) * 60);
        return $"{h:D2}:{m:D2}:{s:D2}";
    }

    /// <summary>
    /// Dec degrees → ±DD*MM:SS (same format as LX200 server)
    /// </summary>
    internal static string FormatDec(double decDeg)
    {
        var sign = decDeg >= 0 ? "+" : "-";
        var v = Math.Abs(decDeg);
        var d = (int)v;
        var m = (int)((v - d) * 60);
        var s = (int)(((v - d) * 60 - m) * 60);
        return $"{sign}{d:D2}*{m:D2}:{s:D2}";
    }
}
