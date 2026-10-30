using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RoboExp.App.Localization;
using RoboExp.Core.Model;

namespace RoboExp.App.Subroutines;

/// <summary>UpdateDlgResult of the original.</summary>
public enum UpdateAnswer { Yes, No, All, None }

/// <summary>
/// Port of UpdateSubDlg: library vs. project signature of one private subroutine side by side
/// (Name, Caption, Path, Icon, ReturnType, Parameters) and Yes / All / No / None.
/// </summary>
internal sealed class UpdateSubWindow : Window
{
    public UpdateSubWindow(ProjectInfo library, ProjectInfo project)
    {
        var loc = Localizer.Instance;
        Title = loc.T("UpdateSubDlg:$this.Text");
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children =
            {
                SubroutineDialogs.DialogButton(loc["UpdateSubDlg:btnYes.Text"], () => Finish(UpdateAnswer.Yes), isDefault: true),
                SubroutineDialogs.DialogButton(loc["UpdateSubDlg:btnAll.Text"], () => Finish(UpdateAnswer.All)),
                SubroutineDialogs.DialogButton(loc["UpdateSubDlg:btnNo.Text"], () => Finish(UpdateAnswer.No), isCancel: true),
                SubroutineDialogs.DialogButton(loc["UpdateSubDlg:btnNone.Text"], () => Finish(UpdateAnswer.None)),
            },
        };

        Content = new StackPanel
        {
            Margin = new Thickness(12),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = loc.T("UpdateSubDlg:label3.Text"), TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = loc.T("UpdateSubDlg:lblSystem.Text"), FontWeight = FontWeight.SemiBold },
                PropertyTable(library),
                new TextBlock { Text = loc.T("UpdateSubDlg:lblProj.Text"), FontWeight = FontWeight.SemiBold },
                PropertyTable(project),
                buttons,
                new Border
                {
                    BorderBrush = Brushes.Gray,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(4, 2),
                    Child = new TextBlock { Text = loc.T("UpdateSubDlg:label4.Text").Trim(), Foreground = Brushes.Red },
                },
            },
        };
    }

    /// <summary>Closing with the window button counts as No (btnNo is the CancelButton).</summary>
    public UpdateAnswer Answer { get; private set; } = UpdateAnswer.No;

    private void Finish(UpdateAnswer answer)
    {
        Answer = answer;
        Close();
    }

    /// <summary>AddProperty/AddValue: the six property rows (labels are untranslated in the original too).</summary>
    private static Control PropertyTable(ProjectInfo info)
    {
        var parameters = string.Join(", ", (info.ParamList ?? []).Select(p => p.Type + " " + p.Name));
        var rows = new (string Label, Control Value)[]
        {
            ("Name", Value(info.ProjectName)),
            ("Caption", Value(info.ProjectCaption)),
            ("Path", Value(info.ProjectPath)),
            ("Icon", new Image { Source = SubroutineDialogs.ToBitmap(info.ProjectIcon), Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Left }),
            ("ReturnType", Value(info.ReturnType?.Type)),
            ("Parameters", Value(parameters)),
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("1*,3*") };
        for (var i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.Children.Add(Cell(new TextBlock { Text = rows[i].Label }, i, 0));
            grid.Children.Add(Cell(rows[i].Value, i, 1));
        }
        return new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1, 1, 0, 0), Child = grid };
    }

    private static TextBlock Value(string? text) => new() { Text = text ?? "", TextTrimming = TextTrimming.CharacterEllipsis, [ToolTip.TipProperty] = text };

    private static Border Cell(Control content, int row, int column) => new()
    {
        BorderBrush = Brushes.LightGray,
        BorderThickness = new Thickness(0, 0, 1, 1),
        Padding = new Thickness(4, 2),
        Child = content,
        [Grid.RowProperty] = row,
        [Grid.ColumnProperty] = column,
    };
}
