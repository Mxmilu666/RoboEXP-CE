using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using RoboExp.App.Localization;
using RoboExp.App.Views;
using RoboExp.Core.Model;

namespace RoboExp.App.NewProject;

/// <summary>
/// Original ProjectInfoFrm: General (name/location read-only, author, version, caption, description, icon), Return
/// (type for subroutines, description) and Parameter (list with add/delete/save) pages. Edits a working copy;
/// <see cref="ApplyTo"/> writes it back (the caller wraps that in an undoable edit).
/// </summary>
public sealed class ProjectInfoWindow : Window
{
    private readonly ProjectInfo _info;
    private readonly IReadOnlyList<HardwareItem> _hardware;
    private readonly SoftwareType _software;
    private readonly ObservableCollection<ParameterClass> _params = [];
    private readonly ListBox _paramList = new() { Height = 120 };
    private readonly TextBox _author = new(), _version = new(), _caption = new(), _desc = new() { AcceptsReturn = true, Height = 48 };
    private readonly ComboBox _retType = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _retDesc = new();
    private readonly TextBox _pName = new(), _pDefault = new(), _pMin = new(), _pMax = new(), _pDesc = new();
    private readonly ComboBox _pType = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Image _icon = new() { Width = 32, Height = 32 };
    private EncodedImage? _iconData;

    public bool Accepted { get; private set; }

    public ProjectInfoWindow(ProjectInfo info, IReadOnlyList<HardwareItem> hardwareItems, SoftwareType software)
    {
        _info = info;
        _hardware = hardwareItems;
        _software = software;
        var l = Localizer.Instance;
        Title = l.T("ProjectProperties");
        Width = 600;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _author.Text = info.UserName;
        _version.Text = info.UserVersion;
        _caption.Text = info.ProjectCaption;
        _desc.Text = info.ProjectDescription;
        _iconData = info.ProjectIcon;
        _icon.Source = Decode(_iconData);
        _retType.ItemsSource = ProjectInfoRules.ReturnTypes;
        _retType.SelectedItem = ProjectInfoRules.ReturnTypes.Contains(info.ReturnType?.Type ?? "") ? info.ReturnType!.Type : "void";
        _retType.IsEnabled = info.ProjectType is not (ProjectType.FlowApp or ProjectType.CodeApp);
        _retDesc.Text = info.ReturnType?.Description;
        _pType.ItemsSource = ProjectInfoRules.ParameterTypes(hardwareItems);
        _pType.SelectedIndex = 0;
        foreach (var p in info.ParamList ?? []) _params.Add(Clone(p));
        _paramList.ItemsSource = _params;
        _paramList.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<ParameterClass>((p, _) =>
            new TextBlock { Text = p == null ? "" : $"{p.Name}  ({p.Type})  {p.DefaultValue}  [{p.Min}..{p.Max}]  {p.Description}" });
        _paramList.SelectionChanged += (_, _) => LoadSelected();

        Control Row(string key, Control c)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*"), Margin = new Thickness(0, 1) };
            g.Children.Add(new TextBlock { Text = l.T(key), VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(c, 1);
            g.Children.Add(c);
            return g;
        }
        TextBlock Header(string key) => new() { Text = l.T(key), FontWeight = FontWeight.Bold, Margin = new Thickness(0, 8, 0, 2) };

        var browse = new Button { Content = "..." };
        browse.Click += async (_, _) => await PickIconAsync();
        var add = new Button { Content = l.T("Xwb:VariableFrm_Add") };
        var del = new Button { Content = l.T("Xwb:VariableFrm_Delete") };
        var save = new Button { Content = l.T("Port:Info.SaveParam") };
        add.Click += async (_, _) => await SaveParamAsync(-1);
        save.Click += async (_, _) => await SaveParamAsync(_paramList.SelectedIndex);
        del.Click += async (_, _) =>
        {
            if (_paramList.SelectedIndex < 0) { await Warn("Xwb:ProjectInfoFrm_ErrSelNull"); return; }
            _params.RemoveAt(_paramList.SelectedIndex);
            if (_params.Count > 0) _paramList.SelectedIndex = 0;
        };
        var ok = new Button { Content = l.T("Options:btnOK.Text"), MinWidth = 80, IsDefault = true };
        var cancel = new Button { Content = l.T("Options:btnCancel.Text"), MinWidth = 80, IsCancel = true };
        ok.Click += (_, _) => { Accepted = true; Close(); };
        cancel.Click += (_, _) => Close();

        var paramEdit = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        var leftP = new StackPanel { Margin = new Thickness(0, 0, 6, 0), Children = { Row("Xwb:ProjectInfoFrm_Name", _pName), Row("Xwb:Type", _pType), Row("Xwb:ProjectInfoFrm_Description", _pDesc) } };
        var rightP = new StackPanel { Children = { Row("Xwb:Default", _pDefault), Row("Xwb:Minimum", _pMin), Row("Xwb:Maximum", _pMax) } };
        Grid.SetColumn(rightP, 1);
        paramEdit.Children.Add(leftP);
        paramEdit.Children.Add(rightP);

        var root = new StackPanel
        {
            Margin = new Thickness(12),
            Children =
            {
                Header("Xwb:PropertyFrm_GeneralTitle"),
                Row("Xwb:ProjectInfoFrm_Name", new SelectableTextBlock { Text = info.ProjectName }),
                Row("Xwb:ProjectInfoFrm_Location", new SelectableTextBlock { Text = info.ProjectPath, TextWrapping = TextWrapping.Wrap }),
                Row("Xwb:ProjectInfoFrm_Author", _author),
                Row("Xwb:ProjectInfoFrm_UserVersion", _version),
                Row("Xwb:ProjectInfoFrm_Caption", _caption),
                Row("Xwb:ProjectInfoFrm_Description", _desc),
                Row("Xwb:ProjectInfoFrm_Icon", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Child = _icon }, browse } }),
                Header("Xwb:PropertyFrm_ReturnTitle"),
                Row("Xwb:PropertyFrm_ReturnType", _retType),
                Row("Xwb:ProjectInfoFrm_Description", _retDesc),
                Header("Xwb:PropertyFrm_ParameterTitle"),
                _paramList,
                paramEdit,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 4), Children = { add, save, del } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0), Children = { ok, cancel } },
            },
        };
        Content = new ScrollViewer { Content = root, MaxHeight = 760 };
        if (_params.Count > 0) _paramList.SelectedIndex = 0;
    }

    private static ParameterClass Clone(ParameterClass p) => new()
    {
        Version = p.Version, Name = p.Name, NameCHS = p.NameCHS, NameCHT = p.NameCHT, NameDefault = p.NameDefault, Type = p.Type,
        DefaultValue = p.DefaultValue, Min = p.Min, Max = p.Max, Description = p.Description, IsHardwareType = p.IsHardwareType, IsEnumType = p.IsEnumType,
    };

    private void LoadSelected()
    {
        if (_paramList.SelectedItem is not ParameterClass p) return;
        _pName.Text = p.Name;
        _pType.SelectedItem = p.Type;
        _pDefault.Text = p.DefaultValue;
        _pMin.Text = p.Min;
        _pMax.Text = p.Max;
        _pDesc.Text = p.Description;
    }

    private Task Warn(string key) =>
        MessageDialog.ShowAsync(this, Localizer.Instance.T(key), Localizer.Instance.T("Xwb:RobotExpress"), MessageDialog.Buttons.Ok);

    /// <summary>AddParameter (index -1) / ModifyParameter.</summary>
    private async Task SaveParamAsync(int index)
    {
        var name = _pName.Text ?? "";
        var type = _pType.SelectedItem as string ?? "";
        var err = ProjectInfoRules.CheckParamName(_params, index, name)
                  ?? ProjectInfoRules.CheckParamDefine(type, _pDefault.Text ?? "", _pMin.Text ?? "", _pMax.Text ?? "", _software);
        if (err != null) { await Warn(err); return; }
        var p = index >= 0 ? _params[index] : new ParameterClass();
        ProjectInfoRules.Apply(p, name, type, _pDesc.Text ?? "", _pDefault.Text ?? "", _pMin.Text ?? "", _pMax.Text ?? "", _hardware);
        if (index < 0) { _params.Add(p); _paramList.SelectedItem = p; }
        else { _params[index] = p; _paramList.SelectedIndex = index; }
    }

    private static Bitmap? Decode(EncodedImage? image) =>
        RoboExp.App.Flow.FlowImages.Decode(image);

    /// <summary>BtnOpenIcon: load an image and scale it to a 32×32 thumbnail (stored as PNG).</summary>
    private async Task PickIconAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Images") { Patterns = ["*.png", "*.bmp", "*.jpg", "*.jpeg", "*.gif", "*.ico"] }],
        });
        if (files.Count == 0) return;
        try
        {
            await using var s = await files[0].OpenReadAsync();
            using var src = new Bitmap(s);
            using var thumb = src.CreateScaledBitmap(new PixelSize(32, 32));
            var ms = new MemoryStream();
#pragma warning disable CS0618 // Save(Stream) writes PNG; same as FlowImages.EncodeControlIcon
            thumb.Save(ms);
#pragma warning restore CS0618
            _iconData = new EncodedImage(ms.ToArray());
            _icon.Source = Decode(_iconData);
        }
        catch (Exception)
        {
            await Warn("Xwb:ProjectInfoFrm_ErrIconOpen");
        }
    }

    /// <summary>BtnOk_Click: writes the edited fields into <paramref name="target"/>.</summary>
    public void ApplyTo(ProjectInfo target)
    {
        target.ProjectIcon = _iconData ?? DefaultProjectIcon.Get();
        target.UserName = _author.Text ?? "";
        target.UserVersion = _version.Text ?? "";
        target.ProjectCaption = ProjectInfoRules.Caption(_caption.Text ?? "", target.ProjectName);
        target.ProjectDescription = _desc.Text ?? "";
        target.ReturnType = new ReturnType { Type = _retType.SelectedItem as string ?? "void", Description = _retDesc.Text ?? "" };
        target.ParamList = new ArrayList<ParameterClass>();
        foreach (var p in _params) target.ParamList.Add(p);
    }
}
