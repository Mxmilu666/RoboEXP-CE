using Avalonia;
using System.Text;
using RoboExp.App.Localization;
using RoboExp.App.Palette;
using RoboExp.App.Services;
using RoboExp.App.ViewModels;
using RoboExp.Core.Install;
using RoboExp.Core.Library;

namespace RoboExp.Tests.App;

public class MnemonicTests
{
    [Theory]
    [InlineData("&File", "_File")]
    [InlineData("文件(&F)", "文件(_F)")]
    [InlineData("Save A&ll", "Save A_ll")]
    [InlineData("R&&D", "R&D")]
    [InlineData("a_b", "a__b")]
    [InlineData("Compile", "Compile")]
    public void ToAccessText_converts_winforms_mnemonics(string input, string expected) =>
        Assert.Equal(expected, Localizer.ToAccessText(input));

    [Theory]
    [InlineData("&Open...", "Open...")]
    [InlineData("打开(&O)...", "打开...")]
    [InlineData("取消(&C)", "取消")]
    [InlineData("R&&D", "R&D")]
    public void StripMnemonic_removes_mnemonics(string input, string expected) =>
        Assert.Equal(expected, Localizer.StripMnemonic(input));
}

public class TextFileCodecTests
{
    [Fact]
    public void Gbk_without_bom_round_trips_chinese()
    {
        const string text = "//应用程序模板 \r\nint main(void)\r\n{\r\n\tLCD_Print(\"你好\");\r\n}\r\n";
        var bytes = TextFileCodec.Encode(text, TextFileCodec.Gbk);
        Assert.Equal(new byte[] { 0xD3, 0xA6 }, bytes[2..4]); // 应 in GBK
        var (decoded, encoding) = TextFileCodec.Decode(bytes);
        Assert.Equal(text, decoded);
        Assert.Equal(936, encoding.CodePage);
    }

    [Fact]
    public void Utf8_bom_is_detected_and_preserved_on_save()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("中文")).ToArray();
        var (decoded, encoding) = TextFileCodec.Decode(bytes);
        Assert.Equal("中文", decoded);
        Assert.Equal(bytes, TextFileCodec.Encode(decoded, encoding));
    }

    [Fact]
    public void Characters_outside_gbk_fail_instead_of_becoming_question_marks() =>
        Assert.Throws<EncoderFallbackException>(() => TextFileCodec.Encode("emoji 😀", TextFileCodec.Gbk));
}

public class AppSettingsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("roboexp-settings").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Missing_or_corrupt_file_yields_original_defaults()
    {
        var path = Path.Combine(_dir, "settings.json");
        var s = AppSettings.Load(path);
        Assert.True(s.CompileBeforeDownload);   // ConfigInfo.m_IsCompileBeforeDownload = true
        Assert.True(s.ShowMessageAfterDownload); // ConfigInfo.m_IsShowMsgAfterDownload = true
        File.WriteAllText(path, "{ not json");
        Assert.Null(AppSettings.Load(path).Language);
    }

    [Fact]
    public void Round_trips_and_caps_recent_files()
    {
        var path = Path.Combine(_dir, "sub", "settings.json");
        var s = new AppSettings { Language = UiLanguage.CHT, RobotType = "X4RCU", ToolchainBin = "/opt/arm/bin", CompileBeforeDownload = false };
        for (var i = 0; i < 15; i++) s.AddRecentFile($"/p/{i}.c");
        s.AddRecentFile("/P/3.C"); // case-insensitive move to front, like the original MRU
        s.Save(path);
        var loaded = AppSettings.Load(path);
        Assert.Equal(UiLanguage.CHT, loaded.Language);
        Assert.Equal("X4RCU", loaded.RobotType);
        Assert.Equal("/opt/arm/bin", loaded.ToolchainBin);
        Assert.False(loaded.CompileBeforeDownload);
        Assert.Equal(AppSettings.MaxRecentFiles, loaded.RecentFiles.Count);
        Assert.Equal("/P/3.C", loaded.RecentFiles[0]);
        Assert.Single(loaded.RecentFiles, f => f.Equals("/p/3.c", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("CHS", UiLanguage.CHS)]
    [InlineData("cht", UiLanguage.CHT)]
    [InlineData("Default", UiLanguage.Default)]
    public void ParseLanguage_uses_original_culture_codes(string code, UiLanguage expected) =>
        Assert.Equal(expected, AppSettings.ParseLanguage(code));

    [Fact]
    public void Defaults_without_install_are_chs_and_e6rcu()
    {
        var s = new AppSettings();
        Assert.Equal(UiLanguage.CHS, s.EffectiveLanguage(null));
        Assert.Equal("E6RCU", s.EffectiveRobotType(null).Name);
        s.RobotType = "x4rcu";
        Assert.Equal("X4RCU", s.EffectiveRobotType(null).Name);
    }
}

public class OutputRowTests
{
    [Fact]
    public void Gcc_error_line_is_located()
    {
        var row = OutputRow.FromLine("/w/a.c:12:5: error: 'x' undeclared 没有声明", "编译成功");
        Assert.Equal(OutputSeverity.Error, row.Severity);
        Assert.Equal(12, row.Line);
        Assert.Equal(5, row.Column);
        Assert.Equal("/w/a.c", row.File);
        Assert.True(row.CanNavigate);
    }

    [Fact]
    public void Context_and_success_lines_are_not_navigable()
    {
        var info = OutputRow.FromLine("/w/a.c: 在函数中 'main':", "编译成功");
        Assert.Equal(OutputSeverity.Info, info.Severity);
        Assert.False(info.CanNavigate);
        Assert.Equal(OutputSeverity.Success, OutputRow.FromLine("编译成功", "编译成功").Severity);
    }
}

public class PaletteDragDataTests
{
    [Theory]
    [InlineData(PaletteItemKind.Control, "BeginIf", "ctrl:BeginIf")]
    [InlineData(PaletteItemKind.Function, "SetMotor", "func:SetMotor")]
    public void Payload_format_is_stable(PaletteItemKind kind, string name, string payload)
    {
        Assert.Equal(payload, PaletteDragData.Encode(new PaletteDragItem(kind, name)));
        Assert.Equal(new PaletteDragItem(kind, name), PaletteDragData.Decode(payload));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ctrl:")]
    [InlineData("SetMotor")]
    public void Invalid_payloads_are_rejected(string? payload) => Assert.Null(PaletteDragData.Decode(payload));
}

public class TemplateCodeTests
{
    [Fact]
    public void Call_template_matches_GetCodesTemple()
    {
        var f = new LibFunction("SetMotor", "1", "", LocalizedText.Empty, LocalizedText.Empty, "void", LocalizedText.Empty,
            [new LibParam("port", "_M_", "", "", "", LocalizedText.Empty), new LibParam("speed", "int", "", "", "", LocalizedText.Empty)], null);
        Assert.Equal("SetMotor( _M_:port ,int:speed )", TemplateCodeViewModel.CallTemplate(f));
    }
}

/// <summary>Switches the process-wide <see cref="Localizer"/> language; must not overlap the headless UI tests that read it.</summary>
[CollectionDefinition(nameof(LocalizerStateCollection), DisableParallelization = true)]
public sealed class LocalizerStateCollection;

[Collection(nameof(LocalizerStateCollection))]
public class VendorStringsTests
{
    /// <summary>
    /// Language changes notify every bound control; the headless UI tests' windows belong to the headless dispatcher thread,
    /// so the switch runs there (inside the UI harness session).
    /// </summary>
    [Fact]
    public Task Original_strings_resolve_per_language_with_neutral_fallback() =>
        RoboExp.Tests.App.Ui.UiHarness.RunOnUiThreadAsync(() => { Check(); return Task.CompletedTask; });

    private static void Check()
    {
        var loc = Localizer.Instance;
        var previousLanguage = loc.Language;
        loc.Load();
        var menu = new Avalonia.Controls.MenuItem();
        using var binding = menu.Bind(Avalonia.Controls.MenuItem.HeaderProperty,
            new TrExtension("File").ProvideValue(null!));
        try
        {
            loc.SetLanguage(UiLanguage.CHS);
            Assert.Equal("文件(_F)", loc["File"]);
            Assert.Equal(loc["File"], menu.Header);
            Assert.Equal("编译成功", loc.T("CompilationSuccessful"));
            Assert.Equal("选项", loc.T("Options:$this.Text"));
            Assert.Equal("English", loc.T("Options:comboBoxLanguage.Items")); // CHS satellite lacks it → neutral
            loc.SetLanguage(UiLanguage.CHT);
            Assert.Equal("選項", loc.T("Options:$this.Text"));
            Assert.Equal(loc["File"], menu.Header);
            loc.SetLanguage(UiLanguage.Default);
            Assert.Equal("_File", loc["File"]);
            Assert.Equal(loc["File"], menu.Header);
            Assert.Equal("Compilation successful", loc.T("CompilationSuccessful"));
        }
        finally { loc.SetLanguage(previousLanguage); }
    }

    [Fact]
    public Task Original_images_decode_without_a_vendor_install() =>
        RoboExp.Tests.App.Ui.UiHarness.RunOnUiThreadAsync(() =>
        {
            Localizer.Instance.Load();
            var png = LegacyResources.GetResource("RoboExp.Resources.Images.Compile.png");
            Assert.NotNull(png);
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png![..4]);
            var compile = VendorImages.Get("RoboExp.Resources.Images.Compile.png");
            Assert.NotNull(compile);
            Assert.True(compile.PixelSize.Width > 0 && compile.PixelSize.Height > 0);
            var control = VendorImages.CtrlIcon("BeginIf");
            Assert.NotNull(control);
            Assert.True(control.PixelSize.Width > 0 && control.PixelSize.Height > 0);
            foreach (var key in new[] { "pictureBox3.Image", "pictureBox4.Image" })
            {
                using var picture = FormResourceImages.Get("PropertyFrmNew", key);
                Assert.NotNull(picture);
                Assert.True(picture.PixelSize.Width > 0 && picture.PixelSize.Height > 0);
            }
            Assert.NotNull(RoboExp.App.NewProject.DefaultProjectIcon.Get());
            return Task.CompletedTask;
        });
}
