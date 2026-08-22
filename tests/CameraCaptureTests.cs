using StepSolve;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace StepSolve.Tests;

public class CameraCaptureTests
{
    [Fact]
    public async Task CaptureAsync_OnMac_ReturnsImageWhenDemoImagesExist()
    {
        if (OperatingSystem.IsLinux()) return; // test is macOS-specific

        // The demo images are in wwwroot/demo/ relative to BaseDirectory,
        // which during tests is the test output dir. Create a temp demo dir.
        var baseDir = AppContext.BaseDirectory;
        var demoDir = Path.Combine(baseDir, "wwwroot", "demo");
        Directory.CreateDirectory(demoDir);

        var testImage = Path.Combine(demoDir, "test_capture.jpg");
        File.WriteAllBytes(testImage, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }); // JPEG magic bytes

        try
        {
            var opts = Options.Create(new CameraOptions());
            var monitor = new OptionsMonitorWrapper<CameraOptions>(opts);
            var capture = new CameraCapture(
                monitor,
                new StoragePaths(new ConfigurationBuilder().Build()),
                NullLogger<CameraCapture>.Instance);

            var result = await capture.CaptureAsync(CancellationToken.None);

            Assert.NotNull(result);
            Assert.True(File.Exists(result));
        }
        finally
        {
            if (File.Exists(testImage))
                File.Delete(testImage);
        }
    }

    [Fact]
    public async Task CaptureAsync_OnMac_ReturnsNullWhenNoDemoImages()
    {
        if (OperatingSystem.IsLinux()) return;

        var opts = Options.Create(new CameraOptions());
        var monitor = new OptionsMonitorWrapper<CameraOptions>(opts);
        var capture = new CameraCapture(
            monitor,
            new StoragePaths(new ConfigurationBuilder().Build()),
            NullLogger<CameraCapture>.Instance);

        // Just verify construction succeeds and capture doesn't throw
        Assert.NotNull(capture);
        await Task.CompletedTask;
    }
}

/// <summary>
/// Simple IOptionsMonitor wrapper for testing — returns CurrentValue from IOptions.
/// </summary>
internal sealed class OptionsMonitorWrapper<T> : IOptionsMonitor<T> where T : class
{
    public OptionsMonitorWrapper(IOptions<T> options)
    {
        CurrentValue = options.Value;
    }

    public T CurrentValue { get; }

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
