using CommunityToolkit.Mvvm.ComponentModel;

namespace RoboExp.App.ViewModels;

/// <summary>Where a tool panel lives in the fixed layout (original WeifenLuo dock defaults).</summary>
public enum PanelDock { Left, Right, Bottom }

/// <summary>Stable ids of the main window's panels (original dock contents of MainForm).</summary>
public static class PanelIds
{
    public const string Icons = "Icons";              // DockFunctionsBox (palette)
    public const string Code = "Code";                // DockCodeBox (generated code of the active flow document)
    public const string Properties = "Properties";    // PropertyFrm
    public const string Variables = "Variables";      // VariableFrm
    public const string Hardware = "Hardware";        // hardware table of the active project (original HardInfoDlg)
    public const string TemplateCode = "TemplateCode";// DockSysFunc
    public const string Output = "Output";            // DockOutputBox
}

/// <summary>
/// A dockable tool window. <see cref="Content"/> is any view model with a view registered in <see cref="ViewLocator"/>;
/// later slices assign it (e.g. <c>main.Panel(PanelIds.Properties).Content = new PropertyGridViewModel(...)</c>),
/// typically in response to <see cref="MainWindowViewModel.ActiveDocumentChanged"/>.
/// </summary>
public sealed partial class ToolPanel : ObservableObject
{
    public ToolPanel(string id, string titleKey, PanelDock dock)
    {
        Id = id;
        TitleKey = titleKey;
        Dock = dock;
        Localization.Localizer.Instance.LanguageChanged += (_, _) => OnPropertyChanged(nameof(Title));
    }

    public string Id { get; }

    /// <summary><see cref="Localization.Localizer"/> key of the caption.</summary>
    public string TitleKey { get; }

    public string Title => Localization.Localizer.Instance.T(TitleKey);

    public PanelDock Dock { get; }

    [ObservableProperty]
    private object? _content;

    [ObservableProperty]
    private bool _isVisible = true;
}
