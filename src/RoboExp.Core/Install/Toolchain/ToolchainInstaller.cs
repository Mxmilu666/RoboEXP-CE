using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using RoboExp.Core.Build;

namespace RoboExp.Core.Install;

/// <summary>Installer progress. <paramref name="Phase"/> is "download", "verify", "extract" or "prune";
/// <paramref name="Total"/> is 0 while the amount of work is unknown (extraction/pruning start).</summary>
public sealed record ToolchainInstallProgress(string Phase, long Done, long Total);

/// <summary>
/// Downloads, verifies and extracts an Arm GNU Toolchain package into <c>&lt;root&gt;/&lt;DirectoryName&gt;</c>
/// (root defaults to <see cref="DataDirectory.ToolchainsRoot"/>, which <see cref="ToolchainLocator"/> searches).
/// </summary>
public sealed class ToolchainInstaller
{
    private const int ProgressStep = 256 * 1024;

    private static readonly Lazy<HttpClient> SharedHttp = new(() =>
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RoboExpCE");
        return client;
    });

    private readonly HttpClient _http;

    /// <summary>
    /// gitlab.arm.com only accepts TLS 1.3, which the default macOS TLS stack (Secure Transport) lacks. .NET 10's opt-in
    /// Network.framework backend provides it; the switch is process-wide and must be set before the first TLS connection,
    /// so hosts that make HTTPS requests earlier should set it at startup too. An explicit setting by the host wins.
    /// </summary>
    static ToolchainInstaller()
    {
        const string useNetworkFramework = "System.Net.Security.UseNetworkFramework";
        if (OperatingSystem.IsMacOS() && !AppContext.TryGetSwitch(useNetworkFramework, out _)
            && Environment.GetEnvironmentVariable("DOTNET_SYSTEM_NET_SECURITY_USENETWORKFRAMEWORK") == null)
            AppContext.SetSwitch(useNetworkFramework, true);
    }

    public ToolchainInstaller(HttpClient? http = null) => _http = http ?? SharedHttp.Value;

    /// <summary>
    /// Installs <paramref name="pkg"/> and returns its bin directory. Already-installed packages return immediately.
    /// The archive goes to <c>&lt;root&gt;/.download/&lt;FileName&gt;</c>; an existing file with the pinned hash is reused,
    /// otherwise it is downloaded (hashed while streaming). It is extracted into a temporary sibling directory and moved
    /// into place only after <c>bin/arm-none-eabi-gcc</c> is found and optional language/debugger files are pruned,
    /// so an interrupted install never leaves a half-extracted toolchain where the locator would pick it up.
    /// License/copyright notices and every C/libgcc multilib variant are retained. Existing installs are not pruned.
    /// The archive is deleted after a successful install.
    /// </summary>
    /// <exception cref="InvalidDataException">SHA-256 mismatch (the download is deleted) or the archive lacks arm-none-eabi-gcc.</exception>
    public async Task<string> InstallAsync(ToolchainPackage pkg, string? root, IProgress<ToolchainInstallProgress>? progress, CancellationToken ct)
    {
        root = Path.GetFullPath(root ?? DataDirectory.ToolchainsRoot);
        var target = Path.Combine(root, pkg.DirectoryName);
        var gcc = "arm-none-eabi-gcc" + ToolchainLocator.ExeSuffix;
        if (File.Exists(Path.Combine(target, "bin", gcc))) return Path.Combine(target, "bin");

        var downloads = Path.Combine(root, ".download");
        Directory.CreateDirectory(downloads);
        var archive = Path.Combine(downloads, pkg.FileName);
        await EnsureArchiveAsync(pkg, archive, progress, ct).ConfigureAwait(false);

        // Short random name: keeps Windows paths (deepest zip entry ~120 chars) well below MAX_PATH.
        var temp = Path.Combine(root, ".x" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)));
        try
        {
            progress?.Report(new ToolchainInstallProgress("extract", 0, 0));
            await ExtractAsync(archive, pkg.Kind, temp, ct).ConfigureAwait(false);
            var content = ContentRoot(temp);
            if (!File.Exists(Path.Combine(content, "bin", gcc)))
                throw new InvalidDataException($"{pkg.FileName} does not contain bin/{gcc}");
            progress?.Report(new ToolchainInstallProgress("prune", 0, 0));
            ToolchainPruner.Prune(content, ct);
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(content, target);
            progress?.Report(new ToolchainInstallProgress("prune", 1, 1));
        }
        finally
        {
            if (Directory.Exists(temp)) TryDeleteDirectory(temp);
        }
        File.Delete(archive);
        return Path.Combine(target, "bin");
    }

    public static async Task<string> ComputeSha256Async(string file, CancellationToken ct)
    {
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        return await HashAsync(stream, null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Extracts <paramref name="archive"/> into <paramref name="destination"/> (created if needed).
    /// <para>
    /// tar.xz uses the system <c>tar</c>: the BCL has no xz decoder, and a managed one (a NuGet dependency) would still need
    /// System.Formats.Tar plus hand-written handling of exec bits, symlinks and hard links that tar already gets right.
    /// <c>tar</c> is present everywhere a tar.xz package is used: bsdtar on macOS (and Windows 10+ at System32\tar.exe,
    /// preferred over a Git-for-Windows GNU tar on PATH), GNU tar + xz on Linux. Zip (the Windows package) uses
    /// <see cref="ZipFile"/>, which handles long paths on Windows and needs no exec bits there.
    /// </para>
    /// </summary>
    public static async Task ExtractAsync(string archive, ArchiveKind kind, string destination, CancellationToken ct)
    {
        archive = Path.GetFullPath(archive);
        destination = Path.GetFullPath(destination);
        Directory.CreateDirectory(destination);
        if (kind == ArchiveKind.Zip)
        {
            await ZipFile.ExtractToDirectoryAsync(archive, destination, overwriteFiles: true, ct).ConfigureAwait(false);
            return;
        }

        var psi = new ProcessStartInfo(TarExecutable())
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "-xJf", archive, "-C", destination }) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi) ?? throw new IOException($"could not start {psi.FileName}");
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        await stdout.ConfigureAwait(false);
        var error = (await stderr.ConfigureAwait(false)).Trim();
        if (process.ExitCode != 0)
            throw new IOException($"tar exited with {process.ExitCode} extracting {Path.GetFileName(archive)}: {error}");
    }

    private async Task EnsureArchiveAsync(ToolchainPackage pkg, string archive, IProgress<ToolchainInstallProgress>? progress, CancellationToken ct)
    {
        if (File.Exists(archive))
        {
            string existing;
            await using (var stream = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true))
                existing = await HashAsync(stream, p => progress?.Report(new ToolchainInstallProgress("verify", p, stream.Length)), ct).ConfigureAwait(false);
            if (string.Equals(existing, pkg.Sha256, StringComparison.OrdinalIgnoreCase)) return;
            File.Delete(archive);
        }

        // Downloaded under .part so that "<FileName> exists" always means a complete, verified file.
        var part = archive + ".part";
        try
        {
            string actual;
            using (var response = await _http.GetAsync(pkg.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? pkg.Size;
                await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1 << 16];
                long done = 0, reported = 0;
                progress?.Report(new ToolchainInstallProgress("download", 0, total));
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    done += read;
                    if (done - reported >= ProgressStep)
                    {
                        reported = done;
                        progress?.Report(new ToolchainInstallProgress("download", done, total));
                    }
                }
                progress?.Report(new ToolchainInstallProgress("download", done, total));
                actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            }

            progress?.Report(new ToolchainInstallProgress("verify", 1, 1));
            if (!string.Equals(actual, pkg.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"SHA-256 mismatch for {pkg.FileName}: expected {pkg.Sha256}, got {actual}");
            File.Move(part, archive, overwrite: true);
        }
        finally
        {
            File.Delete(part);
        }
    }

    private static async Task<string> HashAsync(Stream stream, Action<long>? progress, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 16];
        long done = 0, reported = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            done += read;
            if (progress != null && done - reported >= ProgressStep)
            {
                reported = done;
                progress(done);
            }
        }
        progress?.Invoke(done);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>The single top-level directory of an extracted archive (tar.xz packages), else the directory itself (Windows zip).</summary>
    private static string ContentRoot(string extracted)
    {
        var entries = Directory.GetFileSystemEntries(extracted);
        return entries.Length == 1 && Directory.Exists(entries[0]) ? entries[0] : extracted;
    }

    private static string TarExecutable()
    {
        if (OperatingSystem.IsWindows())
        {
            var system = Path.Combine(Environment.SystemDirectory, "tar.exe");
            if (File.Exists(system)) return system;
        }
        return "tar";
    }

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
