namespace StepSolve;

/// <summary>
/// Identifies the OnStep firmware returned by the LX200 version commands.
/// </summary>
public sealed record OnStepIdentity(string Product, string FirmwareVersion);

/// <summary>
/// The mount state reported by OnStep's packed, human-readable <c>:GU#</c> reply.
/// The raw reply is retained because the set of status flags is firmware-dependent.
/// </summary>
public sealed record OnStepMountStatus(string Raw)
{
    /// <summary>True while a goto is active. OnStep emits <c>N</c> only when no goto is active.</summary>
    public bool IsSlewing => !Raw.Contains('N');
    public bool IsParked => Raw.Contains('P');
    public bool IsParking => Raw.Contains('I');
    public bool HasParkFailure => Raw.Contains('F');
    public bool IsHoming => Raw.Contains('h');
    /// <summary>True while OnStep reports pulse or manual guiding activity.</summary>
    public bool IsGuiding => Raw.Contains('G') || Raw.Contains('g');
    /// <summary>OnStep's packed :GU# status ends with a non-zero general-error digit on fault.</summary>
    public bool HasGeneralError => Raw.Length > 0 && char.IsDigit(Raw[^1]) && Raw[^1] != '0';
    public bool IsAtHome => Raw.Contains('H');
}

/// <summary>
/// An equatorial position read from the mount via <c>:GR#</c> and <c>:GD#</c>.
/// </summary>
public sealed record OnStepPosition(double RaDeg, double DecDeg);

/// <summary>
/// Progress reported by <c>:A?#</c> during OnStep's manual multi-star alignment.
/// </summary>
public sealed record OnStepAlignmentProgress(int MaximumStars, int CurrentStar, int LastRequiredStar)
{
    public bool IsActive => LastRequiredStar > 0;
    public bool IsComplete => !IsActive && CurrentStar == 0;
}

/// <summary>
/// The acknowledgement returned by a mutating LX200 command sequence.
/// Transport failures are represented by exceptions; controller rejections are returned here.
/// </summary>
public sealed record OnStepCommandResult(bool Succeeded, string Command, string Response, string? Error = null)
{
    public static OnStepCommandResult Success(string command, string response) => new(true, command, response);
    public static OnStepCommandResult Failure(string command, string response, string error) => new(false, command, response, error);
}
