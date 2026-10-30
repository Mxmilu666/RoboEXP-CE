using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace RoboExp.App.Hardware;

public partial class HardwarePanelView : UserControl
{
    private HardwarePanelViewModel? _vm;

    public HardwarePanelView() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.OwnerProvider = null;
        }
        _vm = DataContext as HardwarePanelViewModel;
        if (_vm == null) return;
        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.OwnerProvider = () => TopLevel.GetTopLevel(this) as Window;
        Picture.SetBoard(_vm.Board, _vm.Picture);
        Picture.Marks = _vm.Marks;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm == null) return;
        switch (e.PropertyName)
        {
            case nameof(HardwarePanelViewModel.Board):
            case nameof(HardwarePanelViewModel.Picture):
                Picture.SetBoard(_vm.Board, _vm.Picture);
                break;
            case nameof(HardwarePanelViewModel.Marks):
                Picture.Marks = _vm.Marks;
                break;
        }
    }

    /// <summary>treeViewLeft_DoubleClick: edit an instance.</summary>
    private void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_vm?.SelectedNode is HardwareInstanceNode && _vm.EditCommand.CanExecute(null)) _vm.EditCommand.Execute(null);
    }

    private void OnUsedSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm != null && UsedTree.SelectedItem is UsedHardwareNode node) _vm.SelectUsed(node);
    }

    /// <summary>treeViewRight_DoubleClick: define an undefined instance or edit a defined one.</summary>
    private async void OnUsedDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_vm != null && UsedTree.SelectedItem is UsedHardwareNode node) await _vm.ActivateUsedAsync(node);
    }
}
