using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using StepSolve;

namespace StepSolve.Tests;

public class Lx200ServerTests
{
    private readonly SolveState _state = new();
    private readonly Lx200Server _server;

    public Lx200ServerTests()
    {
        var opts = new TestOptionsMonitor<StepSolveOptions>(new StepSolveOptions());
        _server = new Lx200Server(_state, opts, NullLogger<Lx200Server>.Instance);
    }

    // --- Coordinate Formatting ---

    [Theory]
    [InlineData(0.0, "00:00:00")]
    [InlineData(180.0, "12:00:00")]    // 180° = 12h
    [InlineData(15.0, "01:00:00")]     // 15° = 1h
    [InlineData(296.944646, "19:47:46")]
    [InlineData(360.0, "24:00:00")]    // Edge: full circle
    [InlineData(7.5, "00:30:00")]      // 7.5° = 0h 30m
    public void FormatRaDeg_ConvertsCorrectly(double deg, string expected)
    {
        Assert.Equal(expected, Lx200Server.FormatRaDeg(deg));
    }

    [Theory]
    [InlineData(0.0, "+00*00:00")]
    [InlineData(42.688983, "+42*41:20")]
    [InlineData(-45.5, "-45*30:00")]
    [InlineData(90.0, "+90*00:00")]
    [InlineData(-90.0, "-90*00:00")]
    [InlineData(-0.5, "-00*30:00")]
    public void FormatDecDeg_ConvertsCorrectly(double deg, string expected)
    {
        Assert.Equal(expected, Lx200Server.FormatDecDeg(deg));
    }

    // --- Command Processing ---

    [Fact]
    public void ProcessCommand_GR_ReturnsRa()
    {
        _state.UpdateResult(new SolveResult(296.944646, 42.69, null, null, 0.95, TimeSpan.Zero, "test"));
        var response = _server.ProcessCommand(":GR");
        Assert.Equal("19:47:46#", response);
    }

    [Fact]
    public void ProcessCommand_RS_ReturnsRa()
    {
        // SkySafari alternate RA query
        _state.UpdateResult(new SolveResult(180.0, 0, null, null, 0.9, TimeSpan.Zero, "test"));
        var response = _server.ProcessCommand(":RS");
        Assert.Equal("12:00:00#", response);
    }

    [Fact]
    public void ProcessCommand_GD_ReturnsDec()
    {
        _state.UpdateResult(new SolveResult(0, 42.688983, null, null, 0.95, TimeSpan.Zero, "test"));
        var response = _server.ProcessCommand(":GD");
        Assert.Equal("+42*41:20#", response);
    }

    [Fact]
    public void ProcessCommand_GD_NegativeDec()
    {
        _state.UpdateResult(new SolveResult(0, -45.5, null, null, 0.9, TimeSpan.Zero, "test"));
        var response = _server.ProcessCommand(":GD");
        Assert.Equal("-45*30:00#", response);
    }

    [Fact]
    public void ProcessCommand_GR_NoSolve_ReturnsZero()
    {
        var response = _server.ProcessCommand(":GR");
        Assert.Equal("00:00:00#", response);
    }

    [Fact]
    public void ProcessCommand_GD_NoSolve_ReturnsZero()
    {
        var response = _server.ProcessCommand(":GD");
        Assert.Equal("+00*00:00#", response);
    }

    [Fact]
    public void ProcessCommand_GVP_ReturnsProductName()
    {
        Assert.Equal("StepSolve#", _server.ProcessCommand(":GVP"));
    }

    [Fact]
    public void ProcessCommand_GVN_ReturnsVersion()
    {
        Assert.Equal("1.0#", _server.ProcessCommand(":GVN"));
    }

    [Fact]
    public void ProcessCommand_U_ReturnsPrecisionToggle()
    {
        // Note: returns "1" without trailing # (LX200 protocol quirk)
        Assert.Equal("1", _server.ProcessCommand(":U"));
    }

    [Fact]
    public void ProcessCommand_GVD_ReturnsDate()
    {
        var response = _server.ProcessCommand(":GVD");
        Assert.NotNull(response);
        Assert.EndsWith("#", response);
        Assert.Matches(@"\d{2}/\d{2}/\d{2}#", response);
    }

    [Fact]
    public void ProcessCommand_GL_ReturnsTime()
    {
        var response = _server.ProcessCommand(":GL");
        Assert.NotNull(response);
        Assert.EndsWith("#", response);
        Assert.Matches(@"\d{2}:\d{2}:\d{2}#", response);
    }

    // --- Set commands (ACK only) ---

    [Theory]
    [InlineData(":SC01/01/26")]
    [InlineData(":SL12:00:00")]
    [InlineData(":St+45")]
    [InlineData(":Sg-120")]
    public void ProcessCommand_SetCommands_ReturnAck(string cmd)
    {
        Assert.Equal("1", _server.ProcessCommand(cmd));
    }

    // --- Motion commands (ignored) ---

    [Theory]
    [InlineData(":MS")]
    [InlineData(":Mn")]
    [InlineData(":Me")]
    [InlineData(":Mw")]
    public void ProcessCommand_MotionCommands_ReturnZero(string cmd)
    {
        Assert.Equal("0", _server.ProcessCommand(cmd));
    }

    // --- Unknown commands ---

    [Fact]
    public void ProcessCommand_Unknown_ReturnsHash()
    {
        Assert.Equal("#", _server.ProcessCommand(":XY"));
    }

    // --- Case insensitivity ---

    [Fact]
    public void ProcessCommand_CaseInsensitive()
    {
        Assert.Equal("StepSolve#", _server.ProcessCommand(":gvp"));
        Assert.Equal("1.0#", _server.ProcessCommand(":gvn"));
    }

    // --- Without leading colon ---

    [Fact]
    public void ProcessCommand_WithoutColon()
    {
        _state.UpdateResult(new SolveResult(180.0, 45.0, null, null, 0.9, TimeSpan.Zero, "test"));
        Assert.Equal("12:00:00#", _server.ProcessCommand("GR"));
        Assert.Equal("+45*00:00#", _server.ProcessCommand("GD"));
    }

    // --- TCP Integration Test ---

    [Fact]
    public async Task TcpIntegration_BatchedCommands()
    {
        _state.UpdateResult(new SolveResult(180.0, 45.0, null, null, 0.9, TimeSpan.Zero, "test"));

        var opts = new TestOptionsMonitor<StepSolveOptions>(new StepSolveOptions { Lx200Port = 0 });
        var server = new Lx200Server(_state, opts, NullLogger<Lx200Server>.Instance);

        // Use port 0 to get a random available port — but we need a different approach.
        // Instead, test with a known port range.
        var port = FindAvailablePort();
        var serverOpts = new TestOptionsMonitor<StepSolveOptions>(new StepSolveOptions { Lx200Port = port });
        var tcpServer = new Lx200Server(_state, serverOpts, NullLogger<Lx200Server>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await tcpServer.StartAsync(cts.Token);

        // Give server time to start
        await Task.Delay(200, cts.Token);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port, cts.Token);
            using var stream = client.GetStream();

            // Send batched commands like SkySafari does: ":GR#:GD#"
            var request = Encoding.ASCII.GetBytes(":GR#:GD#");
            await stream.WriteAsync(request, cts.Token);

            // Read responses
            var responseBuffer = new byte[256];
            var totalRead = 0;
            // Read until we have both responses
            while (totalRead < 10)
            {
                var read = await stream.ReadAsync(responseBuffer.AsMemory(totalRead), cts.Token);
                if (read == 0) break;
                totalRead += read;
            }

            var response = Encoding.ASCII.GetString(responseBuffer, 0, totalRead);

            // Should contain both RA and Dec responses
            Assert.Contains("12:00:00#", response);
            Assert.Contains("+45*00:00#", response);
        }
        finally
        {
            await tcpServer.StopAsync(CancellationToken.None);
        }
    }

    private static int FindAvailablePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
