namespace StepSolve;

public sealed class StepSolveOptions
{
    public const string Section = "StepSolve";

    public string Mode { get; set; } = "demo";
    public int WebPort { get; set; } = 5001;
    public int Lx200Port { get; set; } = 5002;
}

public sealed class SolverOptions
{
    public const string Section = "Solver";

    public string Backend { get; set; } = "tetra3";
    public int HintTimeout { get; set; } = 10;
    public double SolveRadius { get; set; } = 20.0;
    public double FovEstimateDeg { get; set; } = 34.3;
    public bool EnableFallback { get; set; } = true;

    public AstrometryOptions Astrometry { get; set; } = new();
    public CedarOptions Cedar { get; set; } = new();
    public Tetra3Options Tetra3 { get; set; } = new();
}

public sealed class AstrometryOptions
{
    public string SolveFieldPath { get; set; } = "solve-field";
    public string IndexPath { get; set; } = "";
    public int Timeout { get; set; } = 60;
    public int Sigma { get; set; } = 5;
    public int Depth { get; set; } = 20;
}

public sealed class CedarOptions
{
    public string PythonPath { get; set; } = "/var/lib/stepsolve/solvers/.venv/bin/python";
    public string ScriptPath { get; set; } = "/var/lib/stepsolve/solvers/cedar_solve_service.py";
    public string IndexPath { get; set; } = "/var/lib/stepsolve/indexes/cedar-default";
    public int Timeout { get; set; } = 30;
}

public sealed class Tetra3Options
{
    public string PythonPath { get; set; } = "/var/lib/stepsolve/solvers/.venv/bin/python";
    public string ScriptPath { get; set; } = "/var/lib/stepsolve/solvers/tetra3_solve_service.py";
    public string IndexPath { get; set; } = "/var/lib/stepsolve/indexes/tetra3-default";
    public int Timeout { get; set; } = 30;
}

public sealed class CameraOptions
{
    public const string Section = "Camera";

    public int ShutterUs { get; set; } = 1_000_000;
    public double Gain { get; set; } = 8.0;
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 960;
    public string OutputFormat { get; set; } = "jpg";
}

public sealed class OnStepOptions
{
    public const string Section = "OnStep";

    public bool Enabled { get; set; }
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 9998;
    /// <summary>Maximum time for one TCP connect-and-command exchange with OnStep.</summary>
    public int CommandTimeoutSeconds { get; set; } = 10;
    // Retained only so existing runtime configuration files continue to bind.
    // Automatic sync policy is controlled by the explicit settings below.
    public string SyncMode { get; set; } = "sync";
    public double MaxSyncDeltaDeg { get; set; } = 5.0;

    // New calibration behaviour is deliberately read-only by default. The
    // legacy SyncMode remains for compatibility with existing configurations.
    public string StartupPolicy { get; set; } = "probe";
    public string BackgroundPolicy { get; set; } = "validate";
    /// <summary>Permits narrowly gated one-point corrections during normal Solve mode.</summary>
    public bool AutomaticCorrectionsEnabled { get; set; }
    /// <summary>Minimum time between successful automatic one-point corrections.</summary>
    public int CorrectionIntervalMinutes { get; set; } = 15;
    /// <summary>Largest OnStep-to-solve residual that may be corrected automatically.</summary>
    public double MaxAutomaticCorrectionDeg { get; set; } = 1.0;
    /// <summary>Persisted timestamp used to preserve the automatic-correction cooldown across restarts.</summary>
    public DateTimeOffset? LastAutomaticCorrectionAtUtc { get; set; }
    public double MinSolveConfidence { get; set; } = 0.90;
    public int StableSolveIntervalSeconds { get; set; } = 1;
    public double MaxSolveDisagreementDeg { get; set; } = 0.05;
    public int CalibrationSettleSeconds { get; set; } = 3;
    public int CalibrationTargetRetryCount { get; set; } = 3;

    // V1 reference-rig guard rails. These are intentionally narrower than
    // generic Alt-Az ranges and may be changed only through reviewed config.
    public double CalibrationMinAltitudeDeg { get; set; } = 15;
    public double CalibrationMaxAltitudeDeg { get; set; } = 85;
    public double CalibrationMinAzimuthDeg { get; set; } = -150;
    public double CalibrationMaxAzimuthDeg { get; set; } = 150;

    // Ordered as Az/Alt, relative to the established 0,0 home reference.
    // Defaults live in appsettings.json. This must start empty because
    // ConfigurationBinder appends configured collection values to an existing
    // list; initializing it here would duplicate every configured target.
    public List<OnStepCalibrationTarget> CalibrationTargets { get; set; } = [];
}

public sealed record OnStepCalibrationTarget(double AzimuthDeg, double AltitudeDeg);
