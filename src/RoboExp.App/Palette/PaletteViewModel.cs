using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RoboExp.App.Localization;
using RoboExp.App.Services;
using RoboExp.Core.Library;

namespace RoboExp.App.Palette;

/// <summary>One icon of a band (original IconPanel.AddIcon(caption, name, image, ...)).</summary>
public sealed class PaletteItemViewModel(PaletteDragItem item, string caption, string toolTip, Func<IImage?> icon)
{
    private IImage? _icon;
    private bool _iconLoaded;

    public PaletteDragItem Item { get; } = item;
    public string Caption { get; } = caption;
    public string ToolTip { get; } = toolTip;

    /// <summary>Decoded on first display only (a band can hold 100+ library icons).</summary>
    public IImage? Icon
    {
        get
        {
            if (!_iconLoaded) { _icon = icon(); _iconLoaded = true; }
            return _icon;
        }
    }
}

public sealed class PaletteBandViewModel(string caption, IReadOnlyList<PaletteItemViewModel> items)
{
    public string Caption { get; } = caption;
    public IReadOnlyList<PaletteItemViewModel> Items { get; } = items;
}

/// <summary>
/// The Icons window (original DockFunctionsBox/OutlookBar filled by MainForm.RefreshOutlookBar): a "Flow Control"
/// band with the control icons, then one band per shown Include.xml band of the robot's system library.
/// </summary>
public sealed partial class PaletteViewModel : ObservableObject
{
    /// <summary>Order and captions of PublicMethod.CreateCtrlIconList (End* icons are never shown in the palette).</summary>
    private static readonly (string IconType, string Caption, string NameKey)[] ControlIcons =
    [
        ("BeginIf", "If", "IfMsg"),
        ("BeginWhile", "While", "WhileMsg"),
        ("BeginFor", "For", "ForMsg"),
        ("Calculate", "Calculate", "CalculateMsg"),
        ("Continue", "Continue", "ContinueMsg"),
        ("Break", "Break", "BreakMsg"),
        ("Return", "Return", "ReturnMsg"),
        ("CodeEditor", "CodeEditor", "CodeEditorMsg"),
        ("Start", "Start", "StartMsg"),
    ];

    public ObservableCollection<PaletteBandViewModel> Bands { get; } = [];

    /// <summary>The open band; its icons fill the pane between the band buttons above and below it (OutlookBar).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BandsAbove), nameof(BandsBelow))]
    private PaletteBandViewModel? _selectedBand;

    /// <summary>Band buttons from the first through the selected band.</summary>
    public IReadOnlyList<PaletteBandViewModel> BandsAbove =>
        SelectedBand == null ? Bands.ToList() : Bands.Take(Bands.IndexOf(SelectedBand) + 1).ToList();

    /// <summary>Band buttons after the selected band.</summary>
    public IReadOnlyList<PaletteBandViewModel> BandsBelow =>
        SelectedBand == null ? [] : Bands.Skip(Bands.IndexOf(SelectedBand) + 1).ToList();

    [RelayCommand]
    private void SelectBand(PaletteBandViewModel band) => SelectedBand = band;

    /// <summary>Shown instead of library bands when the library could not be loaded.</summary>
    [ObservableProperty]
    private string? _error;

    /// <summary>
    /// RefreshOutlookBar: the control band, then every shown band of LibManager.RootNode (Include.xml system bands merged with
    /// UserInclude.xml user bands, <see cref="UserLibrary"/>) with its shown, loaded functions.
    /// </summary>
    public void Refresh(SystemLibrary? library, UserLibrary? userLibrary, UiLanguage language, string? error)
    {
        var selectedIndex = SelectedBand == null ? 0 : Bands.IndexOf(SelectedBand);
        Bands.Clear();
        Error = error;

        var loc = Localizer.Instance;
        Bands.Add(new PaletteBandViewModel(loc.T("FlowControl"), ControlIcons
            .Select(c => new PaletteItemViewModel(new PaletteDragItem(PaletteItemKind.Control, c.IconType), c.Caption,
                loc.T(c.NameKey), () => VendorImages.CtrlIcon(c.IconType)))
            .ToList()));

        if (userLibrary != null)
        {
            foreach (var band in userLibrary.Bands.Where(b => b.IsShown))
            {
                var items = new List<PaletteItemViewModel>();
                foreach (var fn in band.Functions.Where(f => f.IsShown && f.IsLoaded))
                {
                    if (fn.SystemFunction is { } f)
                    {
                        var caption = f.Caption.Get(language);
                        items.Add(new PaletteItemViewModel(new PaletteDragItem(PaletteItemKind.Function, f.Name),
                            caption.Length > 0 ? caption : f.Name, FunctionToolTip(f, language), () => VendorImages.FromFile(f.IconPath)));
                    }
                    else if (fn.ProjectInfo is { } p)
                    {
                        items.Add(new PaletteItemViewModel(new PaletteDragItem(PaletteItemKind.Function, p.ProjectName ?? ""),
                            string.IsNullOrEmpty(p.ProjectCaption) ? p.ProjectName ?? "" : p.ProjectCaption!, ProjectToolTip(p),
                            () => DecodeIcon(p.ProjectIcon)));
                    }
                }
                Bands.Add(new PaletteBandViewModel(userLibrary.CaptionOf(band), items));
            }
        }
        else if (library != null)
        {
            foreach (var band in library.Bands.Where(b => b.IsShown))
            {
                var items = new List<PaletteItemViewModel>();
                foreach (var name in band.FunctionNames)
                {
                    if (library.Find(name) is not { } f) continue;
                    var caption = f.Caption.Get(language);
                    items.Add(new PaletteItemViewModel(new PaletteDragItem(PaletteItemKind.Function, f.Name),
                        caption.Length > 0 ? caption : f.Name, FunctionToolTip(f, language), () => VendorImages.FromFile(f.IconPath)));
                }
                Bands.Add(new PaletteBandViewModel(band.Caption.Get(language), items));
            }
        }
        if (_projectBand != null) Bands.Add(_projectBand);
        SelectedBand = Bands[Math.Clamp(selectedIndex, 0, Bands.Count - 1)];
    }

    private PaletteBandViewModel? _projectBand;

    /// <summary>
    /// The active project's private subroutines band (original RefreshOutlookBar: non-system UsedProjectInfoList), shown
    /// last; null removes it. <paramref name="select"/> opens it (original SelectLastBand after New/Ref Subroutine).
    /// </summary>
    public void SetProjectBand(PaletteBandViewModel? band, bool select = false)
    {
        var wasSelected = SelectedBand != null && SelectedBand == _projectBand;
        if (_projectBand != null) Bands.Remove(_projectBand);
        _projectBand = band;
        if (band != null) Bands.Add(band);
        if (band != null && (select || wasSelected)) SelectedBand = band;
        else if (SelectedBand == null || !Bands.Contains(SelectedBand)) SelectedBand = Bands.FirstOrDefault();
        OnPropertyChanged(nameof(BandsAbove));
        OnPropertyChanged(nameof(BandsBelow));
    }

    /// <summary>Declaration line plus description (original tooltip: GetFuncDeclareCodeLine + ProjectDescription).</summary>
    public static string FunctionToolTip(LibFunction f, UiLanguage language)
    {
        var ret = string.IsNullOrEmpty(f.ReturnType) ? "void" : f.ReturnType;
        var decl = $"{ret} {f.Name}({string.Join(", ", f.Params.Select(p => $"{p.Type} {p.Name}"))})";
        var description = f.Description.Get(language);
        return description.Length == 0 ? decl : decl + "\n" + description;
    }

    /// <summary>Same tooltip for a subroutine's ProjectInfo (user library / project subroutines).</summary>
    public static string ProjectToolTip(RoboExp.Core.Model.ProjectInfo p) =>
        p.GetFuncDeclareCodeLine() + (string.IsNullOrEmpty(p.ProjectDescription) ? "" : "\n" + p.ProjectDescription);

    /// <summary>The original persisted project icon, shared with the canvas.</summary>
    public static Bitmap? DecodeIcon(RoboExp.Core.Model.EncodedImage? image) =>
        RoboExp.App.Flow.FlowImages.Decode(image);
}
