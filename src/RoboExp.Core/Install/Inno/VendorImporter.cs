using System.Diagnostics;

namespace RoboExp.Core.Install.Inno;

public enum VendorImportMethod { Directory, Managed, Innoextract }

/// <param name="Phase">"read" (headers), "extract", "validate".</param>
/// <param name="Total">0 when unknown (innoextract fallback).</param>
public sealed record VendorImportProgress(string Phase, long Done, long Total);

public sealed record VendorImportResult(string InstallRoot, string Version, int FileCount, VendorImportMethod Method);

/// <summary>
/// Makes a user-supplied original RoboEXP install usable: either an already extracted directory, or the original
/// Inno Setup installer .exe, which is extracted into <c>&lt;DataDirectory.VendorRoot&gt;/&lt;version&gt;</c> with the
/// built-in <see cref="InnoExtractor"/> (no external tools). Installers outside the managed reader's format range
/// fall back to an <c>innoextract</c> found on PATH.
/// </summary>
public static class VendorImporter
{
    /// <summary>Accepts an install root, or a directory containing <c>app/</c> (innoextract output). Throws
    /// <see cref="DirectoryNotFoundException"/> with a readable message otherwise.</summary>
    public static VendorImportResult ImportDirectory(string directory)
    {
        var full = Path.GetFullPath(directory);
        foreach (var candidate in new[] { full, Path.Combine(full, "app") })
        {
            if (VendorInstall.IsInstall(candidate))
                return new VendorImportResult(candidate, Path.GetFileName(full), 0, VendorImportMethod.Directory);
        }
        throw new DirectoryNotFoundException(
            $"Not a RoboEXP install (expected Compiler/JMCompiler.xml and Robots/): {full}");
    }

    /// <summary>Extracts the installer and validates the result. <paramref name="vendorRoot"/> defaults to
    /// <see cref="DataDirectory.VendorRoot"/>; an existing import of the same version is replaced.</summary>
    /// <exception cref="UnsupportedInstallerException">Format not handled and no innoextract on PATH.</exception>
    /// <exception cref="InvalidDataException">Corrupt installer, or it does not contain a RoboEXP install.</exception>
    public static Task<VendorImportResult> ImportInstallerAsync(string installerPath, string? vendorRoot = null,
        IProgress<VendorImportProgress>? progress = null, CancellationToken ct = default) =>
        Task.Run(async () =>
        {
            var root = Path.GetFullPath(vendorRoot ?? DataDirectory.VendorRoot);
            Directory.CreateDirectory(root);
            var staging = Path.Combine(root, ".import-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                string version;
                int fileCount;
                VendorImportMethod method;
                progress?.Report(new VendorImportProgress("read", 0, 0));
                try
                {
                    await using var exe = new FileStream(installerPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                    var info = InnoSetupInfo.Load(exe);
                    var plan = InnoExtractor.Plan(info);
                    var extractProgress = progress == null ? null
                        : new SyncProgress<InnoExtractProgress>(p => progress.Report(new VendorImportProgress("extract", p.BytesDone, p.BytesTotal)));
                    InnoExtractor.Extract(exe, info, plan, staging, extractProgress, ct);
                    version = info.AppVersion;
                    fileCount = plan.Files.Count;
                    method = VendorImportMethod.Managed;
                }
                catch (UnsupportedInstallerException e)
                {
                    var tool = FindInnoextract() ?? throw new UnsupportedInstallerException(
                        $"{e.Message}. Install innoextract (https://constexpr.org/innoextract/) and retry, " +
                        "or extract the installer with another tool and choose the extracted directory.");
                    if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
                    progress?.Report(new VendorImportProgress("extract", 0, 0));
                    await RunInnoextractAsync(tool, installerPath, staging, ct);
                    version = "";
                    fileCount = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).Count();
                    method = VendorImportMethod.Innoextract;
                }

                progress?.Report(new VendorImportProgress("validate", 0, 0));
                var app = Path.Combine(staging, "app");
                if (!VendorInstall.IsInstall(app))
                    throw new InvalidDataException("The installer does not contain a RoboEXP install (no app/Compiler/JMCompiler.xml or app/Robots).");
                if (version.Length == 0) version = Path.GetFileNameWithoutExtension(installerPath);
                var target = Path.Combine(root, SafeName(version));
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                Directory.Move(app, target);
                return new VendorImportResult(target, version, fileCount, method);
            }
            finally
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            }
        }, ct);

    /// <summary>innoextract on PATH, or null.</summary>
    public static string? FindInnoextract()
    {
        var exe = OperatingSystem.IsWindows() ? "innoextract.exe" : "innoextract";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, exe);
            if (File.Exists(candidate)) return candidate;
        }
        // GUI apps on macOS start with a minimal PATH; check the usual package-manager locations too.
        foreach (var dir in new[] { "/opt/homebrew/bin", "/usr/local/bin", "/opt/local/bin" })
        {
            var candidate = Path.Combine(dir, exe);
            if (!OperatingSystem.IsWindows() && File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static async Task RunInnoextractAsync(string tool, string installer, string output, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(tool) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in new[] { "--silent", "--output-dir", output, installer }) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new IOException($"Could not start {tool}");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try { await process.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        await stdout;
        var err = await stderr;
        if (process.ExitCode != 0) throw new InvalidDataException($"innoextract failed ({process.ExitCode}): {err.Trim()}");
    }

    private static string SafeName(string version)
    {
        var chars = version.Trim().Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray();
        return chars.Length == 0 ? "imported" : new string(chars);
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
