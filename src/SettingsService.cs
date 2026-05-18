using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace StepSolve;

/// <summary>
/// Persists configuration changes to appsettings.json so they survive restarts.
/// Also validates settings before applying them.
/// </summary>
public sealed class SettingsService
{
    private readonly IConfiguration _config;
    private readonly string _settingsPath;
    private readonly Lock _fileLock = new();

    private static readonly HashSet<string> ValidBackends = new(StringComparer.OrdinalIgnoreCase)
    {
        "astrometry", "cedar", "tetra3"
    };

    private static readonly HashSet<string> ValidModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "solve", "demo", "idle", "calibrate"
    };

    private static readonly HashSet<string> ValidSyncModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "sync", "align"
    };

    public SettingsService(IConfiguration config)
    {
        _config = config;
        _settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.runtime.json");
    }

    // Constructor for testing — allows specifying the settings file path
    internal SettingsService(IConfiguration config, string settingsPath)
    {
        _config = config;
        _settingsPath = settingsPath;
    }

    /// <summary>
    /// Validate and apply settings. Returns null on success, or an error message.
    /// </summary>
    public string? ApplyAndPersist(Dictionary<string, Dictionary<string, object>> settings)
    {
        // Validate first — don't partially apply if there's an error
        var error = Validate(settings);
        if (error != null)
            return error;

        // Apply to in-memory IConfiguration
        foreach (var (section, values) in settings)
        {
            foreach (var (key, value) in values)
            {
                _config[$"{section}:{key}"] = value?.ToString();
            }
        }

        // Persist to disk
        Persist(settings);
        return null;
    }

    internal string? Validate(Dictionary<string, Dictionary<string, object>> settings)
    {
        foreach (var (section, values) in settings)
        {
            foreach (var (key, rawValue) in values)
            {
                var value = rawValue?.ToString() ?? "";
                var fullKey = $"{section}:{key}";

                var error = fullKey switch
                {
                    "StepSolve:Mode" => ValidModes.Contains(value) ? null : "Mode must be solve, demo, idle, or calibrate",
                    "StepSolve:WebPort" => ValidPort(value),
                    "StepSolve:Lx200Port" => ValidPort(value),
                    "Solver:Backend" => ValidBackends.Contains(value) ? null : "Backend must be astrometry, cedar, or tetra3",
                    "Solver:FovEstimateDeg" => ValidNonNegativeDouble(value, "FovEstimateDeg"),
                    "Solver:HintTimeout" => ValidPositiveInt(value, "HintTimeout"),
                    "Solver:SolveRadius" => ValidPositiveDouble(value, "SolveRadius"),
                    "Camera:ShutterUs" => ValidPositiveInt(value, "ShutterUs"),
                    "Camera:Gain" => ValidNonNegativeDouble(value, "Gain"),
                    "Camera:Width" => ValidPositiveInt(value, "Width"),
                    "Camera:Height" => ValidPositiveInt(value, "Height"),
                    "OnStep:Port" => ValidPort(value),
                    "OnStep:MaxSyncDeltaDeg" => ValidPositiveDouble(value, "MaxSyncDeltaDeg"),
                    "OnStep:SyncMode" => ValidSyncModes.Contains(value) ? null : "SyncMode must be sync or align",
                    _ => null // Unknown keys pass through
                };

                if (error != null)
                    return error;
            }
        }

        return null;
    }

    private void Persist(Dictionary<string, Dictionary<string, object>> settings)
    {
        lock (_fileLock)
        {
            JsonNode root;
            if (File.Exists(_settingsPath))
            {
                var existing = File.ReadAllText(_settingsPath);
                root = JsonNode.Parse(existing) ?? new JsonObject();
            }
            else
            {
                root = new JsonObject();
            }

            foreach (var (section, values) in settings)
            {
                foreach (var (key, value) in values)
                {
                    var strVal = value?.ToString() ?? "";
                    if (strVal == "") continue; // don't overwrite with empty

                    // Split full path (e.g. "Solver:Tetra3" + "IndexPath" → ["Solver","Tetra3","IndexPath"])
                    var segments = $"{section}:{key}".Split(':');
                    SetNestedValue(root.AsObject(), segments, strVal);
                }
            }

            var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsPath, json);
        }
    }

    private static void SetNestedValue(JsonObject node, string[] segments, string strVal)
    {
        for (int i = 0; i < segments.Length - 1; i++)
        {
            var seg = segments[i];
            if (node[seg] is not JsonObject child)
            {
                child = new JsonObject();
                node[seg] = child;
            }
            node = child;
        }

        var lastKey = segments[^1];
        if (bool.TryParse(strVal, out var boolVal))
            node[lastKey] = boolVal;
        else if (int.TryParse(strVal, out var intVal))
            node[lastKey] = intVal;
        else if (double.TryParse(strVal, out var dblVal))
            node[lastKey] = dblVal;
        else
            node[lastKey] = strVal;
    }

    private static string? ValidPort(string value)
    {
        if (!int.TryParse(value, out var port) || port < 1 || port > 65535)
            return $"Port must be between 1 and 65535";
        return null;
    }

    private static string? ValidPositiveInt(string value, string name)
    {
        if (!int.TryParse(value, out var n) || n <= 0)
            return $"{name} must be a positive integer";
        return null;
    }

    private static string? ValidPositiveDouble(string value, string name)
    {
        if (!double.TryParse(value, out var n) || n <= 0)
            return $"{name} must be a positive number";
        return null;
    }

    private static string? ValidNonNegativeDouble(string value, string name)
    {
        if (!double.TryParse(value, out var n) || n < 0)
            return $"{name} must be a non-negative number";
        return null;
    }
}
