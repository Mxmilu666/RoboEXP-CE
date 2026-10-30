using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RoboExp.App;
using RoboExp.App.Flow;
using RoboExp.App.Services;
using RoboExp.App.ViewModels;
using RoboExp.App.Views;
using RoboExp.Core.Install;
using RoboExp.Tests.Rcu;
using AvPoint = Avalonia.Point;

namespace RoboExp.Tests.App.Ui;

/// <summary>Avalonia entry point for the headless session: the real <see cref="RoboExp.App.App"/> on Skia (frames can be captured).</summary>
public static class HeadlessEntry
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<RoboExp.App.App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>Skips UI tests that need the vendor install (vendor/app is gitignored).</summary>
public sealed class UiFactAttribute : FactAttribute
{
    public UiFactAttribute()
    {
        if (RcuVendor.Install == null) Skip = RcuVendor.MissingMessage;
    }
}

/// <summary>
/// Headless UI test harness: one Avalonia headless session per test run (Avalonia.Headless.XUnit needs xunit v3; this repo
/// uses xunit 2), each test gets a fresh <see cref="MainWindow"/> + <see cref="MainWindowViewModel"/> built exactly like
/// <c>App.OnFrameworkInitializationCompleted</c>, and all input goes through Avalonia's input pipeline
/// (<see cref="HeadlessWindowExtensions"/>: raw pointer/key/text/drag events on the window).
/// <para>Usage: <c>await UiHarness.RunAsync(async ui =&gt; { var doc = await ui.OpenExampleCopyAsync("E6RCU", "Other/MusicNoteTest"); ui.Click(...); });</c></para>
/// </summary>
public sealed class UiHarness
{
    private static readonly Lazy<HeadlessUnitTestSession> Session = new(() => HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry)));
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private UiHarness(MainWindow window, MainWindowViewModel vm, string workDir)
    {
        Window = window;
        Vm = vm;
        WorkDir = workDir;
    }

    public MainWindow Window { get; }
    public MainWindowViewModel Vm { get; }

    /// <summary>Per-test scratch directory (settings, project copies).</summary>
    public string WorkDir { get; }

    public VendorInstall Install => Vm.Env.Install!;

    /// <summary>Runs <paramref name="test"/> on the headless UI thread with a freshly opened main window (serialized across tests).</summary>
    public static async Task RunAsync(Func<UiHarness, Task> test, string robotType = "E6RCU", RoboExp.Core.Library.UiLanguage language = RoboExp.Core.Library.UiLanguage.Default)
    {
        await Gate.WaitAsync();
        try
        {
            await Session.Value.Dispatch(async () =>
            {
                var ui = Start(robotType, language);
                try { await test(ui); }
                finally { ui.Shutdown(); }
                return 0;
            }, CancellationToken.None);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Runs <paramref name="action"/> on the shared headless UI thread without opening a main window (serialized with the other UI tests).</summary>
    public static async Task RunOnUiThreadAsync(Func<Task> action)
    {
        await Gate.WaitAsync();
        try
        {
            await Session.Value.Dispatch(async () =>
            {
                await action();
                return 0;
            }, CancellationToken.None);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static UiHarness Start(string robotType, RoboExp.Core.Library.UiLanguage language)
    {
        AppComposition.Register();
        var dir = Path.Combine(Path.GetTempPath(), "roboexp-ui", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var settingsPath = Path.Combine(dir, "settings.json");
        var settings = new AppSettings { RobotType = robotType, Language = language };
        var env = new AppEnvironment(settings, settingsPath);
        var window = new MainWindow { Width = 1280, Height = 820 };
        var vm = new MainWindowViewModel(env, window);
        window.DataContext = vm;
        window.Show();
        var ui = new UiHarness(window, vm, dir);
        ui._dialogTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(20), DispatcherPriority.Background, (_, _) => ui.AnswerDialogs());
        ui._dialogTimer.Start();
        ui.Pump();
        return ui;
    }

    private DispatcherTimer? _dialogTimer;

    /// <summary>
    /// Button caption (e.g. the localized "&amp;No") the responder clicks in the next message box; null clicks the default
    /// button (OK / Yes). Message boxes are answered with real clicks, so tests never block on a modal dialog.
    /// </summary>
    public Func<string, string?>? DialogAnswer { get; set; }

    /// <summary>Texts of the message boxes answered so far.</summary>
    public List<string> Dialogs { get; } = [];

    private void AnswerDialogs()
    {
        foreach (var dialog in Window.OwnedWindows.OfType<MessageDialog>().ToList())
        {
            if (!dialog.IsVisible) continue;
            var text = string.Join("\n", dialog.GetVisualDescendants().OfType<SelectableTextBlock>().Select(t => t.Text));
            var buttons = dialog.GetVisualDescendants().OfType<Button>().ToList();
            if (buttons.Count == 0) continue;
            var wanted = DialogAnswer?.Invoke(text);
            var button = wanted == null ? buttons.FirstOrDefault(b => b.IsDefault) ?? buttons[0]
                : buttons.FirstOrDefault(b => Localizer_Strip(b.Content as string) == Localizer_Strip(wanted)) ?? buttons[0];
            Dialogs.Add(text);
            var p = button.TranslatePoint(new AvPoint(button.Bounds.Width / 2, button.Bounds.Height / 2), dialog)!.Value;
            dialog.MouseMove(p);
            dialog.MouseDown(p, MouseButton.Left);
            dialog.MouseUp(p, MouseButton.Left);
        }
    }

    private static string Localizer_Strip(string? s) => RoboExp.App.Localization.Localizer.StripMnemonic(s ?? "").Replace("_", "");


    private void Shutdown()
    {
        foreach (var doc in Vm.Documents) doc.IsDirty = false;
        _dialogTimer?.Stop();
        foreach (var owned in Window.OwnedWindows.ToList()) owned.Close();
        Window.Close();
        Pump();
    }

    /// <summary>Runs queued dispatcher jobs and a render tick (layout, bindings, posted callbacks).</summary>
    public void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Lets async continuations (dialogs, awaited commands) run.</summary>
    public async Task SettleAsync(int ms = 50)
    {
        Pump();
        await Task.Delay(ms);
        Pump();
    }

    // ------------------------------------------------------------------ documents

    /// <summary>Copies a vendor example folder (Robots/&lt;type&gt;/Examples/&lt;folder&gt;) into <see cref="WorkDir"/> and opens its .rcu.</summary>
    public async Task<FlowDocumentViewModel> OpenExampleCopyAsync(string robotType, string exampleFolder, string? rcuName = null)
    {
        var src = Path.Combine(Install.ExamplesDirectory(RobotType.Parse(robotType)), exampleFolder);
        var dst = Path.Combine(WorkDir, Path.GetFileName(exampleFolder));
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
        var rcu = Path.Combine(dst, rcuName ?? Path.GetFileName(exampleFolder) + ".rcu");
        var doc = await Vm.OpenPathAsync(rcu);
        await SettleAsync();
        return Assert.IsType<FlowDocumentViewModel>(doc);
    }

    /// <summary>The canvas of the active flow document.</summary>
    public FlowCanvas Canvas => Window.GetVisualDescendants().OfType<FlowCanvas>().First(c => c.IsEffectivelyVisible);

    /// <summary>Window coordinates of world point <paramref name="world"/> on the active canvas.</summary>
    public AvPoint WindowPoint(RoboExp.Core.Model.Point world)
    {
        var canvas = Canvas;
        var scroll = canvas.Document!.ScrollOffset;
        return canvas.TranslatePoint(new AvPoint(world.X - scroll.X + 0.5, world.Y - scroll.Y + 0.5), Window)
               ?? throw new InvalidOperationException("canvas not in window");
    }

    /// <summary>Scrolls the active canvas so <paramref name="world"/> is well inside the view.</summary>
    public void Reveal(RoboExp.Core.Model.Point world)
    {
        var canvas = Canvas;
        canvas.Document!.ScrollOffset = new RoboExp.Core.Model.Point(world.X - 200, world.Y - 150);
        canvas.Document.NotifyViewChanged();
        Pump();
    }

    // ------------------------------------------------------------------ input (window coordinates)

    /// <summary>The app's command modifier (Ctrl, Cmd on macOS) as the canvas reads it from the platform hotkey settings.</summary>
    public static RawInputModifiers Cmd
    {
        get
        {
            var mods = Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;
            return mods.HasFlag(KeyModifiers.Meta) ? RawInputModifiers.Meta : RawInputModifiers.Control;
        }
    }

    public void Move(AvPoint p, RawInputModifiers mods = RawInputModifiers.None)
    {
        Window.MouseMove(p, mods);
        Pump();
    }

    public void Press(AvPoint p, MouseButton button = MouseButton.Left, RawInputModifiers mods = RawInputModifiers.None)
    {
        Window.MouseMove(p, mods);
        Window.MouseDown(p, button, mods);
        Pump();
    }

    public void Release(AvPoint p, MouseButton button = MouseButton.Left, RawInputModifiers mods = RawInputModifiers.None)
    {
        Window.MouseUp(p, button, mods);
        Pump();
    }

    public void Click(AvPoint p, MouseButton button = MouseButton.Left, RawInputModifiers mods = RawInputModifiers.None)
    {
        Press(p, button, mods);
        Release(p, button, mods);
    }

    /// <summary>Two clicks inside the platform double-click time/distance (pointer ClickCount 2).</summary>
    public void DoubleClick(AvPoint p, RawInputModifiers mods = RawInputModifiers.None)
    {
        Click(p, MouseButton.Left, mods);
        Click(p, MouseButton.Left, mods);
    }

    /// <summary>Press at <paramref name="from"/>, move in steps, release at <paramref name="to"/>.</summary>
    public void Drag(AvPoint from, AvPoint to, RawInputModifiers mods = RawInputModifiers.None, int steps = 5)
    {
        Press(from, MouseButton.Left, mods);
        for (var i = 1; i <= steps; i++)
        {
            var t = i / (double)steps;
            Window.MouseMove(new AvPoint(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t), mods | RawInputModifiers.LeftMouseButton);
            Pump();
        }
        Release(to, MouseButton.Left, mods);
    }

    public void Key(Key key, RawInputModifiers mods = RawInputModifiers.None)
    {
        Window.KeyPress(key, mods, PhysicalKey.None, null);
        Pump();
        Window.KeyRelease(key, mods, PhysicalKey.None, null);
        Pump();
    }

    public void Type(string text)
    {
        Window.KeyTextInput(text);
        Pump();
    }

    /// <summary>OS-level drag of <paramref name="data"/> entering the window and dropped at <paramref name="at"/> (DragEnter, DragOver, Drop).</summary>
    public void DropAt(AvPoint at, IDataTransfer data, DragDropEffects effects = DragDropEffects.Copy)
    {
        Window.DragDrop(at, RawDragEventType.DragEnter, data, effects, RawInputModifiers.None);
        Window.DragDrop(at, RawDragEventType.DragOver, data, effects, RawInputModifiers.None);
        Window.DragDrop(at, RawDragEventType.Drop, data, effects, RawInputModifiers.None);
        Pump();
    }

    /// <summary>Window coordinates of the centre of <paramref name="control"/>.</summary>
    public AvPoint CenterOf(Control control) =>
        control.TranslatePoint(new AvPoint(control.Bounds.Width / 2, control.Bounds.Height / 2), Window) ?? throw new InvalidOperationException("not in window");

    // ------------------------------------------------------------------ evidence

    /// <summary>
    /// Saves a frame to <c>$ROBOEXP_UI_EVIDENCE/&lt;name&gt;.png</c> when that variable is set (manual evidence runs);
    /// otherwise does nothing.
    /// </summary>
    public void Evidence(string name)
    {
        var dir = Environment.GetEnvironmentVariable("ROBOEXP_UI_EVIDENCE");
        if (!string.IsNullOrEmpty(dir)) Screenshot(Path.Combine(dir, name + ".png"));
    }

    /// <summary>Renders the window (Skia) and saves a PNG; returns the path.</summary>
    public string Screenshot(string path)
    {
        Pump();
        var frame = Window.CaptureRenderedFrame() ?? throw new InvalidOperationException("no frame rendered");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        frame.Save(path);
        return path;
    }
}
