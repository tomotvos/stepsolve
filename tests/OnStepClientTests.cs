using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using StepSolve;

namespace StepSolve.Tests;

public class OnStepClientTests
{
    // --- Coordinate Formatting (same as LX200, shared via OnStepClient) ---

    [Theory]
    [InlineData(0.0, "00:00:00")]
    [InlineData(180.0, "12:00:00")]
    [InlineData(296.944646, "19:47:46")]
    public void FormatRa_ConvertsCorrectly(double deg, string expected)
    {
        Assert.Equal(expected, OnStepClient.FormatRa(deg));
    }

    [Theory]
    [InlineData(0.0, "+00*00:00")]
    [InlineData(42.688983, "+42*41:20")]
    [InlineData(-45.5, "-45*30:00")]
    public void FormatDec_ConvertsCorrectly(double deg, string expected)
    {
        Assert.Equal(expected, OnStepClient.FormatDec(deg));
    }

    // --- Angular Distance ---

    [Fact]
    public void AngularDistance_SamePoint_IsZero()
    {
        Assert.Equal(0.0, OnStepClient.AngularDistance(100, 45, 100, 45), precision: 6);
    }

    [Fact]
    public void AngularDistance_SmallSeparation()
    {
        // 1 degree RA apart at equator ≈ 1 degree
        var dist = OnStepClient.AngularDistance(100, 0, 101, 0);
        Assert.InRange(dist, 0.9, 1.1);
    }

    [Fact]
    public void AngularDistance_LargeSeparation()
    {
        // Opposite sides of sky
        var dist = OnStepClient.AngularDistance(0, 0, 180, 0);
        Assert.Equal(180.0, dist, precision: 1);
    }

    [Fact]
    public void AngularDistance_PoleToEquator()
    {
        var dist = OnStepClient.AngularDistance(0, 0, 0, 90);
        Assert.Equal(90.0, dist, precision: 1);
    }

    [Fact]
    public void AngularDistance_NegativeDec()
    {
        var dist = OnStepClient.AngularDistance(0, -45, 0, 45);
        Assert.Equal(90.0, dist, precision: 1);
    }

    // --- Safety Threshold ---

    [Fact]
    public async Task SyncAsync_Skips_WhenDeltaExceedsThreshold()
    {
        var opts = new TestOptionsMonitor<OnStepOptions>(new OnStepOptions
        {
            Enabled = true,
            MaxSyncDeltaDeg = 5.0
        });
        var client = new OnStepClient(opts, NullLogger<OnStepClient>.Instance);

        // First sync succeeds — establishes the baseline position.
        // We need a listening server for this to work, so use SendSyncCommands directly
        // to simulate a successful sync instead.
        // Directly test via a mock server to establish baseline, then attempt big jump.
        var port = FindAvailablePort();
        var firstOpts = new OnStepOptions { Enabled = true, Host = "127.0.0.1", Port = port, MaxSyncDeltaDeg = 5.0 };
        var clientOpts = new TestOptionsMonitor<OnStepOptions>(firstOpts);
        client = new OnStepClient(clientOpts, NullLogger<OnStepClient>.Instance);

        // Start mock server for first sync
        var mockServer = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
        mockServer.Start();
        var serverTask = Task.Run(async () =>
        {
            using var sc = await mockServer.AcceptTcpClientAsync();
            using var stream = sc.GetStream();
            await ReplyToCommandsAsync(stream, [
                (":Sr06:40:00#", "1"),
                (":Sd+50*00:00#", "1"),
                (":CM#", "N/A#"),
            ]);
        });

        // First sync at (100, 50) — establishes baseline
        var firstResult = new SolveResult(100, 50, null, null, 0.9, TimeSpan.Zero, "test");
        await client.SyncAsync(firstResult, CancellationToken.None);
        await serverTask;
        mockServer.Stop();

        Assert.Equal("ok", client.LastSyncResult);

        // Now attempt a solve 20° away — should exceed 5° threshold and be skipped
        var bigJump = new SolveResult(120, 50, null, null, 0.9, TimeSpan.Zero, "test");
        await client.SyncAsync(bigJump, CancellationToken.None);

        Assert.Contains("skipped", client.LastSyncResult);
    }

    [Fact]
    public async Task SyncAsync_DoesNothing_WhenDisabled()
    {
        var opts = new TestOptionsMonitor<OnStepOptions>(new OnStepOptions { Enabled = false });
        var client = new OnStepClient(opts, NullLogger<OnStepClient>.Instance);

        var result = new SolveResult(100, 50, null, null, 0.9, TimeSpan.Zero, "test");
        await client.SyncAsync(result, CancellationToken.None);

        // Should not have attempted sync
        Assert.Null(client.LastSyncResult);
    }

    // --- TCP Integration: Mock OnStep Server ---

    [Fact]
    public async Task SyncSolvedPositionAsync_SendsCorrectProtocolAndChecksReplies()
    {
        var port = FindAvailablePort();
        var receivedData = new StringBuilder();

        // Start a mock OnStep server
        var mockServer = new TcpListener(IPAddress.Loopback, port);
        mockServer.Start();

        var serverTask = Task.Run(async () =>
        {
            using var serverClient = await mockServer.AcceptTcpClientAsync();
            using var stream = serverClient.GetStream();
            await ReplyToCommandsAsync(stream, [
                (":Sr19:47:46#", "1"),
                (":Sd+42*41:20#", "1"),
                (":CM#", "N/A#"),
            ], receivedData);
        });

        try
        {
            var opts = new OnStepOptions { Enabled = true, Host = "127.0.0.1", Port = port };
            var clientOpts = new TestOptionsMonitor<OnStepOptions>(opts);
            var client = new OnStepClient(clientOpts, NullLogger<OnStepClient>.Instance);

            var result = new SolveResult(296.944646, 42.688983, null, null, 0.95, TimeSpan.FromSeconds(2), "astrometry");
            var sync = await client.SyncSolvedPositionAsync(result, CancellationToken.None);

            await serverTask;

            var received = receivedData.ToString();

            // Verify the exact protocol commands
            Assert.Contains(":Sr19:47:46#", received);
            Assert.Contains(":Sd+42*41:20#", received);
            Assert.Contains(":CM#", received);
            Assert.True(sync.Succeeded);
            Assert.Equal("N/A", sync.Response);
        }
        finally
        {
            mockServer.Stop();
        }
    }

    [Fact]
    public async Task SyncAsync_RecordsError_WhenConnectionFails()
    {
        var opts = new TestOptionsMonitor<OnStepOptions>(new OnStepOptions
        {
            Enabled = true,
            Host = "127.0.0.1",
            Port = 19999  // Nothing listening here
        });
        var client = new OnStepClient(opts, NullLogger<OnStepClient>.Instance);

        var result = new SolveResult(100, 50, null, null, 0.9, TimeSpan.Zero, "test");
        await client.SyncAsync(result, CancellationToken.None);

        Assert.NotNull(client.LastSyncResult);
        Assert.StartsWith("error:", client.LastSyncResult);
    }

    [Fact]
    public async Task ProbeAndQueryOperations_ParseHashTerminatedReplies()
    {
        var port = FindAvailablePort();
        using var server = new TcpListener(IPAddress.Loopback, port);
        server.Start();

        var serverTask = Task.Run(async () =>
        {
            using var client = await server.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            await ReplyToCommandsAsync(stream, [
                (":GVP#", "OnStepX#"),
                (":GVN#", "5.0.0#"),
            ]);
        });

        var onstep = CreateEnabledClient(port);
        var identity = await onstep.ProbeAsync(CancellationToken.None);
        await serverTask;

        Assert.Equal("OnStepX", identity.Product);
        Assert.Equal("5.0.0", identity.FirmwareVersion);
    }

    [Fact]
    public async Task ProbeAsync_ReconnectsOnce_WhenAnIdleConnectionWasClosedByOnStep()
    {
        var port = FindAvailablePort();
        using var server = new TcpListener(IPAddress.Loopback, port);
        server.Start();

        var serverTask = Task.Run(async () =>
        {
            // Model a controller that silently closes its idle connection. The next
            // read-only identity probe must establish a fresh connection.
            using (var staleClient = await server.AcceptTcpClientAsync())
            using (var staleStream = staleClient.GetStream())
                Assert.Equal(":GVP#", await ReadFrameAsync(staleStream));

            using var freshClient = await server.AcceptTcpClientAsync();
            using var freshStream = freshClient.GetStream();
            await ReplyToCommandsAsync(freshStream,
            [
                (":GVP#", "OnStepX#"),
                (":GVN#", "5.0.0#"),
            ]);
        });

        var identity = await CreateEnabledClient(port).ProbeAsync(CancellationToken.None);
        await serverTask;

        Assert.Equal("OnStepX", identity.Product);
        Assert.Equal("5.0.0", identity.FirmwareVersion);
    }

    [Fact]
    public async Task StatusPositionAndAlignmentProgress_ParseReplies()
    {
        var port = FindAvailablePort();
        using var server = new TcpListener(IPAddress.Loopback, port);
        server.Start();

        var serverTask = Task.Run(async () =>
        {
            using var client = await server.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            await ReplyToCommandsAsync(stream,
            [
                (":GU#", "pH#"),
                (":GR#", "12:00:00#"),
                (":GD#", "-45*30:00#"),
                (":A?#", "313#"),
            ]);
        });

        var onstep = CreateEnabledClient(port);
        var status = await onstep.GetStatusAsync(CancellationToken.None);
        var position = await onstep.GetPositionAsync(CancellationToken.None);
        var alignment = await onstep.GetAlignmentProgressAsync(CancellationToken.None);
        await serverTask;

        Assert.True(status.IsSlewing);
        Assert.True(status.IsAtHome);
        Assert.Equal(180, position.RaDeg, precision: 6);
        Assert.Equal(-45.5, position.DecDeg, precision: 6);
        Assert.Equal(3, alignment.MaximumStars);
        Assert.Equal(1, alignment.CurrentStar);
        Assert.Equal(3, alignment.LastRequiredStar);
        Assert.True(alignment.IsActive);
    }

    [Fact]
    public async Task PersistentConnection_ReconnectsWhenEndpointChanges()
    {
        var firstPort = FindAvailablePort();
        var secondPort = FindAvailablePort();
        using var firstServer = new TcpListener(IPAddress.Loopback, firstPort);
        using var secondServer = new TcpListener(IPAddress.Loopback, secondPort);
        firstServer.Start();
        secondServer.Start();

        var firstTask = Task.Run(async () =>
        {
            using var client = await firstServer.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            await ReplyToCommandsAsync(stream, [ (":GU#", "pH#") ]);
        });
        var secondTask = Task.Run(async () =>
        {
            using var client = await secondServer.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            await ReplyToCommandsAsync(stream, [ (":GU#", "N#") ]);
        });

        var options = new TestOptionsMonitor<OnStepOptions>(new OnStepOptions
        {
            Enabled = true,
            Host = "127.0.0.1",
            Port = firstPort,
        });
        var onstep = new OnStepClient(options, NullLogger<OnStepClient>.Instance);

        var first = await onstep.GetStatusAsync(CancellationToken.None);
        options.Set(new OnStepOptions
        {
            Enabled = true,
            Host = "127.0.0.1",
            Port = secondPort,
        });
        var second = await onstep.GetStatusAsync(CancellationToken.None);
        await Task.WhenAll(firstTask, secondTask);

        Assert.True(first.IsSlewing);
        Assert.False(second.IsSlewing);
    }

    [Fact]
    public async Task StartAlignmentAndGoto_RequireSuccessfulAcknowledgements()
    {
        var port = FindAvailablePort();
        using var server = new TcpListener(IPAddress.Loopback, port);
        server.Start();
        var received = new StringBuilder();

        var serverTask = Task.Run(async () =>
        {
            using (var alignmentClient = await server.AcceptTcpClientAsync())
            using (var alignmentStream = alignmentClient.GetStream())
                await ReplyToCommandsAsync(alignmentStream, [ (":A3#", "1") ], received);

            using var gotoClient = await server.AcceptTcpClientAsync();
            using var gotoStream = gotoClient.GetStream();
            await ReplyToCommandsAsync(gotoStream,
            [
                (":Sa+45*00'00#", "1"),
                (":Sz060*00'00#", "1"),
                (":MA#", "0"),
            ], received);
        });

        var onstep = CreateEnabledClient(port);
        var started = await onstep.StartAlignmentAsync(3, CancellationToken.None);
        var gotoResult = await onstep.GotoAltAzAsync(45, 60, CancellationToken.None);
        await serverTask;

        Assert.True(started.Succeeded);
        Assert.True(gotoResult.Succeeded);
        Assert.Equal(":MA#", gotoResult.Command);
        Assert.Contains(":A3#", received.ToString());
        Assert.Contains(":Sa+45*00'00#", received.ToString());
        Assert.Contains(":Sz060*00'00#", received.ToString());
    }

    [Fact]
    public async Task SetOverheadLimitAsync_SendsReplyCheckedCommand()
    {
        var port = FindAvailablePort();
        using var server = new TcpListener(IPAddress.Loopback, port);
        server.Start();
        var serverTask = Task.Run(async () =>
        {
            using var client = await server.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            await ReplyToCommandsAsync(stream, [ (":So90#", "1") ]);
        });

        var result = await CreateEnabledClient(port).SetOverheadLimitAsync(90, CancellationToken.None);
        await serverTask;

        Assert.True(result.Succeeded);
        Assert.Equal(":So90#", result.Command);
    }

    [Fact]
    public async Task AcceptAlignmentPointAsync_UsesDocumentedAlignmentAcceptanceCommand()
    {
        var port = FindAvailablePort();
        using var server = new TcpListener(IPAddress.Loopback, port);
        server.Start();
        var received = new StringBuilder();
        var serverTask = Task.Run(async () =>
        {
            using var client = await server.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            await ReplyToCommandsAsync(stream,
            [
                (":Sr19:47:46#", "1"),
                (":Sd+42*41:20#", "1"),
                (":A+#", "1"),
            ], received);
        });

        var result = new SolveResult(296.944646, 42.688983, null, null, 0.95, TimeSpan.Zero, "test");
        var accepted = await CreateEnabledClient(port).AcceptAlignmentPointAsync(result, CancellationToken.None);
        await serverTask;

        Assert.True(accepted.Succeeded);
        Assert.Equal(":A+#", accepted.Command);
        Assert.Equal("1", accepted.Response);
        Assert.Contains(":A+#", received.ToString());
    }

    [Fact]
    public async Task AcceptAlignmentPointAsync_UsesFreshConnectionAfterAnIdleStatusQuery()
    {
        var port = FindAvailablePort();
        using var server = new TcpListener(IPAddress.Loopback, port);
        server.Start();
        var serverTask = Task.Run(async () =>
        {
            using (var idleClient = await server.AcceptTcpClientAsync())
            using (var idleStream = idleClient.GetStream())
                await ReplyToCommandsAsync(idleStream, [ (":GU#", "N#") ]);

            using var acceptClient = await server.AcceptTcpClientAsync();
            using var acceptStream = acceptClient.GetStream();
            await ReplyToCommandsAsync(acceptStream,
            [
                (":Sr19:47:46#", "1"),
                (":Sd+42*41:20#", "1"),
                (":A+#", "1"),
            ]);
        });

        var onstep = CreateEnabledClient(port);
        await onstep.GetStatusAsync(CancellationToken.None);
        var result = new SolveResult(296.944646, 42.688983, null, null, 0.95, TimeSpan.Zero, "test");
        var accepted = await onstep.AcceptAlignmentPointAsync(result, CancellationToken.None);
        await serverTask;

        Assert.True(accepted.Succeeded);
    }

    [Fact]
    public async Task ReturnHomeAsync_SendsNoReplyCommandWithoutWaitingForAReply()
    {
        var port = FindAvailablePort();
        using var server = new TcpListener(IPAddress.Loopback, port);
        server.Start();
        var serverTask = Task.Run(async () =>
        {
            using var client = await server.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            Assert.Equal(":hC#", await ReadFrameAsync(stream));
            // OnStep :hC# deliberately sends no response.
        });

        var result = await CreateEnabledClient(port).ReturnHomeAsync(CancellationToken.None);
        await serverTask;

        Assert.True(result.Succeeded);
        Assert.Equal(":hC#", result.Command);
        Assert.Equal(string.Empty, result.Response);
    }

    [Fact]
    public async Task GotoAltAz_ReturnsControllerRejection()
    {
        var port = FindAvailablePort();
        using var server = new TcpListener(IPAddress.Loopback, port);
        server.Start();
        var serverTask = Task.Run(async () =>
        {
            using var client = await server.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            await ReplyToCommandsAsync(stream, [
                (":Sa+45*00'00#", "1"),
                (":Sz060*00'00#", "1"),
                (":MA#", "4"),
            ]);
        });

        var result = await CreateEnabledClient(port).GotoAltAzAsync(45, 60, CancellationToken.None);
        await serverTask;

        Assert.False(result.Succeeded);
        Assert.Equal(":MA#", result.Command);
        Assert.Contains("parked", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(45.0, "+45*00'00")]
    [InlineData(-4.5, "-04*30'00")]
    public void FormatAltitude_UsesOnStepAltAzFormat(double value, string expected)
    {
        Assert.Equal(expected, OnStepClient.FormatAltitude(value));
    }

    [Theory]
    [InlineData(60.0, "060*00'00")]
    [InlineData(-10.0, "350*00'00")]
    public void FormatAzimuth_NormalizesAndUsesOnStepAltAzFormat(double value, string expected)
    {
        Assert.Equal(expected, OnStepClient.FormatAzimuth(value));
    }

    private static OnStepClient CreateEnabledClient(int port) => new(
        new TestOptionsMonitor<OnStepOptions>(new OnStepOptions
        {
            Enabled = true,
            Host = "127.0.0.1",
            Port = port,
        }),
        NullLogger<OnStepClient>.Instance);

    private static async Task ReplyToCommandsAsync(NetworkStream stream,
        IReadOnlyList<(string Expected, string Reply)> commands, StringBuilder? received = null)
    {
        foreach (var (expected, reply) in commands)
        {
            var command = await ReadFrameAsync(stream);
            received?.Append(command);
            Assert.Equal(expected, command);
            await stream.WriteAsync(Encoding.ASCII.GetBytes(reply));
        }
    }

    private static async Task<string> ReadFrameAsync(NetworkStream stream)
    {
        var buffer = new byte[1];
        var frame = new StringBuilder();
        while (true)
        {
            var count = await stream.ReadAsync(buffer);
            if (count == 0)
                throw new IOException("Client closed before completing an LX200 frame.");
            frame.Append((char)buffer[0]);
            if (buffer[0] == '#')
                return frame.ToString();
        }
    }

    private static int FindAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
