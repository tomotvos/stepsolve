using System.Net.Sockets;
using System.Text;
using System.Globalization;
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
    private readonly SemaphoreSlim _protocolGate = new(1, 1);

    private DateTimeOffset _lastSyncTime;
    private string? _lastSyncResult;

    // Track last successfully synced position for safety threshold comparison
    private double _lastSyncedRa;
    private double _lastSyncedDec;
    private bool _hasSyncedBefore;

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
    /// Compares against the last successfully synced position (not shared state) to
    /// correctly detect large jumps from faulty solves.
    /// This method does not throw — errors are logged and tracked.
    /// </summary>
    public async Task SyncAsync(SolveResult result, CancellationToken ct)
    {
        var opts = _options.CurrentValue;
        if (!opts.Enabled)
            return;

        // Safety check: if we have a previous synced position, verify the delta isn't too large
        if (_hasSyncedBefore)
        {
            var delta = AngularDistance(_lastSyncedRa, _lastSyncedDec, result.RaDeg, result.DecDeg);
            if (delta > opts.MaxSyncDeltaDeg)
            {
                _logger.LogWarning(
                    "OnStep sync skipped: angular delta {Delta:F2}° exceeds threshold {Max:F1}°. " +
                    "Previous sync: ({PrevRa:F4}, {PrevDec:F4}), Solved: ({Ra:F4}, {Dec:F4})",
                    delta, opts.MaxSyncDeltaDeg, _lastSyncedRa, _lastSyncedDec, result.RaDeg, result.DecDeg);
                _lastSyncResult = $"skipped: delta {delta:F2}° > {opts.MaxSyncDeltaDeg:F1}°";
                return;
            }
        }

        try
        {
            var sync = await SyncSolvedPositionAsync(result, ct);
            if (!sync.Succeeded)
                throw new InvalidOperationException(sync.Error ?? $"OnStep rejected {sync.Command}: {sync.Response}");
            _consecutiveFailures = 0;
            _lastSyncTime = DateTimeOffset.UtcNow;
            _lastSyncResult = "ok";
            _lastSyncedRa = result.RaDeg;
            _lastSyncedDec = result.DecDeg;
            _hasSyncedBefore = true;
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

    /// <summary>
    /// Queries the controller identity using the standard LX200 version commands.
    /// </summary>
    public Task<OnStepIdentity> ProbeAsync(CancellationToken ct) =>
        WithConnectionAsync(async (stream, token) =>
        {
            var product = await SendAndReadHashReplyAsync(stream, ":GVP#", token);
            var firmware = await SendAndReadHashReplyAsync(stream, ":GVN#", token);
            return new OnStepIdentity(product, firmware);
        }, ct);

    /// <summary>
    /// Reads OnStep's packed mount status (<c>:GU#</c>).
    /// </summary>
    public Task<OnStepMountStatus> GetStatusAsync(CancellationToken ct) =>
        WithConnectionAsync(async (stream, token) =>
            new OnStepMountStatus(await SendAndReadHashReplyAsync(stream, ":GU#", token)), ct);

    /// <summary>
    /// Reads the current equatorial position from the controller.
    /// </summary>
    public Task<OnStepPosition> GetPositionAsync(CancellationToken ct) =>
        WithConnectionAsync(async (stream, token) =>
        {
            var ra = await SendAndReadHashReplyAsync(stream, ":GR#", token);
            var dec = await SendAndReadHashReplyAsync(stream, ":GD#", token);
            return new OnStepPosition(ParseRa(ra), ParseDec(dec));
        }, ct);

    /// <summary>
    /// Reads the current manual-alignment sequence progress (<c>:A?#</c>).
    /// </summary>
    public Task<OnStepAlignmentProgress> GetAlignmentProgressAsync(CancellationToken ct) =>
        WithConnectionAsync(async (stream, token) =>
        {
            var reply = await SendAndReadHashReplyAsync(stream, ":A?#", token);
            if (reply.Length != 3 || reply.Any(c => c < '0' || c > ':'))
                throw new InvalidDataException($"Unexpected OnStep alignment status reply '{reply}'.");

            return new OnStepAlignmentProgress(reply[0] - '0', reply[1] - '0', reply[2] - '0');
        }, ct);

    /// <summary>
    /// Starts an OnStep manual multi-star alignment sequence.
    /// </summary>
    public Task<OnStepCommandResult> StartAlignmentAsync(int starCount, CancellationToken ct)
    {
        if (starCount is < 1 or > 9)
            throw new ArgumentOutOfRangeException(nameof(starCount), "OnStep alignment requires 1 to 9 stars.");

        var command = $":A{starCount}#";
        return WithConnectionAsync(async (stream, token) =>
        {
            var reply = await SendAndReadSingleByteReplyAsync(stream, command, token);
            return reply == "1"
                ? OnStepCommandResult.Success(command, reply)
                : OnStepCommandResult.Failure(command, reply, "OnStep did not start the alignment sequence.");
        }, ct);
    }

    /// <summary>
    /// Sets an Alt/Az target and asks OnStep to slew to it. A successful result means the
    /// controller accepted the goto; completion must be determined by polling <c>:GU#</c>.
    /// </summary>
    public Task<OnStepCommandResult> GotoAltAzAsync(double altitudeDeg, double azimuthDeg, CancellationToken ct)
    {
        if (altitudeDeg is < -90 or > 90)
            throw new ArgumentOutOfRangeException(nameof(altitudeDeg), "Altitude must be between -90° and 90°.");
        if (double.IsNaN(azimuthDeg) || double.IsInfinity(azimuthDeg))
            throw new ArgumentOutOfRangeException(nameof(azimuthDeg));

        var altitudeCommand = $":Sa{FormatAltitude(altitudeDeg)}#";
        var azimuthCommand = $":Sz{FormatAzimuth(azimuthDeg)}#";
        const string gotoCommand = ":MS#";

        return WithConnectionAsync(async (stream, token) =>
        {
            var altitudeReply = await SendAndReadSingleByteReplyAsync(stream, altitudeCommand, token);
            if (altitudeReply != "1")
                return OnStepCommandResult.Failure(altitudeCommand, altitudeReply, "OnStep rejected the target altitude.");

            var azimuthReply = await SendAndReadSingleByteReplyAsync(stream, azimuthCommand, token);
            if (azimuthReply != "1")
                return OnStepCommandResult.Failure(azimuthCommand, azimuthReply, "OnStep rejected the target azimuth.");

            var gotoReply = await SendAndReadSingleByteReplyAsync(stream, gotoCommand, token);
            return gotoReply == "0"
                ? OnStepCommandResult.Success(gotoCommand, gotoReply)
                : OnStepCommandResult.Failure(gotoCommand, gotoReply, GotoError(gotoReply));
        }, ct);
    }

    /// <summary>
    /// Sets target RA/Dec and synchronizes it with OnStep. During a manual alignment this
    /// command records the current alignment point.
    /// </summary>
    public Task<OnStepCommandResult> SyncSolvedPositionAsync(SolveResult result, CancellationToken ct)
    {
        var raCmd = $":Sr{FormatRa(result.RaDeg)}#";
        var decCmd = $":Sd{FormatDec(result.DecDeg)}#";
        const string cmCmd = ":CM#";

        return WithConnectionAsync(async (stream, token) =>
        {
            var raReply = await SendAndReadSingleByteReplyAsync(stream, raCmd, token);
            if (raReply != "1")
                return OnStepCommandResult.Failure(raCmd, raReply, "OnStep rejected the target right ascension.");

            var decReply = await SendAndReadSingleByteReplyAsync(stream, decCmd, token);
            if (decReply != "1")
                return OnStepCommandResult.Failure(decCmd, decReply, "OnStep rejected the target declination.");

            var syncReply = await SendAndReadHashReplyAsync(stream, cmCmd, token);
            return syncReply == "N/A"
                ? OnStepCommandResult.Success(cmCmd, syncReply)
                : OnStepCommandResult.Failure(cmCmd, syncReply, "OnStep rejected the coordinate sync.");
        }, ct);
    }

    // Retained for existing callers/tests. The options argument is intentionally ignored: all
    // operations read the current monitor value so host/port changes take effect immediately.
    internal Task SendSyncCommands(OnStepOptions _, SolveResult result, CancellationToken ct) =>
        SyncSolvedPositionAsync(result, ct);

    private async Task<T> WithConnectionAsync<T>(Func<NetworkStream, CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        var opts = _options.CurrentValue;
        if (!opts.Enabled)
            throw new InvalidOperationException("OnStep is disabled in configuration.");

        await _protocolGate.WaitAsync(ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var token = timeout.Token;

            using var client = new TcpClient();
            try
            {
                await client.ConnectAsync(opts.Host, opts.Port, token);
                using var stream = client.GetStream();
                return await operation(stream, token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"OnStep did not respond within 3 seconds ({opts.Host}:{opts.Port}).");
            }
        }
        finally
        {
            _protocolGate.Release();
        }
    }

    private static async Task<string> SendAndReadSingleByteReplyAsync(NetworkStream stream, string command, CancellationToken ct)
    {
        await SendCommandAsync(stream, command, ct);
        var buffer = new byte[1];
        var count = await stream.ReadAsync(buffer, ct);
        if (count == 0)
            throw new IOException($"OnStep closed the connection without replying to {command}.");
        return Encoding.ASCII.GetString(buffer, 0, count);
    }

    private static async Task<string> SendAndReadHashReplyAsync(NetworkStream stream, string command, CancellationToken ct)
    {
        await SendCommandAsync(stream, command, ct);
        var reply = new StringBuilder();
        var buffer = new byte[1];

        while (true)
        {
            var count = await stream.ReadAsync(buffer, ct);
            if (count == 0)
                throw new IOException($"OnStep closed the connection before terminating its reply to {command}.");

            var character = (char)buffer[0];
            if (character == '#')
                return reply.ToString();
            if (reply.Length >= 1024)
                throw new InvalidDataException($"OnStep reply to {command} exceeded 1024 bytes.");
            reply.Append(character);
        }
    }

    private static async Task SendCommandAsync(NetworkStream stream, string command, CancellationToken ct)
    {
        var bytes = Encoding.ASCII.GetBytes(command);
        await stream.WriteAsync(bytes, ct);
    }

    private static string GotoError(string reply) => reply switch
    {
        "1" => "Target is below the horizon limit.",
        "2" => "Target is above the overhead limit.",
        "3" => "Controller is in standby.",
        "4" => "Mount is parked.",
        "5" => "A goto is already in progress.",
        "6" => "Target is outside mount limits.",
        "7" => "Mount reported a hardware fault.",
        "8" => "Mount is already in motion.",
        "9" => "Mount reported an unspecified goto error.",
        _ => "OnStep returned an invalid goto response."
    };

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

    internal static double ParseRa(string value)
    {
        var fields = value.Split(':');
        if (fields.Length is < 2 or > 3)
            throw new FormatException($"Invalid OnStep RA reply '{value}'.");

        var hours = ParseCoordinateField(fields[0], value);
        var minutes = ParseCoordinateField(fields[1], value);
        var seconds = fields.Length == 3 ? ParseCoordinateField(fields[2], value) : 0;
        if (hours is < 0 or > 24 || minutes is < 0 or >= 60 || seconds is < 0 or >= 60)
            throw new FormatException($"Invalid OnStep RA reply '{value}'.");

        return (hours + minutes / 60.0 + seconds / 3600.0) * 15.0;
    }

    internal static double ParseDec(string value)
    {
        if (value.Length < 4 || (value[0] != '+' && value[0] != '-'))
            throw new FormatException($"Invalid OnStep Dec reply '{value}'.");

        var fields = value[1..].Replace('*', ':').Split(':');
        if (fields.Length is < 2 or > 3)
            throw new FormatException($"Invalid OnStep Dec reply '{value}'.");

        var degrees = ParseCoordinateField(fields[0], value);
        var minutes = ParseCoordinateField(fields[1], value);
        var seconds = fields.Length == 3 ? ParseCoordinateField(fields[2], value) : 0;
        if (degrees is < 0 or > 90 || minutes is < 0 or >= 60 || seconds is < 0 or >= 60)
            throw new FormatException($"Invalid OnStep Dec reply '{value}'.");

        var result = degrees + minutes / 60.0 + seconds / 3600.0;
        return value[0] == '-' ? -result : result;
    }

    private static double ParseCoordinateField(string field, string original) =>
        double.TryParse(field, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new FormatException($"Invalid OnStep coordinate reply '{original}'.");

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

    internal static string FormatAltitude(double altitudeDeg)
    {
        var sign = altitudeDeg >= 0 ? "+" : "-";
        var (degrees, minutes, seconds) = ToDms(Math.Abs(altitudeDeg));
        return $"{sign}{degrees:D2}*{minutes:D2}'{seconds:D2}";
    }

    internal static string FormatAzimuth(double azimuthDeg)
    {
        var normalized = azimuthDeg % 360.0;
        if (normalized < 0)
            normalized += 360.0;

        var (degrees, minutes, seconds) = ToDms(normalized);
        if (degrees == 360)
            degrees = 0;
        return $"{degrees:D3}*{minutes:D2}'{seconds:D2}";
    }

    private static (int Degrees, int Minutes, int Seconds) ToDms(double degrees)
    {
        var totalSeconds = (int)Math.Round(degrees * 3600, MidpointRounding.AwayFromZero);
        var wholeDegrees = totalSeconds / 3600;
        var minutes = (totalSeconds % 3600) / 60;
        var seconds = totalSeconds % 60;
        return (wholeDegrees, minutes, seconds);
    }
}
