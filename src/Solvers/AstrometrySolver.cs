using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace StepSolve.Solvers;

public sealed partial class AstrometrySolver : ISolver
{
    private readonly IOptionsMonitor<SolverOptions> _options;
    private readonly StoragePaths _storagePaths;
    private readonly ILogger<AstrometrySolver> _logger;
    private SolveResult? _lastResult;
    private DateTimeOffset _lastSolveTime;

    public AstrometrySolver(
        IOptionsMonitor<SolverOptions> options,
        StoragePaths storagePaths,
        ILogger<AstrometrySolver> logger)
    {
        _options = options;
        _storagePaths = storagePaths;
        _logger = logger;
    }

    public async Task<SolveResult> SolveAsync(string imagePath, SolveHints? hints, CancellationToken ct)
    {
        if (!File.Exists(imagePath))
            throw new FileNotFoundException("Image not found", imagePath);

        var opts = _options.CurrentValue;
        var astroOpts = opts.Astrometry;
        var sw = Stopwatch.StartNew();

        // Phase 1: Hinted solve (if we have recent hints)
        var effectiveHints = GetEffectiveHints(hints, opts.HintTimeout);
        Directory.CreateDirectory(_storagePaths.ImagesDirectory);
        var xyPath = Path.Combine(
            _storagePaths.ImagesDirectory,
            Path.GetFileNameWithoutExtension(imagePath) + ".xy");

        var result = await RunSolveField(imagePath, astroOpts, effectiveHints, xyPath, _storagePaths.ImagesDirectory, sw, ct);
        if (result.IsValid)
        {
            CacheResult(result);
            return result;
        }

        // Phase 2: Unhinted fallback using XY file
        if (opts.EnableFallback && File.Exists(xyPath))
        {
            _logger.LogInformation("Phase 1 failed, attempting unhinted fallback with XY file");
            result = await RunSolveField(xyPath, astroOpts, hints: null, keepXyPath: null, _storagePaths.ImagesDirectory, sw, ct);
            if (result.IsValid)
            {
                CacheResult(result);
                return result;
            }
        }

        _logger.LogWarning("Solve failed for {Image}", imagePath);
        return new SolveResult(0, 0, null, null, 0, sw.Elapsed, "astrometry");
    }

    private SolveHints? GetEffectiveHints(SolveHints? provided, int hintTimeoutSec)
    {
        if (provided != null) return provided;

        if (_lastResult.HasValue
            && _lastResult.Value.IsValid
            && (DateTimeOffset.UtcNow - _lastSolveTime).TotalSeconds < hintTimeoutSec)
        {
            return new SolveHints(_lastResult.Value.RaDeg, _lastResult.Value.DecDeg,
                _options.CurrentValue.SolveRadius);
        }

        return null;
    }

    private void CacheResult(SolveResult result)
    {
        _lastResult = result;
        _lastSolveTime = DateTimeOffset.UtcNow;
    }

    private async Task<SolveResult> RunSolveField(
        string inputPath,
        AstrometryOptions opts,
        SolveHints? hints,
        string? keepXyPath,
        string outputDirectory,
        Stopwatch sw,
        CancellationToken ct)
    {
        var fov = _options.CurrentValue.FovEstimateDeg > 0 ? _options.CurrentValue.FovEstimateDeg : (double?)null;
        var args = BuildArgs(inputPath, opts, hints, keepXyPath, fov, outputDirectory);
        _logger.LogDebug("Running: {Cmd} {Args}", opts.SolveFieldPath, args);

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = opts.SolveFieldPath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            process.Start();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(opts.Timeout));

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                _logger.LogWarning("solve-field exited with code {Code}: {Stderr}",
                    process.ExitCode, stderr.Trim());
                return new SolveResult(0, 0, null, null, 0, sw.Elapsed, "astrometry");
            }

            return ParseOutput(stdout, sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("solve-field timed out after {Timeout}s", opts.Timeout);
            return new SolveResult(0, 0, null, null, 0, sw.Elapsed, "astrometry");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "solve-field execution failed");
            return new SolveResult(0, 0, null, null, 0, sw.Elapsed, "astrometry");
        }
    }

    internal static string BuildArgs(
        string inputPath,
        AstrometryOptions opts,
        SolveHints? hints,
        string? keepXyPath,
        double? fovEstimateDeg = null,
        string? outputDirectory = null)
    {
        var parts = new List<string>
        {
            inputPath,
            "--overwrite",
            "--no-plots",
            "--new-fits", "none",
            "--sigma", opts.Sigma.ToString(),
            "--depth", opts.Depth.ToString(),
            "--uniformize", "0",
            "--no-remove-lines",
            "--match", "none",
            "--corr", "none",
            "--rdls", "none",
        };

        if (!string.IsNullOrEmpty(opts.IndexPath))
            parts.AddRange(["--index-dir", opts.IndexPath]);

        if (!string.IsNullOrEmpty(outputDirectory))
            parts.AddRange(["--dir", outputDirectory]);

        if (fovEstimateDeg.HasValue)
        {
            var low = (fovEstimateDeg.Value * 0.5).ToString("F2");
            var high = (fovEstimateDeg.Value * 1.5).ToString("F2");
            parts.AddRange(["--scale-low", low, "--scale-high", high, "--scale-units", "degwidth"]);
        }

        if (hints != null)
        {
            parts.AddRange(["--ra", hints.RaDeg.ToString("F6"),
                            "--dec", hints.DecDeg.ToString("F6"),
                            "--radius", hints.RadiusDeg.ToString("F1")]);
        }

        if (keepXyPath != null)
            parts.AddRange(["--keep-xylist", keepXyPath]);

        return string.Join(" ", parts);
    }

    internal static SolveResult ParseOutput(string stdout, TimeSpan elapsed)
    {
        double ra = 0, dec = 0, confidence = 0;

        foreach (var rawLine in stdout.Split('\n'))
        {
            // Strip timestamp prefix like [10:42:02]
            var line = TimestampRegex().Replace(rawLine, "");

            var raDecMatch = RaDecPrimary().Match(line);
            if (!raDecMatch.Success)
                raDecMatch = RaDecFallback().Match(line);

            if (raDecMatch.Success)
            {
                ra = double.Parse(raDecMatch.Groups[1].Value);
                dec = double.Parse(raDecMatch.Groups[2].Value);
            }

            if (line.Contains("Confidence:"))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    if (parts[i] == "Confidence:" && double.TryParse(parts[i + 1], out var c))
                    {
                        confidence = c;
                        break;
                    }
                }
            }
        }

        return new SolveResult(ra, dec, null, null, confidence, elapsed, "astrometry");
    }

    [GeneratedRegex(@"^\[\d{2}:\d{2}:\d{2}\]\s*")]
    private static partial Regex TimestampRegex();

    [GeneratedRegex(@"RA,Dec\s*=\s*\(([-\d.]+),\s*([-\d.]+)\)")]
    private static partial Regex RaDecPrimary();

    [GeneratedRegex(@"Field center: \(RA,Dec\) = \(([-\d.]+),\s*([-\d.]+)\)")]
    private static partial Regex RaDecFallback();
}
