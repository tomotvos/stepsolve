using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace StepSolve.Solvers;

public abstract class PythonSolverBase : ISolver, IDisposable
{
    private readonly string _pythonPath;
    private readonly string _scriptPath;
    private readonly string _indexPath;
    private readonly int _timeoutSec;
    private readonly string _solverName;
    protected readonly ILogger _logger;

    private Process? _process;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _disposed;

    protected PythonSolverBase(
        string pythonPath,
        string scriptPath,
        string indexPath,
        int timeoutSec,
        string solverName,
        ILogger logger)
    {
        _pythonPath = pythonPath;
        _scriptPath = scriptPath;
        _indexPath = indexPath;
        _timeoutSec = timeoutSec;
        _solverName = solverName;
        _logger = logger;
    }

    public async Task<SolveResult> SolveAsync(string imagePath, SolveHints? hints, CancellationToken ct)
    {
        if (!File.Exists(imagePath))
            throw new FileNotFoundException("Image not found", imagePath);

        var sw = Stopwatch.StartNew();

        await _lock.WaitAsync(ct);
        try
        {
            var process = await EnsureProcessAsync(ct);
            if (process == null)
                return Fail(sw);

            return await SendReceiveAsync(process, imagePath, hints, sw, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<Process?> EnsureProcessAsync(CancellationToken ct)
    {
        if (_process != null && !_process.HasExited)
            return _process;

        TerminateAndDispose(_process);
        _process = null;

        _logger.LogInformation("Starting {Solver} subprocess", _solverName);
        Process proc;
        try
        {
            proc = LaunchProcess();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch {Solver} subprocess", _solverName);
            return null;
        }

        using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        startupCts.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var line = await proc.StandardOutput.ReadLineAsync(startupCts.Token);
            if (line == null || !IsReadySignal(line))
            {
                _logger.LogError("{Solver} subprocess unexpected startup output: {Line}", _solverName, line);
                TerminateAndDispose(proc);
                return null;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TerminateAndDispose(proc);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Solver} subprocess failed to send ready signal", _solverName);
            TerminateAndDispose(proc);
            return null;
        }

        _logger.LogInformation("{Solver} subprocess ready", _solverName);
        _process = proc;
        return _process;
    }

    private static bool IsReadySignal(string line) =>
        line.Contains("\"ready\"") && line.Contains("true");

    protected virtual Process LaunchProcess()
    {
        var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _pythonPath,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };
        proc.StartInfo.ArgumentList.Add(_scriptPath);
        proc.StartInfo.ArgumentList.Add(_indexPath);
        proc.Start();
        return proc;
    }

    private async Task<SolveResult> SendReceiveAsync(
        Process process, string imagePath, SolveHints? hints, Stopwatch sw, CancellationToken ct)
    {
        var request = JsonSerializer.Serialize(new PipeRequest(
            imagePath,
            hints?.RaDeg,
            hints?.DecDeg,
            hints?.RadiusDeg));

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_timeoutSec));

            await process.StandardInput.WriteLineAsync(request.AsMemory(), timeoutCts.Token);
            await process.StandardInput.FlushAsync(timeoutCts.Token);

            var response = await process.StandardOutput.ReadLineAsync(timeoutCts.Token);
            if (response == null)
            {
                _logger.LogWarning("{Solver} subprocess closed stdout unexpectedly", _solverName);
                TerminateAndNull();
                return Fail(sw);
            }

            return ParseResponse(response, sw);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("{Solver} solve timed out after {Sec}s", _solverName, _timeoutSec);
            TerminateAndNull();
            return Fail(sw);
        }
        catch (OperationCanceledException)
        {
            TerminateAndNull();
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Solver} pipe error", _solverName);
            TerminateAndNull();
            return Fail(sw);
        }
    }

    private SolveResult ParseResponse(string json, Stopwatch sw)
    {
        try
        {
            var resp = JsonSerializer.Deserialize<PipeResponse>(json);
            if (resp == null) return Fail(sw);
            if (resp.Error != null)
            {
                _logger.LogWarning("{Solver} returned solve error: {Error}", _solverName, resp.Error);
                return Fail(sw);
            }
            return new SolveResult(resp.RaDeg, resp.DecDeg, null, null, resp.Confidence, sw.Elapsed, _solverName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Solver} failed to parse response: {Json}", _solverName, json);
            return Fail(sw);
        }
    }

    private SolveResult Fail(Stopwatch sw) => new(0, 0, null, null, 0, sw.Elapsed, _solverName);

    private void TerminateAndNull()
    {
        TerminateAndDispose(_process);
        _process = null;
    }

    private static void TerminateAndDispose(Process? proc)
    {
        if (proc == null) return;
        try { proc.Kill(entireProcessTree: true); } catch { }
        proc.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        TerminateAndNull();
        _lock.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed record PipeRequest(
        [property: JsonPropertyName("image_path")] string ImagePath,
        [property: JsonPropertyName("ra_hint")] double? RaHint,
        [property: JsonPropertyName("dec_hint")] double? DecHint,
        [property: JsonPropertyName("radius_deg")] double? RadiusDeg);

    private sealed record PipeResponse(
        [property: JsonPropertyName("ra_deg")] double RaDeg = 0,
        [property: JsonPropertyName("dec_deg")] double DecDeg = 0,
        [property: JsonPropertyName("confidence")] double Confidence = 0,
        [property: JsonPropertyName("solve_time_ms")] double SolveTimeMs = 0,
        [property: JsonPropertyName("error")] string? Error = null);
}
