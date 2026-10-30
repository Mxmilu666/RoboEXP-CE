using Avalonia.Controls;
using Avalonia.Input;

namespace RoboExp.App.Palette;

public partial class PaletteView : UserControl
{
    public PaletteView() => InitializeComponent();

    /// <summary>Drag source: left-press on an icon starts a Copy drag carrying <see cref="PaletteDragData"/>.</summary>
    private async void OnItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: PaletteItemViewModel item } control) return;
        if (!e.GetCurrentPoint(control).Properties.IsLeftButtonPressed) return;
        await DragDrop.DoDragDropAsync(e, PaletteDragData.Create(item.Item), DragDropEffects.Copy);
    }
}
