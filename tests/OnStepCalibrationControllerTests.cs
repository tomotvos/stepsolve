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
    public async Task InitializeAsync_ProbesWhenOnStepIsEnabledAfterStartup()
    {
        await using var mount = new MockOnStep();
        var options = new TestOptionsMonitor<OnStepOptions>(new OnStepOptions
        {
            Enabled = false,
            Host = "127.0.0.1",
            Port = mount.Port,
            StartupPolicy = "probe",
        });
        var controller = new OnStepCalibrationController(
            new OnStepClient(options, NullLogger<OnStepClient>.Instance),
            options,
            NullLogger<OnStepCalibrationController>.Instance);

        await controller.InitializeAsync(CancellationToken.None);
        Assert.False(controller.Status.IsConnected);

        options.Set(new OnStepOptions
        {
            Enabled = true,
            Host = "127.0.0.1",
            Port = mount.Port,
            StartupPolicy = "probe",
        });
        await controller.InitializeAsync(CancellationToken.None);

        Assert.True(controller.Status.IsConnected);
        Assert.True(controller.Status.IsSafe);
    }

    [Fact]
    public async Task AutomaticCorrection_RequiresTwoStableSolvesAndUsesCurrentMountResidual()
    {
        await using var mount = new MockOnStep();
        var controller = CreateController(new OnStepOptions
        {
            Enabled = true,
            AutomaticCorrectionsEnabled = true,
            Host = "127.0.0.1",
            Port = mount.Port,
            StableSolveIntervalSeconds = 0,
            MaxAutomaticCorrectionDeg = 1,
            CorrectionIntervalMinutes = 15,
        });
        var solve = new SolveResult(180, 45, null, null, 0.99, TimeSpan.Zero, "stub");

        await controller.SubmitAutomaticCorrectionCandidateAsync(solve, CancellationToken.None);
        await WaitForAutomaticStabilityAsync();
        await controller.SubmitAutomaticCorrectionCandidateAsync(solve, CancellationToken.None);
        await controller.SubmitAutomaticCorrectionCandidateAsync(solve, CancellationToken.None);

        var commands = await mount.StopAsync();
        Assert.Equal(new[] { ":GU#", ":GR#", ":GD#", ":GU#", ":Sr12:00:00#", ":Sd+45*00:00#", ":CM#" }, commands);
    }

    [Fact]
    public async Task AutomaticCorrection_RejectsResidualAboveConfiguredMaximum()
    {
        await using var mount = new MockOnStep();
        var controller = CreateController(new OnStepOptions
        {
            Enabled = true,
            AutomaticCorrectionsEnabled = true,
            Host = "127.0.0.1",
            Port = mount.Port,
            StableSolveIntervalSeconds = 0,
            MaxAutomaticCorrectionDeg = 1,
        });
        var solve = new SolveResult(182, 45, null, null, 0.99, TimeSpan.Zero, "stub");

        await controller.SubmitAutomaticCorrectionCandidateAsync(solve, CancellationToken.None);
        await WaitForAutomaticStabilityAsync();
        await controller.SubmitAutomaticCorrectionCandidateAsync(solve, CancellationToken.None);

        var commands = await mount.StopAsync();
        Assert.Equal(new[] { ":GU#", ":GR#", ":GD#" }, commands);
    }

    [Fact]
    public async Task AutomaticCorrection_RejectsNonFiniteOnStepPosition()
    {
        await using var mount = new MockOnStep { RightAscensionReply = "NaN#" };
        var controller = CreateController(new OnStepOptions
        {
            Enabled = true,
            AutomaticCorrectionsEnabled = true,
            Host = "127.0.0.1",
            Port = mount.Port,
        });
        var solve = new SolveResult(180, 45, null, null, 0.99, TimeSpan.Zero, "stub");

        await controller.SubmitAutomaticCorrectionCandidateAsync(solve, CancellationToken.None);
        await WaitForAutomaticStabilityAsync();
        await controller.SubmitAutomaticCorrectionCandidateAsync(solve, CancellationToken.None);

        var commands = await mount.StopAsync();
        Assert.Equal(new[] { ":GU#", ":GR#", ":GD#" }, commands);
    }

    [Fact]
    public async Task AutomaticCorrection_DoesNotSyncWhileOnStepReportsMotion()
    {
        await using var mount = new MockOnStep { StatusReply = "pH#" };
        var controller = CreateController(new OnStepOptions
        {
            Enabled = true,
            AutomaticCorrectionsEnabled = true,
            Host = "127.0.0.1",
            Port = mount.Port,
            StableSolveIntervalSeconds = 0,
        });
        var solve = new SolveResult(180, 45, null, null, 0.99, TimeSpan.Zero, "stub");

        await controller.SubmitAutomaticCorrectionCandidateAsync(solve, CancellationToken.None);
        await WaitForAutomaticStabilityAsync();
        await controller.SubmitAutomaticCorrectionCandidateAsync(solve, CancellationToken.None);

        var commands = await mount.StopAsync();
        Assert.Equal(new[] { ":GU#" }, commands);
    }

    [Fact]
    public async Task AutomaticCorrection_DoesNotSyncWhileOnStepReportsGuiding()
    {
        await using var mount = new MockOnStep { StatusReply = "NG#" };
        var controller = CreateController(new OnStepOptions
        {
            Enabled = true,
            AutomaticCorrectionsEnabled = true,
            Host = "127.0.0.1",
            Port = mount.Port,
            StableSolveIntervalSeconds = 0,
        });
        var solve = new SolveResult(180, 45, null, null, 0.99, TimeSpan.Zero, "stub");

        await controller.SubmitAutomaticCorrectionCandidateAsync(solve, CancellationToken.None);
        await WaitForAutomaticStabilityAsync();
        await controller.SubmitAutomaticCorrectionCandidateAsync(solve, CancellationToken.None);

        var commands = await mount.StopAsync();
        Assert.Equal(new[] { ":GU#" }, commands);
    }

    [Fact]
    public async Task AutomaticCorrection_RequiresTwoHighConfidenceSolvesWithoutAnInterveningFailure()
    {
        await using var mount = new MockOnStep();
        var controller = CreateController(new OnStepOptions
        {
            Enabled = true,
            AutomaticCorrectionsEnabled = true,
            Host = "127.0.0.1",
            Port = mount.Port,
            StableSolveIntervalSeconds = 0,
        });
        var good = new SolveResult(180, 45, null, null, 0.99, TimeSpan.Zero, "stub");
        var lowConfidence = good with { Confidence = 0.5 };

        await controller.SubmitAutomaticCorrectionCandidateAsync(good, CancellationToken.None);
        await controller.SubmitAutomaticCorrectionCandidateAsync(lowConfidence, CancellationToken.None);
        await controller.SubmitAutomaticCorrectionCandidateAsync(good, CancellationToken.None);

        var commands = await mount.StopAsync();
        Assert.Empty(commands);
    }

    [Fact]
    public async Task AutomaticCorrection_ResetsWhenAConflictingSolveArrivesInsideStabilityInterval()
    {
        await using var mount = new MockOnStep();
        var controller = CreateController(new OnStepOptions
        {
            Enabled = true,
            AutomaticCorrectionsEnabled = true,
            Host = "127.0.0.1",
            Port = mount.Port,
        });
        var first = new SolveResult(180, 45, null, null, 0.99, TimeSpan.Zero, "stub");
        var conflicting = first with { RaDeg = 181 };

        await controller.SubmitAutomaticCorrectionCandidateAsync(first, CancellationToken.None);
        await controller.SubmitAutomaticCorrectionCandidateAsync(conflicting, CancellationToken.None);
        await WaitForAutomaticStabilityAsync();
        await controller.SubmitAutomaticCorrectionCandidateAsync(first, CancellationToken.None);

        var commands = await mount.StopAsync();
        Assert.Empty(commands);
    }

    [Fact]
    public async Task AutomaticCorrection_CancelsWhenOperatorDisablesItDuringSafetyChecks()
    {
        await using var mount = new MockOnStep();
        var options = new OnStepOptions
        {
            Enabled = true,
            AutomaticCorrectionsEnabled = true,
            Host = "127.0.0.1",
            Port = mount.Port,
            StableSolveIntervalSeconds = 0,
        };
        var controller = CreateController(options);
        mount.BeforeReply = command =>
        {
            if (command == ":GU#")
                options.AutomaticCorrectionsEnabled = false;
        };
        var solve = new SolveResult(180, 45, null, null, 0.99, TimeSpan.Zero, "stub");

        await controller.SubmitAutomaticCorrectionCandidateAsync(solve, CancellationToken.None);
        await WaitForAutomaticStabilityAsync();
        await controller.SubmitAutomaticCorrectionCandidateAsync(solve, CancellationToken.None);

        var commands = await mount.StopAsync();
        Assert.Equal(new[] { ":GU#", ":GR#", ":GD#", ":GU#" }, commands);
    }

    [Fact]
    public async Task Simulation_IsTemporaryAndCreatesStableSolvesFromOnStepPosition()
    {
        await using var mount = new MockOnStep();
        var controller = CreateController(new OnStepOptions
        {
            Enabled = true,
            Host = "127.0.0.1",
            Port = mount.Port,
            CalibrationSettleSeconds = 0,
            StableSolveIntervalSeconds = 0,
        });

        Assert.True((await controller.SetSimulationAsync(true, "calibrate", CancellationToken.None)).Success);
        Assert.True(controller.Status.SimulationEnabled);
        Assert.True((await controller.StartAsync(true, "calibrate", CancellationToken.None)).Success);
        await AdvanceToSolveGateAsync(controller);

        var first = await controller.CreateSimulatedSolveAsync(CancellationToken.None);
        var second = await controller.CreateSimulatedSolveAsync(CancellationToken.None);
        Assert.Equal("onstep-simulation", first.SolverName);
        Assert.InRange(first.RaDeg, 179.99, 180.01);
        Assert.InRange(second.DecDeg, 44.99, 45.01);

        await controller.SubmitFreshSolveAsync(first, CancellationToken.None);
        await controller.SubmitFreshSolveAsync(second, CancellationToken.None);
        Assert.Equal("AwaitingAcceptance", controller.Status.State);

        var commands = await mount.StopAsync();
        Assert.Contains(":GR#", commands);
        Assert.Contains(":GD#", commands);
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
        Assert.Equal("ReturningHome", controller.Status.State);
        Assert.Equal(1, controller.Status.CurrentPoint);
        Assert.Equal(0, controller.Status.RequestedAzimuthDeg);
        Assert.Equal(45, controller.Status.RequestedAltitudeDeg);

        await controller.TickAsync(CancellationToken.None); // Home confirmed → point 1 GoTo
        Assert.Equal("WaitingForGoto", controller.Status.State);
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
            ":GVP#", ":GVN#", ":GU#", ":So90#", ":hC#", ":GU#", ":A3#",
            ":Sa+45*00'00#", ":Sz000*00'00#", ":MA#",
            ":GU#",
            ":Sr06:40:00#", ":Sd+45*00:00#", ":A+#",
            ":A?#",
            ":Sa+60*00'00#", ":Sz060*00'00#", ":MA#",
        }, commands);
    }

    [Fact]
    public async Task StartAsync_AtHomeBeginsAlignmentWithoutCommandingReturnHome()
    {
        await using var mount = new MockOnStep();
        var controller = CreateController(new OnStepOptions
        {
            Enabled = true,
            Host = "127.0.0.1",
            Port = mount.Port,
        });

        var started = await controller.StartAsync(
            true, CalibrationHomeStrategy.AtHome, "calibrate", CancellationToken.None);

        Assert.True(started.Success);
        Assert.Equal("WaitingForGoto", controller.Status.State);
        var commands = await mount.StopAsync();
        Assert.DoesNotContain(":hC#", commands);
        Assert.Contains(":A3#", commands);
    }

    [Fact]
    public async Task RecoverHome_UsesTwoStableSolvesThenSyncsBeforeReturningHome()
    {
        await using var mount = new MockOnStep();
        var controller = CreateController(new OnStepOptions
        {
            Enabled = true,
            Host = "127.0.0.1",
            Port = mount.Port,
            StableSolveIntervalSeconds = 0,
        });
        var solve = new SolveResult(100, 45, null, null, 0.99, TimeSpan.Zero, "stub");

        var started = await controller.StartAsync(
            true, CalibrationHomeStrategy.RecoverHome, "calibrate", CancellationToken.None);

        Assert.True(started.Success);
        Assert.Equal("RecoveringHomeSolves", controller.Status.State);
        Assert.True(controller.NeedsFreshSolve);

        await controller.SubmitFreshSolveAsync(solve, CancellationToken.None);
        Assert.True(controller.WantsImmediateFollowUpSolve);
        await controller.SubmitFreshSolveAsync(solve, CancellationToken.None);

        Assert.Equal("ReturningHome", controller.Status.State);
        await controller.TickAsync(CancellationToken.None);
        Assert.Equal("WaitingForGoto", controller.Status.State);

        var commands = await mount.StopAsync();
        Assert.Contains(":Sr06:40:00#", commands);
        Assert.Contains(":Sd+45*00:00#", commands);
        Assert.Contains(":CM#", commands);
        Assert.Contains(":hC#", commands);
        Assert.Contains(":A3#", commands);
    }

    [Fact]
    public async Task StableSolveInterval_DoesNotDelayTheImmediateConfirmationSolve()
    {
        await using var mount = new MockOnStep();
        var controller = CreateController(new OnStepOptions
        {
            Enabled = true,
            Host = "127.0.0.1",
            Port = mount.Port,
            CalibrationSettleSeconds = 0,
            StableSolveIntervalSeconds = 60,
        });

        Assert.True((await controller.StartAsync(true, "calibrate", CancellationToken.None)).Success);
        await AdvanceToSolveGateAsync(controller);

        var solve = new SolveResult(100, 45, null, null, 0.99, TimeSpan.Zero, "stub");
        await controller.SubmitFreshSolveAsync(solve, CancellationToken.None);
        Assert.True(controller.WantsImmediateFollowUpSolve);

        await controller.SubmitFreshSolveAsync(solve, CancellationToken.None);

        Assert.Equal("AwaitingAcceptance", controller.Status.State);
    }

    [Fact]
    public async Task CompletedAlignment_ClosesTheSessionConnectionAndMarksStatusDisconnected()
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
        var solve = new SolveResult(100, 45, null, null, 0.99, TimeSpan.Zero, "stub");

        Assert.True((await controller.StartAsync(true, "calibrate", CancellationToken.None)).Success);
        await controller.TickAsync(CancellationToken.None); // Home confirmed → point 1 GoTo
        for (var point = 0; point < 3; point++)
        {
            await controller.TickAsync(CancellationToken.None);
            await controller.TickAsync(CancellationToken.None);
            await controller.SubmitFreshSolveAsync(solve, CancellationToken.None);
            await controller.SubmitFreshSolveAsync(solve, CancellationToken.None);
            Assert.True((await controller.AcceptAsync("calibrate", CancellationToken.None)).Success);
        }

        Assert.Equal("Completed", controller.Status.State);
        Assert.False(controller.Status.IsConnected);
        Assert.False(controller.Status.IsSafe);
        Assert.Contains("connection closed", controller.Status.Message, StringComparison.OrdinalIgnoreCase);
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
        await AdvanceToSolveGateAsync(controller);
        await controller.SubmitFreshSolveAsync(new SolveResult(0, 0, null, null, 0, TimeSpan.Zero, "stub"), CancellationToken.None);

        Assert.Equal("WaitingForGoto", controller.Status.State);
        Assert.Equal(2, controller.Status.Attempt);
        Assert.Equal(10, controller.Status.RequestedAzimuthDeg);

        var commands = await mount.StopAsync();
        Assert.Contains(":Sz010*00'00#", commands);
    }

    private static OnStepCalibrationController CreateController(OnStepOptions options)
    {
        // Production target defaults are supplied by appsettings.json. Unit
        // tests construct the POCO directly, so supply the equivalent plan.
        if (options.CalibrationTargets.Count == 0)
        {
            options.CalibrationTargets =
            [
                new(0, 45),
                new(60, 60),
                new(90, 80),
            ];
        }

        return new OnStepCalibrationController(
            new OnStepClient(new TestOptionsMonitor<OnStepOptions>(options), NullLogger<OnStepClient>.Instance),
            new TestOptionsMonitor<OnStepOptions>(options),
            NullLogger<OnStepCalibrationController>.Instance);
    }

    private static async Task AdvanceToSolveGateAsync(OnStepCalibrationController controller)
    {
        await controller.TickAsync(CancellationToken.None); // Home confirmed → GoTo
        await controller.TickAsync(CancellationToken.None); // GoTo complete → settling
        await controller.TickAsync(CancellationToken.None); // settle → solve gate
        Assert.True(controller.NeedsFreshSolve);
    }

    private static Task WaitForAutomaticStabilityAsync() => Task.Delay(TimeSpan.FromMilliseconds(1050));

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
        public string StatusReply { get; set; } = "N#";
        public string RightAscensionReply { get; set; } = "12:00:00#";
        public string DeclinationReply { get; set; } = "+45*00:00#";
        public Action<string>? BeforeReply { get; set; }

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
                        BeforeReply?.Invoke(text);
                        var reply = ReplyFor(text);
                        if (reply.Length > 0)
                            await stream.WriteAsync(Encoding.ASCII.GetBytes(reply), _cts.Token);
                    }
                }
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
            catch (SocketException) when (_cts.IsCancellationRequested) { }
        }

        private string ReplyFor(string command) => command switch
        {
            ":GVP#" => "OnStepX#",
            ":GVN#" => "1.0#",
            ":GU#" => StatusReply,
            ":GR#" => RightAscensionReply,
            ":GD#" => DeclinationReply,
            ":A3#" => "1",
            ":So90#" => "1",
            ":CM#" => "N/A#",
            ":hC#" => SetHomeAndReturnNoReply(),
            ":A?#" => "321#",
            ":MA#" => "0",
            ":A+#" => "1",
            _ when command.StartsWith(":Sa", StringComparison.Ordinal) => "1",
            _ when command.StartsWith(":Sz", StringComparison.Ordinal) => "1",
            _ when command.StartsWith(":Sr", StringComparison.Ordinal) => "1",
            _ when command.StartsWith(":Sd", StringComparison.Ordinal) => "1",
            _ => throw new InvalidOperationException($"Unexpected OnStep command {command}"),
        };

        private string SetHomeAndReturnNoReply()
        {
            StatusReply = "NH#";
            return string.Empty;
        }
    }
}
