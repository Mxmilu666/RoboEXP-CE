using RoboExp.Core.Build;
using RoboExp.Core.Install;

namespace RoboExp.Tests.Build;

/// <summary>Skipped when no RoboEXP vendor install can be located (vendor/app is gitignored).</summary>
public sealed class VendorFactAttribute : FactAttribute
{
    public VendorFactAttribute()
    {
        if (Vendor.Install == null) Skip = "RoboEXP vendor install not found (set ROBOEXP_HOME)";
    }
}

/// <summary>Skipped without a vendor install or a native arm-none-eabi toolchain.</summary>
public sealed class ToolchainFactAttribute : FactAttribute
{
    public ToolchainFactAttribute()
    {
        if (Vendor.Install == null) Skip = "RoboEXP vendor install not found (set ROBOEXP_HOME)";
        else if (!ToolchainLocator.Default.IsAvailable) Skip = "arm-none-eabi toolchain not found";
    }
}

/// <summary>Skipped on Windows (uses POSIX shell scripts as fake tools).</summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "needs POSIX shell scripts";
    }
}

public static class Vendor
{
    private static readonly Lazy<VendorInstall?> Lazy = new(() =>
    {
        try { return VendorInstall.Locate(); }
        catch (DirectoryNotFoundException) { return null; }
    });

    public static VendorInstall? Install => Lazy.Value;
}
