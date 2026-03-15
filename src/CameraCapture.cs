using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace StepSolve;

public interface ICameraCapture
{
    Task<string?> CaptureAsync(CancellationToken ct);
}

/// <summary>
/// Captures images by shelling out to rpicam-still on Pi.
/// On macOS, returns a mock/test image path if available.
/// </summary>
public sealed class CameraCapture : ICameraCapture
{
    private readonly IOptionsMonitor<CameraOptions> _options;
    private readonly ILogger<CameraCapture> _logger;
    private readonly string _outputDir;
    private readonly bool _isLinux;

    public CameraCapture(IOptionsMonitor<CameraOptions> options, ILogger<CameraCapture> logger)
    {
        _options = options;
        _logger = logger;
        _outputDir = Path.Combine(AppContext.BaseDirectory, "images");
        Directory.CreateDirectory(_outputDir);
        _isLinux = OperatingSystem.IsLinux();
    }

    public async Task<string?> CaptureAsync(CancellationToken ct)
    {
        if (!_isLinux)
        {
            return GetMockImage();
        }

        var opts = _options.CurrentValue;
        var outputPath = Path.Combine(_outputDir, $"capture.{opts.OutputFormat}");

        var args = string.Join(" ",
            "-o", outputPath,
            "--shutter", opts.ShutterUs.ToString(),
            "--gain", opts.Gain.ToString("F1"),
            "--width", opts.Width.ToString(),
            "--height", opts.Height.ToString(),
            "--immediate",
            "--nopreview");

        _logger.LogDebug("Capturing: rpicam-still {Args}", args);

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "rpicam-still",
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            process.Start();
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
            {
                var stderr = await process.StandardError.ReadToEndAsync(ct);
                _logger.LogError("rpicam-still failed (exit {Code}): {Error}", process.ExitCode, stderr);
                return null;
            }

            _logger.LogDebug("Image captured: {Path}", outputPath);
            return outputPath;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Camera capture failed");
            return null;
        }
    }

    private string? GetMockImage()
    {
        // Look for a test image in the images directory
        var mockPath = Path.Combine(_outputDir, "test.jpg");
        if (File.Exists(mockPath))
        {
            _logger.LogDebug("Using mock image: {Path}", mockPath);
            return mockPath;
        }

        _logger.LogDebug("No mock image available at {Path}, skipping capture", mockPath);
        return null;
    }
}
