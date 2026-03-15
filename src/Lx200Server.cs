using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace StepSolve;

/// <summary>
/// TCP server implementing the Meade LX200 protocol (read-only) for SkySafari.
/// Runs as a BackgroundService, one Task per connected client.
/// Never slews the mount — only reports the latest solved position.
/// </summary>
public sealed class Lx200Server : BackgroundService
{
    private readonly SolveState _state;
    private readonly IOptionsMonitor<StepSolveOptions> _options;
    private readonly ILogger<Lx200Server> _logger;

    public Lx200Server(SolveState state, IOptionsMonitor<StepSolveOptions> options, ILogger<Lx200Server> logger)
    {
        _state = state;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var port = _options.CurrentValue.Lx200Port;
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        _logger.LogInformation("LX200 server listening on port {Port}", port);

        stoppingToken.Register(() => listener.Stop());

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken);
                _ = HandleClientAsync(client, stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            listener.Stop();
            _logger.LogInformation("LX200 server stopped");
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        var endpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        _logger.LogDebug("LX200 client connected: {Endpoint}", endpoint);

        try
        {
            client.ReceiveTimeout = 30_000;
            client.SendTimeout = 5_000;

            using var stream = client.GetStream();
            var buffer = new byte[1024];
            var pending = new StringBuilder();

            while (!ct.IsCancellationRequested)
            {
                int bytesRead;
                try
                {
                    bytesRead = await stream.ReadAsync(buffer, ct);
                }
                catch (IOException)
                {
                    break;
                }

                if (bytesRead == 0) break;

                var received = Encoding.ASCII.GetString(buffer, 0, bytesRead);
                _logger.LogDebug("LX200 RECV {Endpoint}: {Data}", endpoint, received);
                pending.Append(received);

                // Process all complete commands (delimited by #)
                var data = pending.ToString();
                var commands = data.Split('#');

                // Last element is either empty (data ended with #) or partial (buffer more)
                pending.Clear();
                if (!data.EndsWith('#'))
                {
                    pending.Append(commands[^1]);
                    commands = commands[..^1];
                }

                foreach (var rawCmd in commands)
                {
                    if (string.IsNullOrWhiteSpace(rawCmd)) continue;

                    var response = ProcessCommand(rawCmd);
                    if (response != null)
                    {
                        var responseBytes = Encoding.ASCII.GetBytes(response);
                        await stream.WriteAsync(responseBytes, ct);
                        _logger.LogDebug("LX200 SEND {Endpoint}: {Response}", endpoint, response);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LX200 client {Endpoint} error", endpoint);
        }
        finally
        {
            client.Dispose();
            _logger.LogDebug("LX200 client disconnected: {Endpoint}", endpoint);
        }
    }

    internal string? ProcessCommand(string rawCommand)
    {
        // Normalize: strip leading colon, uppercase for matching
        var cmd = rawCommand.TrimStart(':').ToUpperInvariant();

        // Read queries
        if (cmd is "GR" or "RS")
            return FormatRa() + "#";

        if (cmd == "GD")
            return FormatDec() + "#";

        if (cmd == "GVP")
            return "StepSolve#";

        if (cmd == "GVN")
            return "1.0#";

        if (cmd is "GVD" or "GC")
            return $"{DateTime.Now.Month:D2}/{DateTime.Now.Day:D2}/{DateTime.Now.Year % 100:D2}" + "#";

        if (cmd is "GVT" or "GL")
            return $"{DateTime.Now.Hour:D2}:{DateTime.Now.Minute:D2}:{DateTime.Now.Second:D2}" + "#";

        // Precision toggle — SkySafari sends this on connect
        // Returns "1" without trailing # (per LX200 protocol quirk)
        if (cmd == "U")
            return "1";

        // Set commands — accept but ignore (ACK with "1")
        if (cmd.StartsWith("SC") || cmd.StartsWith("SL") ||
            cmd.StartsWith("ST") || cmd.StartsWith("SG"))
            return "1";

        // Motion commands — ignored, return "0" (no slew)
        if (cmd is "MS" || cmd.StartsWith("MN") || cmd.StartsWith("ME") ||
            cmd.StartsWith("MS") || cmd.StartsWith("MW"))
            return "0";

        // Unknown command
        _logger.LogDebug("LX200 unknown command: {Cmd}", rawCommand);
        return "#";
    }

    internal string FormatRa()
    {
        var (raDeg, _) = _state.GetCoordinates();
        return FormatRaDeg(raDeg);
    }

    internal string FormatDec()
    {
        var (_, decDeg) = _state.GetCoordinates();
        return FormatDecDeg(decDeg);
    }

    /// <summary>
    /// Convert RA in degrees to HH:MM:SS format.
    /// </summary>
    internal static string FormatRaDeg(double raDeg)
    {
        var hours = raDeg / 15.0;
        var h = (int)hours;
        var m = (int)((hours - h) * 60);
        var s = (int)(((hours - h) * 60 - m) * 60);
        return $"{h:D2}:{m:D2}:{s:D2}";
    }

    /// <summary>
    /// Convert Dec in degrees to ±DD*MM:SS format.
    /// Note: uses asterisk (not degree symbol) after degrees per LX200 protocol.
    /// </summary>
    internal static string FormatDecDeg(double decDeg)
    {
        var sign = decDeg >= 0 ? "+" : "-";
        var v = Math.Abs(decDeg);
        var d = (int)v;
        var m = (int)((v - d) * 60);
        var s = (int)(((v - d) * 60 - m) * 60);
        return $"{sign}{d:D2}*{m:D2}:{s:D2}";
    }
}
