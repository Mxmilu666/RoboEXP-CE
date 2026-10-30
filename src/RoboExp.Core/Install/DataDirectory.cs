namespace RoboExp.Core.Install;

/// <summary>
/// Writable per-user data root: <c>$ROBOEXP_DATA_DIR</c>, else <c>&lt;LocalApplicationData&gt;/RoboExpCE</c>.
/// Holds imported vendor installs (<see cref="VendorRoot"/>), downloaded toolchains (<see cref="ToolchainsRoot"/>) and caches.
/// </summary>
public static class DataDirectory
{
    public const string EnvVar = "ROBOEXP_DATA_DIR";

    /// <summary>The data root (not created).</summary>
    public static string Root
    {
        get
        {
            var env = Environment.GetEnvironmentVariable(EnvVar);
            return !string.IsNullOrEmpty(env)
                ? Path.GetFullPath(env)
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RoboExpCE");
        }
    }

    /// <summary><c>&lt;Root&gt;/vendor</c>: one subdirectory per imported installer version.</summary>
    public static string VendorRoot => Path.Combine(Root, "vendor");

    /// <summary><c>&lt;Root&gt;/toolchains</c>: extracted <c>arm-gnu-toolchain-*</c> directories.</summary>
    public static string ToolchainsRoot => Path.Combine(Root, "toolchains");
}
