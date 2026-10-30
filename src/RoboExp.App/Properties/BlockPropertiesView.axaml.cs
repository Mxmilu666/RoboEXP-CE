using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;

namespace RoboExp.App.Properties;

public partial class BlockPropertiesView : UserControl
{
    private BlockPropertiesViewModel? _vm;

    public BlockPropertiesView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) => Attach(DataContext as BlockPropertiesViewModel);
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.Source is not Control source) return;
            var row = source.DataContext as ParameterRowViewModel
                ?? source.GetVisualAncestors().OfType<Control>().Select(c => c.DataContext).OfType<ParameterRowViewModel>().FirstOrDefault();
            if (row != null) _vm?.ShowParameterHelp(row);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private TextBox Expr => this.FindControl<TextBox>("TxtExpression")!;

    private void Attach(BlockPropertiesViewModel? vm)
    {
        if (_vm != null)
        {
            _vm.InsertRequested -= OnInsertRequested;
            _vm.SelectRequested -= OnSelectRequested;
        }
        _vm = vm;
        if (vm == null) return;
        vm.InsertRequested += OnInsertRequested;
        vm.SelectRequested += OnSelectRequested;
    }

    /// <summary>Commits pending free-text fields (called before a popup closes).</summary>
    public void CommitPending()
    {
        if (_vm == null) return;
        _vm.CommitExpression();
        _vm.CommitHint();
        _vm.CommitCodes();
        _vm.CommitText();
        CommitLoop();
    }

    /// <summary>Original AddSign: replace the selection with the sign, caret after it.</summary>
    private void OnInsertRequested(object? sender, string sign)
    {
        var box = Expr;
        var text = box.Text ?? "";
        var start = Math.Min(box.SelectionStart, box.SelectionEnd);
        var end = Math.Max(box.SelectionStart, box.SelectionEnd);
        start = Math.Clamp(start, 0, text.Length);
        end = Math.Clamp(end, start, text.Length);
        if (!box.IsFocused && start == 0 && end == 0) start = end = box.CaretIndex;
        box.Text = text.Remove(start, end - start).Insert(start, sign);
        box.CaretIndex = start + sign.Length;
        box.SelectionStart = box.SelectionEnd = box.CaretIndex;
        box.Focus();
        _vm?.CommitExpression();
    }

    private void OnSelectRequested(object? sender, (int Start, int Length) r)
    {
        var box = Expr;
        box.Focus();
        box.SelectionStart = r.Start;
        box.SelectionEnd = r.Start + r.Length;
    }

    private void OnSignClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SignButton s }) _vm?.InsertSignCommand.Execute(s.Sign);
    }

    private async void OnVariablePicked(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox cb || cb.SelectedItem is not string item || _vm == null) return;
        cb.SelectedItem = null;
        await _vm.PickVariableAsync(item);
    }

    private void OnExpressionLostFocus(object? sender, RoutedEventArgs e) => _vm?.CommitExpression();

    private void OnExpressionKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _vm == null) return;
        e.Handled = true;
        _vm.CheckExpressionCommand.Execute(null);
    }

    private void OnParameterHelpPointerPressed(object? sender, PointerPressedEventArgs e) => ShowParameterHelp(sender);

    private void OnParameterHelpGotFocus(object? sender, FocusChangedEventArgs e) => ShowParameterHelp(sender);

    private void ShowParameterHelp(object? sender)
    {
        if (sender is Control { DataContext: ParameterRowViewModel row }) _vm?.ShowParameterHelp(row);
    }
    private async void OnParameterLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ParameterRowViewModel row }) await row.CommitAsync();
    }

    private async void OnParameterKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is Control { DataContext: ParameterRowViewModel row }) await row.CommitAsync();
    }

    /// <summary>A list pick in an editable row applies at once (PropertyGrid drop-down selection).</summary>
    private async void OnParameterDropDownClosed(object? sender, EventArgs e)
    {
        if (sender is Control { DataContext: ParameterRowViewModel row }) await row.CommitAsync();
    }

    private void CommitLoop()
    {
        if (_vm == null || !_vm.IsFor) return;
        var box = this.FindControl<ComboBox>("LoopBox")!;
        _vm.LoopCount = box.Text ?? "";
    }

    private void OnLoopDropDownClosed(object? sender, EventArgs e) => CommitLoop();

    private void OnLoopLostFocus(object? sender, RoutedEventArgs e) => CommitLoop();

    private void OnLoopKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) CommitLoop();
    }

    private void OnCodesLostFocus(object? sender, RoutedEventArgs e) => _vm?.CommitCodes();
    private void OnHintLostFocus(object? sender, RoutedEventArgs e) => _vm?.CommitHint();
    private void OnTextLostFocus(object? sender, RoutedEventArgs e) => _vm?.CommitText();
}
