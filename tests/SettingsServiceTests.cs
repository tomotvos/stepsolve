using StepSolve;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace StepSolve.Tests;

public class SettingsServiceTests
{
    private static SettingsService CreateService(string tempPath, Dictionary<string, string?>? initialConfig = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(initialConfig ?? new Dictionary<string, string?>
            {
                ["StepSolve:Mode"] = "demo",
                ["Solver:Backend"] = "astrometry",
                ["Camera:ShutterUs"] = "1000000",
                ["OnStep:Enabled"] = "false",
            })
            .Build();

        return new SettingsService(config, tempPath);
    }

    [Fact]
    public void Validate_ValidSettings_ReturnsNull()
    {
        var svc = CreateService(Path.GetTempFileName());
        var settings = new Dictionary<string, Dictionary<string, object>>
        {
            ["Solver"] = new() { ["Backend"] = "cedar", ["HintTimeout"] = "15" },
            ["Camera"] = new() { ["ShutterUs"] = "500000" },
        };

        Assert.Null(svc.Validate(settings));
    }

    [Fact]
    public void Validate_InvalidBackend_ReturnsError()
    {
        var svc = CreateService(Path.GetTempFileName());
        var settings = new Dictionary<string, Dictionary<string, object>>
        {
            ["Solver"] = new() { ["Backend"] = "invalid" },
        };

        var error = svc.Validate(settings);
        Assert.NotNull(error);
        Assert.Contains("Backend", error);
    }

    [Fact]
    public void Validate_InvalidMode_ReturnsError()
    {
        var svc = CreateService(Path.GetTempFileName());
        var settings = new Dictionary<string, Dictionary<string, object>>
        {
            ["StepSolve"] = new() { ["Mode"] = "turbo" },
        };

        var error = svc.Validate(settings);
        Assert.NotNull(error);
        Assert.Contains("Mode", error);
    }

    [Fact]
    public void Validate_NegativeShutter_ReturnsError()
    {
        var svc = CreateService(Path.GetTempFileName());
        var settings = new Dictionary<string, Dictionary<string, object>>
        {
            ["Camera"] = new() { ["ShutterUs"] = "-100" },
        };

        var error = svc.Validate(settings);
        Assert.NotNull(error);
        Assert.Contains("ShutterUs", error);
    }

    [Fact]
    public void Validate_InvalidPort_ReturnsError()
    {
        var svc = CreateService(Path.GetTempFileName());
        var settings = new Dictionary<string, Dictionary<string, object>>
        {
            ["OnStep"] = new() { ["Port"] = "99999" },
        };

        var error = svc.Validate(settings);
        Assert.NotNull(error);
        Assert.Contains("Port", error);
    }

    [Fact]
    public void Validate_NegativeGain_ReturnsError()
    {
        var svc = CreateService(Path.GetTempFileName());
        var settings = new Dictionary<string, Dictionary<string, object>>
        {
            ["Camera"] = new() { ["Gain"] = "-1" },
        };

        var error = svc.Validate(settings);
        Assert.NotNull(error);
        Assert.Contains("Gain", error);
    }

    [Fact]
    public void Validate_ZeroGain_IsValid()
    {
        var svc = CreateService(Path.GetTempFileName());
        var settings = new Dictionary<string, Dictionary<string, object>>
        {
            ["Camera"] = new() { ["Gain"] = "0" },
        };

        Assert.Null(svc.Validate(settings));
    }

    [Fact]
    public void Validate_InvalidSyncMode_ReturnsError()
    {
        var svc = CreateService(Path.GetTempFileName());
        var settings = new Dictionary<string, Dictionary<string, object>>
        {
            ["OnStep"] = new() { ["SyncMode"] = "invalid" },
        };

        var error = svc.Validate(settings);
        Assert.NotNull(error);
        Assert.Contains("SyncMode", error);
    }

    [Fact]
    public void Validate_UnknownKeysPassThrough()
    {
        var svc = CreateService(Path.GetTempFileName());
        var settings = new Dictionary<string, Dictionary<string, object>>
        {
            ["Custom"] = new() { ["Whatever"] = "anything" },
        };

        Assert.Null(svc.Validate(settings));
    }

    [Fact]
    public void ApplyAndPersist_WritesToDisk()
    {
        var tempPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempPath, "{}");
            var svc = CreateService(tempPath);
            var settings = new Dictionary<string, Dictionary<string, object>>
            {
                ["Solver"] = new() { ["Backend"] = "cedar" },
            };

            var error = svc.ApplyAndPersist(settings);

            Assert.Null(error);
            var json = File.ReadAllText(tempPath);
            Assert.Contains("cedar", json);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void ApplyAndPersist_UpdatesInMemoryConfig()
    {
        var tempPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempPath, "{}");
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Solver:Backend"] = "astrometry",
                })
                .Build();

            var svc = new SettingsService(config, tempPath);
            var settings = new Dictionary<string, Dictionary<string, object>>
            {
                ["Solver"] = new() { ["Backend"] = "tetra3" },
            };

            svc.ApplyAndPersist(settings);

            Assert.Equal("tetra3", config["Solver:Backend"]);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void ApplyAndPersist_ReloadsOnStepOptionsMonitor()
    {
        var tempPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempPath, "{ \"OnStep\": { \"Enabled\": false } }");
            var config = new ConfigurationBuilder()
                .AddJsonFile(tempPath, optional: false, reloadOnChange: false)
                .Build();
            var services = new ServiceCollection();
            services.Configure<OnStepOptions>(config.GetSection(OnStepOptions.Section));
            using var provider = services.BuildServiceProvider();
            var monitor = provider.GetRequiredService<IOptionsMonitor<OnStepOptions>>();
            var svc = new SettingsService(config, tempPath);

            Assert.False(monitor.CurrentValue.Enabled);
            Assert.Null(svc.ApplyAndPersist(new Dictionary<string, Dictionary<string, object>>
            {
                ["OnStep"] = new() { ["Enabled"] = true },
            }));

            Assert.True(monitor.CurrentValue.Enabled);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void ApplyAndPersist_InvalidSettings_DoesNotPersist()
    {
        var tempPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempPath, "{}");
            var svc = CreateService(tempPath);
            var settings = new Dictionary<string, Dictionary<string, object>>
            {
                ["Solver"] = new() { ["Backend"] = "bad" },
            };

            var error = svc.ApplyAndPersist(settings);

            Assert.NotNull(error);
            // File should still be just "{}"
            var json = File.ReadAllText(tempPath);
            Assert.DoesNotContain("bad", json);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void ApplyAndPersist_PreservesExistingKeys()
    {
        var tempPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempPath, "{\"Logging\":{\"LogLevel\":{\"Default\":\"Information\"}},\"Solver\":{\"Backend\":\"astrometry\"}}");
            var svc = CreateService(tempPath);
            var settings = new Dictionary<string, Dictionary<string, object>>
            {
                ["Camera"] = new() { ["ShutterUs"] = "500000" },
            };

            svc.ApplyAndPersist(settings);

            var json = File.ReadAllText(tempPath);
            // Existing Logging section should still be present
            Assert.Contains("Logging", json);
            Assert.Contains("Information", json);
            // New camera setting should be present
            Assert.Contains("500000", json);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void ApplyAndPersist_PreservesJsonTypes()
    {
        var tempPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempPath, "{}");
            var svc = CreateService(tempPath);
            var settings = new Dictionary<string, Dictionary<string, object>>
            {
                ["OnStep"] = new()
                {
                    ["Enabled"] = "true",
                    ["Port"] = "9998",
                    ["MaxSyncDeltaDeg"] = "5.0"
                },
            };

            svc.ApplyAndPersist(settings);

            var json = File.ReadAllText(tempPath);
            // Booleans and numbers should be written as JSON primitives, not strings
            Assert.Contains("true", json);    // not "true"
            Assert.Contains("9998", json);     // not "9998"
            Assert.Contains("5", json);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void ApplyAndPersist_CreatesFileIfMissing()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"stepsolve_test_{Guid.NewGuid():N}.json");
        try
        {
            Assert.False(File.Exists(tempPath));

            var svc = CreateService(tempPath);
            var settings = new Dictionary<string, Dictionary<string, object>>
            {
                ["Solver"] = new() { ["Backend"] = "astrometry" },
            };

            var error = svc.ApplyAndPersist(settings);

            Assert.Null(error);
            Assert.True(File.Exists(tempPath));
            var json = File.ReadAllText(tempPath);
            Assert.Contains("astrometry", json);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }
}
