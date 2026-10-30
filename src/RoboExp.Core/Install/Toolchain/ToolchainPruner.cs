namespace RoboExp.Core.Install;

/// <summary>Removes optional language/debugging tools from a freshly extracted C toolchain, never an existing install.</summary>
internal static class ToolchainPruner
{
    public static void Prune(string root, CancellationToken ct) => PruneDirectory(new DirectoryInfo(root), "", false, ct);

    private static void PruneDirectory(DirectoryInfo directory, string relative, bool removeContents, CancellationToken ct)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            ct.ThrowIfCancellationRequested();
            var path = relative.Length == 0 ? entry.Name : relative + "/" + entry.Name;
            // Keep notices in place, including any inside otherwise removable documentation/header trees.
            if (IsLicense(entry.Name)) continue;
            var remove = removeContents || IsOptional(path, entry.Name);
            var isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                // Never enumerate a symlink/junction: its target may be shared, or outside the extraction root.
                // Unlinking an optional hard-linked file likewise leaves every retained name/content intact.
                if (remove) entry.Delete();
                continue;
            }
            if (isDirectory)
            {
                PruneDirectory((DirectoryInfo)entry, path, remove, ct);
                ct.ThrowIfCancellationRequested();
                if (remove && !Directory.EnumerateFileSystemEntries(entry.FullName).Any()) entry.Delete();
            }
            else if (remove)
            {
                entry.Delete();
            }
        }
    }

    private static bool IsLicense(string name) =>
        name.Contains("license", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("licence", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("copyright", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("copying", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("notice", StringComparison.OrdinalIgnoreCase);

    private static bool IsOptional(string path, string name)
    {
        if (name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".debug", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".dwo", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".dwp", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".dSYM", StringComparison.OrdinalIgnoreCase) || name == ".debug") return true;

        if (path is "share/man" or "share/info" or "share/gdb" or "include/gdb" ||
            path == "arm-none-eabi/include/c++" ||
            (path.StartsWith("lib/gcc/arm-none-eabi/", StringComparison.Ordinal) && name == "finclude")) return true;

        // Only named tools, not a broad executable allowlist: retain binutils and all C/LTO internals.
        if (path.StartsWith("bin/", StringComparison.Ordinal) ||
            path.StartsWith("arm-none-eabi/bin/", StringComparison.Ordinal) ||
            path.StartsWith("libexec/gcc/arm-none-eabi/", StringComparison.Ordinal) ||
            path.StartsWith("lib/gcc/arm-none-eabi/", StringComparison.Ordinal))
        {
            var tool = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
            if (tool.StartsWith("arm-none-eabi-", StringComparison.Ordinal)) tool = tool[14..];
            if (tool is "c++" or "g++" or "gfortran" or "cc1plus" or "f951" or
                "gdb" or "gdb-py" or "gdb-add-index" or "gdb-add-index-py" or "gdbserver" or
                "gstack" or "gstack-py" or
                "gcov" or "gcov-dump" or "gcov-tool" or "gprof" or "gprofng" or "lto-dump") return true;
        }

        // Applies at every multilib depth, without removing architecture directories, startup objects,
        // specs, newlib/nano/semihosting libraries, libgcc or any host DLL/shared-library dependencies.
        if (path.StartsWith("arm-none-eabi/lib/", StringComparison.Ordinal) ||
            path.StartsWith("lib/gcc/arm-none-eabi/", StringComparison.Ordinal))
            return name.StartsWith("libstdc++", StringComparison.Ordinal) ||
                name.StartsWith("libsupc++", StringComparison.Ordinal) ||
                name.StartsWith("libgfortran", StringComparison.Ordinal);
        return false;
    }
}
