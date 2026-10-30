using System.Security.Cryptography;
using System.Text;
using RoboExp.Core.Install;
using RoboExp.Core.Install.Inno;

namespace RoboExp.Tests.Setup;

/// <summary>Skipped unless the original installer is found ($ROBOEXP_INSTALLER, or RoboEXP_*.exe in an ancestor of the test directory).</summary>
public sealed class InstallerFactAttribute : FactAttribute
{
    public InstallerFactAttribute()
    {
        if (InnoExtractorTests.InstallerPath == null) Skip = "RoboEXP installer .exe not found (set ROBOEXP_INSTALLER)";
    }
}

public class InnoExtractorTests
{
    internal static readonly string? InstallerPath = FindInstaller();

    private static string? FindInstaller()
    {
        var env = Environment.GetEnvironmentVariable("ROBOEXP_INSTALLER");
        if (!string.IsNullOrEmpty(env)) return File.Exists(env) ? env : null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var hit = dir.EnumerateFiles("*.exe")
                .FirstOrDefault(f => f.Name.StartsWith("RoboEXP_", StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit.FullName;
        }
        return null;
    }

    /// <summary>600 text lines + 1000 LCG bytes; the vectors below were produced from it with Python's lzma module
    /// (FORMAT_ALONE minus the 8-byte size field = Inno LZMA1; FORMAT_RAW LZMA2 prefixed with dictionary byte 16 = Inno LZMA2).</summary>
    private static byte[] SampleData()
    {
        var text = new StringBuilder();
        for (var i = 0; i < 600; i++) text.Append($"line {i} RoboExp {i * i % 97}\n");
        var data = new List<byte>(Encoding.ASCII.GetBytes(text.ToString()));
        long x = 12345;
        for (var i = 0; i < 1000; i++)
        {
            x = (x * 1103515245 + 12345) & 0x7fffffff;
            data.Add((byte)(x >> 16));
        }
        return data.ToArray();
    }

    private const string Lzma1Vector = "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";

    private const string Lzma2Vector = "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";

    private static byte[] ReadAllInSmallPieces(Stream s, int total)
    {
        var result = new byte[total];
        var n = 0;
        var piece = 1;
        while (n < total)
        {
            var got = s.Read(result, n, Math.Min(piece, total - n));
            Assert.True(got > 0);
            n += got;
            piece = piece * 3 % 997 + 1; // varying sizes exercise matches split across Read calls
        }
        return result;
    }

    [Fact]
    public void Lzma1Chunk_DecodesToOriginal()
    {
        var expected = SampleData();
        using var s = new InnoLzma1Stream(new MemoryStream(Convert.FromBase64String(Lzma1Vector)));
        Assert.Equal(expected, ReadAllInSmallPieces(s, expected.Length));
    }

    [Fact]
    public void Lzma2Chunk_DecodesToOriginal_AndEnds()
    {
        var expected = SampleData();
        using var s = new InnoLzma2Stream(new MemoryStream(Convert.FromBase64String(Lzma2Vector)));
        Assert.Equal(expected, ReadAllInSmallPieces(s, expected.Length));
        Assert.Equal(0, s.Read(new byte[16], 0, 16));
    }

    [Fact]
    public void Lzma2UncompressedChunks_AreCopied()
    {
        // dict byte, chunk 0x01 (dict reset) "abc", chunk 0x02 (no reset) "de", end
        byte[] stream = [16, 0x01, 0x00, 0x02, (byte)'a', (byte)'b', (byte)'c', 0x02, 0x00, 0x01, (byte)'d', (byte)'e', 0x00];
        using var s = new InnoLzma2Stream(new MemoryStream(stream));
        var buffer = new byte[10];
        var n = s.ReadAtLeast(buffer, 10, throwOnEndOfStream: false);
        Assert.Equal("abcde", Encoding.ASCII.GetString(buffer, 0, n));
    }

    [Fact]
    public void TruncatedLzma_Throws()
    {
        var data = Convert.FromBase64String(Lzma1Vector)[..200];
        using var s = new InnoLzma1Stream(new MemoryStream(data));
        Assert.Throws<EndOfStreamException>(() => s.ReadExactly(new byte[SampleData().Length]));
    }

    private static byte[] BuildBlock(byte[] payload)
    {
        var block = new MemoryStream();
        for (var off = 0; off < payload.Length; off += 4096)
        {
            var piece = payload.AsSpan(off, Math.Min(4096, payload.Length - off));
            block.Write(BitConverter.GetBytes(Crc32.Compute(piece)));
            block.Write(piece);
        }
        return block.ToArray();
    }

    [Fact]
    public void BlockStream_StripsChunkCrcs_AndDetectsCorruption()
    {
        var payload = SampleData();
        var block = BuildBlock(payload);
        Assert.Equal(payload.Length + 4 * 4, block.Length); // 12794 bytes = 4 chunks
        using (var s = new InnoBlockChunkStream(new MemoryStream(block)))
            Assert.Equal(payload, ReadAllInSmallPieces(s, payload.Length));

        block[5000] ^= 0x40;
        using var bad = new InnoBlockChunkStream(new MemoryStream(block));
        Assert.Throws<InvalidDataException>(() => bad.ReadExactly(new byte[payload.Length]));
    }

    [Fact]
    public void Crc32_MatchesStandardCheckValue() =>
        Assert.Equal(0xCBF43926u, Crc32.Compute("123456789"u8));

    [Fact]
    public void CallFilter_RestoresRelativeTargets()
    {
        // Forward CALL +0x10 at offset 0: stored absolute = rel + 5. Backward JMP -0x10 at offset 5: stored
        // absolute = (rel + 10) & 0xFFFFFF with the 0xFF high byte flipped to 0x00 (5.3.9+).
        byte[] stored = [0xE8, 0x15, 0x00, 0x00, 0x00, 0xE9, 0xFA, 0xFF, 0xFF, 0x00, 0x90];
        byte[] expected = [0xE8, 0x10, 0x00, 0x00, 0x00, 0xE9, 0xF0, 0xFF, 0xFF, 0xFF, 0x90];

        var whole = new byte[stored.Length + 4];
        var filter = new CallInstructionFilter(flipHighByte: true);
        var n = filter.Process(stored, whole);
        n += filter.Flush(whole.AsSpan(n));
        Assert.Equal(expected, whole[..n]);

        // Same result when the input arrives one byte at a time.
        var split = new CallInstructionFilter(flipHighByte: true);
        var output = new List<byte>();
        var tmp = new byte[8];
        foreach (var b in stored)
        {
            var m = split.Process([b], tmp);
            output.AddRange(tmp[..m]);
        }
        var f = split.Flush(tmp);
        output.AddRange(tmp[..f]);
        Assert.Equal(expected, output);
    }

    [Fact]
    public void CallFilter_LeavesTruncatedAndBlockSpanningInstructionsAlone()
    {
        byte[] tail = [0x90, 0xE8, 0x01, 0x02];
        var output = new byte[tail.Length + 4];
        var filter = new CallInstructionFilter(true);
        var n = filter.Process(tail, output);
        n += filter.Flush(output.AsSpan(n));
        Assert.Equal(tail, output[..n]);

        var block = new byte[0x10000 + 8];
        block[0xFFFD] = 0xE8; // only 2 bytes left in the 64 KiB block: not transformed
        block[0xFFFE] = 0x55;
        var outBlock = new byte[block.Length + 4];
        var f2 = new CallInstructionFilter(true);
        var m = f2.Process(block, outBlock);
        m += f2.Flush(outBlock.AsSpan(m));
        Assert.Equal(block, outBlock[..m]);
    }

    [Theory]
    [InlineData(@"{app}\Robots\E6RCU\a.c", "app/Robots/E6RCU/a.c")]
    [InlineData(@"{app}\x:y?.txt", "app/x$y$.txt")]
    [InlineData(@"{tmp}\..\setup.dll", "setup.dll")]
    [InlineData(@"{{literal}\.\b", "{literal}/b")]
    [InlineData(@"{code:GetDir}\f", "code$GetDir/f")]
    public void ConvertPath_MatchesInnoextract(string destination, string expected) =>
        Assert.Equal(expected, InnoExtractor.ConvertPath(destination));

    [InstallerFact]
    public void Installer_HeadersAndPlan()
    {
        using var exe = File.OpenRead(InstallerPath!);
        var info = InnoSetupInfo.Load(exe);
        Assert.Equal("Inno Setup Setup Data (5.5.0) (u)", info.VersionString);
        Assert.Equal(new InnoVersion(5, 5, 0, true), info.Version);
        Assert.Equal("RoboEXP", info.AppName);
        Assert.Equal("v6.0.8", info.AppVersion);
        Assert.Equal(InnoCompression.Lzma1, info.Compression);
        Assert.Equal(["english", "chinesesimplified"], info.Languages);
        Assert.Equal(62, info.Directories.Count);
        Assert.Equal(11248, info.Files.Count);
        Assert.Equal(11246, info.DataEntries.Count);

        var plan = InnoExtractor.Plan(info);
        Assert.Equal(11246, plan.Files.Count); // two RoboExp.exe entries collapse into one path
        Assert.Equal(1375, plan.Directories.Count);
        Assert.Single(plan.Files, f => f.Path == "app/RoboExp.exe");
        Assert.Contains(plan.Files, f => f.Path == "app/Compiler/JMCompiler.xml");
    }

    [InstallerFact]
    public async Task Installer_ImportsVerifiedInstall()
    {
        var root = Path.Combine(Path.GetTempPath(), "roboexp-import-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var result = await VendorImporter.ImportInstallerAsync(InstallerPath!, root);
            Assert.Equal(VendorImportMethod.Managed, result.Method);
            Assert.Equal("v6.0.8", result.Version);
            Assert.Equal(11246, result.FileCount);
            Assert.Equal(Path.Combine(root, "v6.0.8"), result.InstallRoot);
            Assert.True(VendorInstall.IsInstall(result.InstallRoot));
            Assert.Equal(11246, Directory.EnumerateFiles(result.InstallRoot, "*", SearchOption.AllDirectories).Count());
            Assert.DoesNotContain(Directory.EnumerateDirectories(root), d => Path.GetFileName(d).StartsWith(".import-"));

            string Sha(string rel) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(result.InstallRoot, rel))));
            Assert.Equal("6bfb82390e71431775f20ba15ddfc032f4de0fdad3010d86ffe683eac5cb8760", Sha("Compiler/JMCompiler.xml"));
            Assert.Equal("d4802b0f00c6edd6a2ce3799d19cc492b4b9ec32b3bd15162aac7283debe5dbe", Sha("RoboExp.exe")); // CALL-filtered
            Assert.Equal("36849d73884da4dfdc34cb41912cf99c6430ffb6cb79bfa07c346c9eda133ab2", Sha("Robots/E6RCU/Include/JMlib.xml"));
            // Data shared by several entries (innoextract 1.9 drops all but one of these).
            Assert.True(File.Exists(Path.Combine(result.InstallRoot, "Robots/C1RCU/Examples/BuiltIn/GetIRCodeTest/HardwareInfo.c")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ImportDirectory_AcceptsInstallOrParentOfApp_RejectsOthers()
    {
        var root = Path.Combine(Path.GetTempPath(), "roboexp-dir-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var app = Path.Combine(root, "app");
            Directory.CreateDirectory(Path.Combine(app, "Compiler"));
            Directory.CreateDirectory(Path.Combine(app, "Robots"));
            File.WriteAllText(Path.Combine(app, "Compiler", "JMCompiler.xml"), "<x/>");
            Assert.Equal(app, VendorImporter.ImportDirectory(app).InstallRoot);
            Assert.Equal(app, VendorImporter.ImportDirectory(root).InstallRoot);
            Assert.Throws<DirectoryNotFoundException>(() => VendorImporter.ImportDirectory(Path.Combine(app, "Robots")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Load_RejectsNonInnoExecutable()
    {
        var bytes = new byte[4096];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        Assert.Throws<UnsupportedInstallerException>(() => InnoSetupInfo.Load(new MemoryStream(bytes)));
    }
}
