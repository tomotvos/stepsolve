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

    public string Backend { get; set; } = "astrometry";
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
    public string SyncMode { get; set; } = "sync";
    public double MaxSyncDeltaDeg { get; set; } = 5.0;
}
