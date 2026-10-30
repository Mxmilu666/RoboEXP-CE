using System.Text;
using System.Text.RegularExpressions;

namespace RoboExp.Core.Install.Inno;

/// <summary>Inno Setup format that the managed reader cannot handle (caller may fall back to innoextract).</summary>
public sealed class UnsupportedInstallerException(string message) : Exception(message);

/// <summary>Setup data version, e.g. "Inno Setup Setup Data (5.5.0) (u)".</summary>
public readonly record struct InnoVersion(int Major, int Minor, int Patch, bool Unicode)
{
    public int Value => (Major << 16) | (Minor << 8) | Patch;
    public static int Of(int major, int minor, int patch) => (major << 16) | (minor << 8) | patch;
    public bool AtLeast(int major, int minor, int patch) => Value >= Of(major, minor, patch);
    public override string ToString() => $"{Major}.{Minor}.{Patch}{(Unicode ? " (unicode)" : "")}";
}

public enum InnoCompression { Stored, Zlib, BZip2, Lzma1, Lzma2 }

/// <summary>A <c>[Dirs]</c> entry.</summary>
public sealed record InnoDirectoryEntry(string Name, string Languages, uint Options);

/// <summary>A <c>[Files]</c> entry (only fields that matter for extraction).</summary>
public sealed record InnoFileEntry(string Destination, string Languages, uint Location, uint Attributes, ulong Options, byte Type)
{
    public const ulong ConfirmOverwrite = 1ul << 0;
    public const ulong CompareTimeStamp = 1ul << 7;
    public const ulong OverwriteReadOnly = 1ul << 10;
    public const ulong OverwriteSameVersion = 1ul << 11;
    public const ulong IgnoreVersion = 1ul << 17;
    public const ulong PromptIfOlder = 1ul << 18;
    public const ulong ReplaceSameVersionIfContentsDiffer = 1ul << 22;
    public const byte UninstExe = 1;
    public const uint ReadOnlyAttribute = 0x1;
}

/// <summary>A file location: where the (possibly filtered) file data lives inside a compressed chunk.</summary>
public sealed record InnoDataEntry(
    uint FirstSlice, uint ChunkOffset, ulong FileOffset, ulong FileSize, ulong ChunkSize,
    byte[] Sha1, long FileTime, ulong FileVersion, uint Flags)
{
    public const uint VersionInfoValid = 1 << 0;
    public const uint CallInstructionOptimized = 1 << 4;
    public const uint ChunkEncrypted = 1 << 6;
    public const uint ChunkCompressed = 1 << 7;

    /// <summary>Win32 FILETIME (100 ns since 1601) as UTC.</summary>
    public DateTime TimestampUtc => FileTime >= 0 && FileTime <= DateTime.MaxValue.ToFileTimeUtc()
        ? DateTime.FromFileTimeUtc(FileTime) : DateTime.UnixEpoch;
}

/// <summary>
/// Reads the setup headers of an Inno Setup installer .exe: loader offset table (PE resource 11111 or the
/// pre-5.1.5 pointer at 0x30), setup data version, the first header block up to the <c>[Files]</c> entries and the
/// second block (file locations). Supports unicode setup data versions 5.3.9 – 6.0.x with data embedded in the
/// .exe (single slice), the range where the record layouts below are exact. Layout reference: innoextract
/// (src/setup/*.cpp, zlib license) by Daniel Scharrer.
/// </summary>
public sealed partial class InnoSetupInfo
{
    public required InnoVersion Version { get; init; }
    public required string VersionString { get; init; }
    public required string AppName { get; init; }
    public required string AppVersion { get; init; }
    public required InnoCompression Compression { get; init; }
    public required IReadOnlyList<string> Languages { get; init; }
    public required IReadOnlyList<InnoDirectoryEntry> Directories { get; init; }
    public required IReadOnlyList<InnoFileEntry> Files { get; init; }
    public required IReadOnlyList<InnoDataEntry> DataEntries { get; init; }
    /// <summary>Absolute offset of slice data inside the .exe (chunk offsets are relative to it).</summary>
    public required long DataOffset { get; init; }

    private static readonly (byte[] Magic, int Version)[] KnownLoaders =
    [
        ([(byte)'r', (byte)'D', (byte)'l', (byte)'P', (byte)'t', (byte)'S', (byte)'0', (byte)'2', 0x87, (byte)'e', (byte)'V', (byte)'x'], InnoVersion.Of(1, 2, 10)),
        ([(byte)'r', (byte)'D', (byte)'l', (byte)'P', (byte)'t', (byte)'S', (byte)'0', (byte)'4', 0x87, (byte)'e', (byte)'V', (byte)'x'], InnoVersion.Of(4, 0, 0)),
        ([(byte)'r', (byte)'D', (byte)'l', (byte)'P', (byte)'t', (byte)'S', (byte)'0', (byte)'5', 0x87, (byte)'e', (byte)'V', (byte)'x'], InnoVersion.Of(4, 0, 3)),
        ([(byte)'r', (byte)'D', (byte)'l', (byte)'P', (byte)'t', (byte)'S', (byte)'0', (byte)'6', 0x87, (byte)'e', (byte)'V', (byte)'x'], InnoVersion.Of(4, 0, 10)),
        ([(byte)'r', (byte)'D', (byte)'l', (byte)'P', (byte)'t', (byte)'S', (byte)'0', (byte)'7', 0x87, (byte)'e', (byte)'V', (byte)'x'], InnoVersion.Of(4, 1, 6)),
        ([(byte)'r', (byte)'D', (byte)'l', (byte)'P', (byte)'t', (byte)'S', 0xcd, 0xe6, 0xd7, (byte)'{', 0x0b, (byte)'*'], InnoVersion.Of(5, 1, 5)),
        ([(byte)'n', (byte)'S', (byte)'5', (byte)'W', (byte)'7', (byte)'d', (byte)'T', 0x83, 0xaa, 0x1b, 0x0f, (byte)'j'], InnoVersion.Of(5, 1, 5)),
    ];

    [GeneratedRegex(@"^Inno Setup Setup Data \((\d+)\.(\d+)\.(\d+)\)( \([uU]\))?$")]
    private static partial Regex VersionPattern();

    /// <summary>Reads the headers. Throws <see cref="UnsupportedInstallerException"/> for formats outside the supported range
    /// and <see cref="InvalidDataException"/> for corrupt data.</summary>
    public static InnoSetupInfo Load(Stream exe)
    {
        var (headerOffset, dataOffset) = FindOffsets(exe);
        if (dataOffset == 0)
            throw new UnsupportedInstallerException("Installer data is stored in external setup-*.bin files");

        exe.Position = headerOffset;
        var versionBytes = new byte[64];
        exe.ReadExactly(versionBytes);
        var versionString = Encoding.ASCII.GetString(versionBytes).TrimEnd('\0');
        var m = VersionPattern().Match(versionString);
        if (!m.Success) throw new UnsupportedInstallerException($"Unsupported setup data version \"{versionString}\"");
        var version = new InnoVersion(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), m.Groups[4].Success);
        if (!version.Unicode || !version.AtLeast(5, 3, 9) || version.AtLeast(6, 1, 0))
            throw new UnsupportedInstallerException($"Setup data version {version} is not supported by the built-in extractor (supported: unicode 5.3.9 – 6.0.x)");

        long block1Start = exe.Position;
        var (block1Size, block1Compressed) = ReadBlockHeader(exe);
        long block2Start = block1Start + 9 + block1Size;

        Header header;
        List<string> languages = [];
        List<InnoDirectoryEntry> dirs = [];
        List<InnoFileEntry> files = [];
        using (var block = OpenBlock(exe, block1Size, block1Compressed))
        {
            var r = new InnoReader(block);
            header = ReadHeader(r, version);
            for (var i = 0; i < header.LanguageCount; i++) languages.Add(ReadLanguage(r, version));
            for (var i = 0; i < header.MessageCount; i++) { r.SkipString(); r.SkipString(); r.I32(); }
            for (var i = 0; i < header.PermissionCount; i++) r.SkipString();
            for (var i = 0; i < header.TypeCount; i++) SkipType(r);
            for (var i = 0; i < header.ComponentCount; i++) SkipComponent(r);
            for (var i = 0; i < header.TaskCount; i++) SkipTask(r);
            for (var i = 0; i < header.DirectoryCount; i++) dirs.Add(ReadDirectory(r));
            for (var i = 0; i < header.FileCount; i++) files.Add(ReadFile(r));
            // The remaining entries (icons, ini, registry, run, wizard images, decompressor dll) are not needed:
            // the next block starts right after this block's stored bytes.
        }

        exe.Position = block2Start;
        var (block2Size, block2Compressed) = ReadBlockHeader(exe);
        List<InnoDataEntry> data = [];
        using (var block = OpenBlock(exe, block2Size, block2Compressed))
        {
            var r = new InnoReader(block);
            for (var i = 0; i < header.DataEntryCount; i++) data.Add(ReadDataEntry(r, version));
        }

        return new InnoSetupInfo
        {
            Version = version,
            VersionString = versionString,
            AppName = header.AppName,
            AppVersion = header.AppVersion,
            Compression = header.Compression,
            Languages = languages,
            Directories = dirs,
            Files = files,
            DataEntries = data,
            DataOffset = dataOffset,
        };
    }

    // ---- loader -------------------------------------------------------------------------------------------------

    private static (long HeaderOffset, long DataOffset) FindOffsets(Stream exe)
    {
        var r = new InnoReader(exe);
        // Pre-5.1.5: "Inno" at 0x30 followed by the table offset and its complement.
        if (exe.Length > 0x3C)
        {
            exe.Position = 0x30;
            if (r.U32() == 0x6f6e6e49)
            {
                var table = r.U32();
                if (table == ~r.U32()) return LoadOffsetsAt(exe, table);
            }
        }
        var resource = PeResources.FindRcData(exe, 11111)
            ?? throw new UnsupportedInstallerException("Not an Inno Setup installer (no setup loader offset table)");
        return LoadOffsetsAt(exe, resource);
    }

    private static (long, long) LoadOffsetsAt(Stream exe, long pos)
    {
        exe.Position = pos;
        var magic = new byte[12];
        exe.ReadExactly(magic);
        var loader = KnownLoaders.FirstOrDefault(k => k.Magic.AsSpan().SequenceEqual(magic));
        if (loader.Magic == null) throw new UnsupportedInstallerException("Unknown Inno Setup loader signature");
        var r = new InnoReader(exe);
        var crc = Crc32.Compute(magic);
        uint Next()
        {
            var v = r.U32();
            crc = Crc32.Update(crc, BitConverter.GetBytes(v));
            return v;
        }
        if (loader.Version >= InnoVersion.Of(5, 1, 5)) Next(); // revision
        Next(); // total size
        Next(); // exe offset
        if (loader.Version < InnoVersion.Of(4, 1, 6)) Next(); // exe compressed size
        Next(); // exe uncompressed size
        Next(); // exe crc32 / adler32
        if (loader.Version < InnoVersion.Of(4, 0, 0)) r.U32(); // message offset
        var header = Next();
        var data = Next();
        if (loader.Version >= InnoVersion.Of(4, 0, 10) && r.U32() != crc)
            throw new InvalidDataException("Setup loader offset table checksum mismatch");
        return (header, data);
    }

    // ---- blocks -------------------------------------------------------------------------------------------------

    /// <summary>Reads the 9-byte block header: CRC32 of the next 5 bytes, stored size (incl. per-4 KiB CRCs), compressed flag.</summary>
    private static (uint StoredSize, bool Compressed) ReadBlockHeader(Stream s)
    {
        var bytes = new byte[9];
        s.ReadExactly(bytes);
        if (Crc32.Compute(bytes.AsSpan(4, 5)) != BitConverter.ToUInt32(bytes, 0))
            throw new InvalidDataException("Setup block header CRC32 mismatch");
        return (BitConverter.ToUInt32(bytes, 4), bytes[8] != 0);
    }

    /// <summary>Block payload stream; compressed blocks are LZMA1 for every supported version.</summary>
    private static Stream OpenBlock(Stream exe, uint storedSize, bool compressed)
    {
        Stream s = new InnoBlockChunkStream(new SubStream(exe, storedSize));
        return compressed ? new InnoLzma1Stream(s) : s;
    }

    // ---- records ------------------------------------------------------------------------------------------------

    private sealed record Header(
        string AppName, string AppVersion, InnoCompression Compression,
        uint LanguageCount, uint MessageCount, uint PermissionCount, uint TypeCount, uint ComponentCount, uint TaskCount,
        uint DirectoryCount, uint FileCount, uint DataEntryCount);

    private static Header ReadHeader(InnoReader r, InnoVersion v)
    {
        var appName = r.WideString();
        r.SkipString(); // app versioned name
        r.SkipString(); // app id
        r.SkipString(); // copyright
        r.SkipString(); // publisher
        r.SkipString(); // publisher url
        r.SkipString(); // support phone (5.1.13+)
        r.SkipString(); // support url
        r.SkipString(); // updates url
        var appVersion = r.WideString();
        r.SkipString(); // default dir name
        r.SkipString(); // default group name
        r.SkipString(); // base filename
        r.SkipString(); // uninstall files dir
        r.SkipString(); // uninstall name
        r.SkipString(); // uninstall icon
        r.SkipString(); // app mutex
        r.SkipString(); // default user name
        r.SkipString(); // default user organisation
        r.SkipString(); // default serial
        r.SkipString(); // readme file
        r.SkipString(); // contact
        r.SkipString(); // comments
        r.SkipString(); // modify path
        r.SkipString(); // create uninstall registry key (5.3.8+)
        if (v.AtLeast(5, 3, 10)) r.SkipString(); // uninstallable
        if (v.AtLeast(5, 5, 0)) r.SkipString(); // close applications filter
        if (v.AtLeast(5, 5, 6)) r.SkipString(); // setup mutex
        if (v.AtLeast(5, 6, 1)) { r.SkipString(); r.SkipString(); } // changes environment / associations
        r.SkipString(); r.SkipString(); r.SkipString(); // license, info before, info after (ANSI)
        if (!v.AtLeast(5, 3, 10)) r.SkipString(); // uninstaller signature
        r.SkipString(); // compiled code
        // unicode: no lead-byte set
        var languageCount = r.U32();
        var messageCount = r.U32();
        var permissionCount = r.U32();
        var typeCount = r.U32();
        var componentCount = r.U32();
        var taskCount = r.U32();
        var directoryCount = r.U32();
        var fileCount = r.U32();
        var dataEntryCount = r.U32();
        for (var i = 0; i < 7; i++) r.U32(); // icon, ini, registry, delete, uninstall delete, run, uninstall run counts
        r.SkipWindowsVersionRange();
        r.U32(); // back color
        r.U32(); // back color 2
        if (!v.AtLeast(5, 5, 7)) r.U32(); // image back color
        if (v.AtLeast(6, 0, 0)) { r.U8(); r.U32(); r.U32(); } // wizard style, resize percent x/y
        if (v.AtLeast(5, 5, 7)) r.U8(); // image alpha format
        r.Skip(20); // password SHA-1
        r.Skip(8); // password salt
        r.I64(); // extra disk space required
        r.U32(); // slices per disk
        r.U8(); // uninstall log mode
        r.U8(); // dir exists warning
        r.U8(); // privileges required (5.3.7+)
        if (v.AtLeast(5, 7, 0)) r.Flags(2); // privileges required overrides allowed
        r.U8(); // show language dialog
        r.U8(); // language detection
        var compression = r.U8() switch
        {
            0 => InnoCompression.Stored,
            1 => InnoCompression.Zlib,
            2 => InnoCompression.BZip2,
            3 => InnoCompression.Lzma1,
            4 => InnoCompression.Lzma2,
            var c => throw new InvalidDataException($"Unknown compression method {c}"),
        };
        var archCount = v.AtLeast(5, 6, 0) ? 5 : 4;
        r.Flags(archCount); // architectures allowed
        r.Flags(archCount); // architectures installed in 64-bit mode
        if (!v.AtLeast(5, 3, 10)) { r.U32(); r.U32(); } // signed uninstaller original size / header checksum
        r.U8(); r.U8(); // disable dir page, disable program group page (5.3.3+)
        if (v.AtLeast(5, 5, 0)) r.U64(); else r.U32(); // uninstall display size
        r.Flags(HeaderFlagCount(v));
        return new Header(appName, appVersion, compression, languageCount, messageCount, permissionCount, typeCount,
            componentCount, taskCount, directoryCount, fileCount, dataEntryCount);
    }

    /// <summary>Number of stored setup option flags for 5.3.9 ≤ v &lt; 6.1 unicode (see innoextract header.cpp).</summary>
    private static int HeaderFlagCount(InnoVersion v)
    {
        var n = 0;
        n += 1; // DisableStartupPrompt
        if (!v.AtLeast(5, 3, 10)) n++; // Uninstallable
        n += 1; // CreateAppDir
        n += 1; // AllowNoIcons
        n += 1; // AlwaysRestart
        n += 7; // AlwaysUsePersonalGroup .. Password: personal group, visible, caption, resizable, maximized, dir doesn't exist warning, password
        n += 2; // AllowRootDirectory, DisableFinishedPage
        if (!v.AtLeast(5, 6, 1)) n++; // ChangesAssociations
        n += 4; // UsePreviousAppDir, BackColorHorizontal, UsePreviousGroup, UpdateUninstallLogAppName
        n += 1; // UsePreviousSetupType
        n += 6; // DisableReadyMemo .. DisableReadyPage
        n += 2; // AlwaysShowDirOnReadyPage, AlwaysShowGroupOnReadyPage
        n += 1; // AllowUNCPath
        n += 2; // UserInfoPage, UsePreviousUserInfo
        n += 1; // UninstallRestartComputer
        n += 1; // RestartIfNeededByRun
        n += 1; // ShowTasksTreeLines
        n += 1; // AllowCancelDuringInstall
        n += 1; // WizardImageStretch
        n += 2; // AppendDefaultDirName, AppendDefaultGroupName
        n += 1; // EncryptionUsed
        if (!v.AtLeast(5, 6, 1)) n++; // ChangesEnvironment
        // unicode: no ShowUndisplayableLanguages
        n += 1; // SetupLogging
        n += 1; // SignedUninstaller
        n += 1; // UsePreviousLanguage
        n += 1; // DisableWelcomePage
        if (v.AtLeast(5, 5, 0)) n += 3; // CloseApplications, RestartApplications, AllowNetworkDrive
        if (v.AtLeast(5, 5, 7)) n += 1; // ForceCloseApplications
        if (v.AtLeast(6, 0, 0)) n += 3; // AppNameHasConsts, UsePreviousPrivileges, WizardResizable
        return n;
    }

    private static string ReadLanguage(InnoReader r, InnoVersion v)
    {
        var name = r.WideString(); // internal name
        r.SkipString(); // display name
        r.SkipString(); r.SkipString(); r.SkipString(); r.SkipString(); // dialog/title/welcome/copyright fonts
        r.SkipString(); // data
        r.SkipString(); r.SkipString(); r.SkipString(); // license, info before, info after
        r.U32(); // language id
        // unicode >= 5.3.0: no codepage
        r.U32(); r.U32(); r.U32(); r.U32(); // font sizes
        r.Bool(); // right to left
        return name;
    }

    private static void SkipType(InnoReader r)
    {
        for (var i = 0; i < 4; i++) r.SkipString(); // name, description, languages, check
        r.SkipWindowsVersionRange();
        r.Flags(1);
        r.U8(); // setup type
        r.U64(); // size
    }

    private static void SkipComponent(InnoReader r)
    {
        for (var i = 0; i < 5; i++) r.SkipString(); // name, description, types, languages, check
        r.U64(); // extra disk space
        r.I32(); // level
        r.Bool(); // used
        r.SkipWindowsVersionRange();
        r.Flags(5);
        r.U64(); // size
    }

    private static void SkipTask(InnoReader r)
    {
        for (var i = 0; i < 6; i++) r.SkipString(); // name, description, group description, components, languages, check
        r.I32(); // level
        r.Bool(); // used
        r.SkipWindowsVersionRange();
        r.Flags(5);
    }

    /// <summary>Condition strings shared by items: components, tasks, languages, check, after/before install. Returns languages.</summary>
    private static string ReadConditions(InnoReader r)
    {
        r.SkipString(); // components
        r.SkipString(); // tasks
        var languages = r.WideString();
        r.SkipString(); // check
        r.SkipString(); // after install
        r.SkipString(); // before install
        return languages;
    }

    private static InnoDirectoryEntry ReadDirectory(InnoReader r)
    {
        var name = r.WideString();
        var languages = ReadConditions(r);
        r.U32(); // attributes
        r.SkipWindowsVersionRange();
        r.I16(); // permission
        var options = (uint)r.Flags(5);
        return new InnoDirectoryEntry(name, languages, options);
    }

    private static InnoFileEntry ReadFile(InnoReader r)
    {
        r.SkipString(); // source
        var destination = r.WideString();
        r.SkipString(); // install font name
        r.SkipString(); // strong assembly name (5.2.5+)
        var languages = ReadConditions(r);
        r.SkipWindowsVersionRange();
        var location = r.U32();
        var attributes = r.U32();
        r.U64(); // external size
        r.I16(); // permission
        var options = r.Flags(32);
        var type = r.U8();
        return new InnoFileEntry(destination, languages, location, attributes, options, type);
    }

    private static InnoDataEntry ReadDataEntry(InnoReader r, InnoVersion v)
    {
        var firstSlice = r.U32();
        r.U32(); // last slice
        var chunkOffset = r.U32();
        var fileOffset = r.U64();
        var fileSize = r.U64();
        var chunkSize = r.U64();
        var sha1 = r.Bytes(20);
        var fileTime = r.I64();
        var versionMs = r.U32();
        var versionLs = r.U32();
        var flags = (uint)r.Flags(v.AtLeast(5, 5, 7) ? 11 : 9);
        return new InnoDataEntry(firstSlice, chunkOffset, fileOffset, fileSize, chunkSize, sha1, fileTime,
            ((ulong)versionMs << 32) | versionLs, flags);
    }
}
