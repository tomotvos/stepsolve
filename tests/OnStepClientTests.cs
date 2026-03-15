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
        var state = new SolveState();

        // Set previous position far away from new solve
        state.UpdateResult(new SolveResult(100, 50, null, null, 0.9, TimeSpan.Zero, "test"));

        // Solve result is 20° away — should exceed 5° threshold
        var result = new SolveResult(120, 50, null, null, 0.9, TimeSpan.Zero, "test");
        await client.SyncAsync(result, state, CancellationToken.None);

        Assert.Contains("skipped", client.LastSyncResult);
    }

    [Fact]
    public async Task SyncAsync_DoesNothing_WhenDisabled()
    {
        var opts = new TestOptionsMonitor<OnStepOptions>(new OnStepOptions { Enabled = false });
        var client = new OnStepClient(opts, NullLogger<OnStepClient>.Instance);
        var state = new SolveState();

        var result = new SolveResult(100, 50, null, null, 0.9, TimeSpan.Zero, "test");
        await client.SyncAsync(result, state, CancellationToken.None);

        // Should not have attempted sync
        Assert.Null(client.LastSyncResult);
    }

    // --- TCP Integration: Mock OnStep Server ---

    [Fact]
    public async Task SendSyncCommands_SendsCorrectProtocol()
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
            var buffer = new byte[1024];
            var totalRead = 0;

            // Read all commands (3 expected)
            while (totalRead < 30)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(totalRead));
                if (read == 0) break;
                totalRead += read;
                // Check if we have all 3 # terminators
                var data = Encoding.ASCII.GetString(buffer, 0, totalRead);
                if (data.Count(c => c == '#') >= 3) break;
            }

            receivedData.Append(Encoding.ASCII.GetString(buffer, 0, totalRead));
        });

        try
        {
            var opts = new OnStepOptions { Enabled = true, Host = "127.0.0.1", Port = port };
            var clientOpts = new TestOptionsMonitor<OnStepOptions>(opts);
            var client = new OnStepClient(clientOpts, NullLogger<OnStepClient>.Instance);

            var result = new SolveResult(296.944646, 42.688983, null, null, 0.95, TimeSpan.FromSeconds(2), "astrometry");
            await client.SendSyncCommands(opts, result, CancellationToken.None);

            await serverTask;

            var received = receivedData.ToString();

            // Verify the exact protocol commands
            Assert.Contains(":Sr19:47:46#", received);
            Assert.Contains(":Sd+42*41:20#", received);
            Assert.Contains(":CM#", received);
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
        var state = new SolveState();

        var result = new SolveResult(100, 50, null, null, 0.9, TimeSpan.Zero, "test");
        await client.SyncAsync(result, state, CancellationToken.None);

        Assert.NotNull(client.LastSyncResult);
        Assert.StartsWith("error:", client.LastSyncResult);
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
