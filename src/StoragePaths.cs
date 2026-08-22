using Microsoft.Extensions.Configuration;

namespace StepSolve;

/// <summary>
/// Owns the writable locations used by the appliance. Release files remain
/// under the application directory; mutable runtime data belongs elsewhere.
/// </summary>
public sealed class StoragePaths
{
    public StoragePaths(IConfiguration configuration)
    {
        var dataDirectory = configuration["StepSolve:DataDirectory"];
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            dataDirectory = OperatingSystem.IsLinux()
                ? "/var/lib/stepsolve"
                : AppContext.BaseDirectory;
        }

        ImagesDirectory = Path.Combine(dataDirectory, "images");
    }

    public string ImagesDirectory { get; }
}
