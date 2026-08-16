using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;

namespace StepSolve.Tests;

public sealed class OnStepCalibrationControllerTests
{
    [Fact]
    public async Task StartAsync_RejectsRequestsOutsideCalibrateMode()
    {
        var controller = CreateController(new OnStepOptions { Enabled = true });

        var result = await controller.StartAsync(true, "solve", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Calibrate mode", result.Error);
        Assert.Equal("Idle", controller.Status.State);
    }

    [Fact]
    public async Task StartAsync_RejectsDisabledOnStepWithoutConnecting()
    {
        var controller = CreateController(new OnStepOptions { Enabled = false });

        var result = await controller.StartAsync(true, "calibrate", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("disabled", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Idle", controller.Status.State);
    }

    [Fact]
    public async Task InitializeAsync_ProbesOnStepAndPublishesSafeIdleStatus()
    {
        await using var mount = new MockOnStep();
        var controller = CreateController(new OnStepOptions
        {
            Enabled = true,
            Host = "127.0.0.1",
            Port = mount.Port,
            StartupPolicy = "probe",
        });

        await controller.InitializeAsync(CancellationToken.None);

        Assert.Equal("Idle", controller.Status.State);
        Assert.True(controller.Status.IsConnected);
        Assert.True(controller.Status.IsSafe);
        Assert.Contains("OnStepX", controller.Status.Message);
        var commands = await mount.StopAsync();
        Assert.Equal(new[] { ":GVP#", ":GVN#", ":GU#" }, commands);
    }

    [Fact]
    public async Task ThreePointFlow_RequiresTwoFreshSolvesAndExplicitApproval()
    {
        await using var mount = new MockOnStep();
        var controller = CreateController(new OnStepOptions
        {
            Enabled = true,
            Host = "127.0.0.1",
            Port = mount.Port,
            StableSolveIntervalSeconds = 0,
            CalibrationSettleSeconds = 0,
        });

        var started = await controller.StartAsync(true, "calibrate", CancellationToken.None);

        Assert.True(started.Success);
        Assert.Equal("WaitingForGoto", controller.Status.State);
        Assert.Equal(1, controller.Status.CurrentPoint);
        Assert.Equal(0, controller.Status.RequestedAzimuthDeg);
        Assert.Equal(45, controller.Status.RequestedAltitudeDeg);

        await controller.TickAsync(CancellationToken.None); // GoTo complete → settling
        await controller.TickAsync(CancellationToken.None); // no configured delay → solve gate
        Assert.True(controller.NeedsFreshSolve);

        var solve = new SolveResult(100, 45, null, null, 0.99, TimeSpan.FromMilliseconds(20), "stub");
        await controller.SubmitFreshSolveAsync(solve, CancellationToken.None);
        Assert.Equal("AwaitingStableSolves", controller.Status.State);
        await controller.SubmitFreshSolveAsync(solve, CancellationToken.None);

        Assert.Equal("AwaitingAcceptance", controller.Status.State);
        Assert.Equal(100, controller.Status.CandidateRaDeg);
        Assert.Equal(45, controller.Status.CandidateDecDeg);

        var accepted = await controller.AcceptAsync("calibrate", CancellationToken.None);

        Assert.True(accepted.Success);
        Assert.Equal("WaitingForGoto", controller.Status.State);
        Assert.Equal(2, controller.Status.CurrentPoint);
        Assert.Equal(60, controller.Status.RequestedAzimuthDeg);
        Assert.Equal(60, controller.Status.RequestedAltitudeDeg);

        var commands = await mount.StopAsync();
        Assert.Equal(new[]
        {
            ":GVP#", ":GVN#", ":GU#", ":A3#",
            ":Sa+45*00'00#", ":Sz000*00'00#", ":MS#",
            ":GU#",
            ":Sr06:40:00#", ":Sd+45*00:00#", ":CM#",
            ":A?#",
            ":Sa+60*00'00#", ":Sz060*00'00#", ":MS#",
        }, commands);
    }

    [Fact]
    public async Task SubmitFreshSolveAsync_UsesBoundedAzimuthAlternateWhenNoSolve()
    {
        await using var mount = new MockOnStep();
        var controller = CreateController(new OnStepOptions
        {
            Enabled = true,
            Host = "127.0.0.1",
            Port = mount.Port,
            CalibrationSettleSeconds = 0,
        });

        Assert.True((await controller.StartAsync(true, "calibrate", CancellationToken.None)).Success);
        await controller.TickAsync(CancellationToken.None);
        await controller.TickAsync(CancellationToken.None);
        await controller.SubmitFreshSolveAsync(new SolveResult(0, 0, null, null, 0, TimeSpan.Zero, "stub"), CancellationToken.None);

        Assert.Equal("WaitingForGoto", controller.Status.State);
        Assert.Equal(2, controller.Status.Attempt);
        Assert.Equal(10, controller.Status.RequestedAzimuthDeg);

        var commands = await mount.StopAsync();
        Assert.Contains(":Sz010*00'00#", commands);
    }

    private static OnStepCalibrationController CreateController(OnStepOptions options) => new(
        new OnStepClient(new TestOptionsMonitor<OnStepOptions>(options), NullLogger<OnStepClient>.Instance),
        new TestOptionsMonitor<OnStepOptions>(options),
        NullLogger<OnStepCalibrationController>.Instance);

    private sealed class MockOnStep : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private readonly List<string> _commands = [];
        private readonly Task _server;

        public MockOnStep()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _server = RunAsync();
        }

        public int Port { get; }

        public async Task<IReadOnlyList<string>> StopAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            try { await _server; }
            catch (OperationCanceledException) { }
            return _commands;
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            _cts.Dispose();
        }

        private async Task RunAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    using var stream = client.GetStream();
                    var command = new StringBuilder();
                    var buffer = new byte[1];
                    while (!_cts.IsCancellationRequested)
                    {
                        var read = await stream.ReadAsync(buffer, _cts.Token);
                        if (read == 0) break;
                        command.Append((char)buffer[0]);
                        if (buffer[0] != (byte)'#') continue;

                        var text = command.ToString();
                        command.Clear();
                        _commands.Add(text);
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(ReplyFor(text)), _cts.Token);
                    }
                }
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
            catch (SocketException) when (_cts.IsCancellationRequested) { }
        }

        private static string ReplyFor(string command) => command switch
        {
            ":GVP#" => "OnStepX#",
            ":GVN#" => "1.0#",
            ":GU#" => "N#",
            ":A3#" => "1",
            ":A?#" => "321#",
            ":MS#" => "0",
            ":CM#" => "N/A#",
            _ when command.StartsWith(":Sa", StringComparison.Ordinal) => "1",
            _ when command.StartsWith(":Sz", StringComparison.Ordinal) => "1",
            _ when command.StartsWith(":Sr", StringComparison.Ordinal) => "1",
            _ when command.StartsWith(":Sd", StringComparison.Ordinal) => "1",
            _ => throw new InvalidOperationException($"Unexpected OnStep command {command}"),
        };
    }
}
