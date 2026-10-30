using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RoboExp.App.Localization;
using RoboExp.App.Icons;
using RoboExp.Core.Build;

namespace RoboExp.App.ViewModels;

public enum OutputSeverity { Info, Warning, Error, Success }

/// <summary>One row of the output list (original DockOutputBox ListView: Description | Line | File).</summary>
public sealed record OutputRow(string Message, int? Line, int? Column, string File, OutputSeverity Severity)
{
    public string LineText => Line is > 0 ? Line.Value.ToString() : "";
    public bool CanNavigate => File.Length > 0 && Line is > 0;

    public IImage? Icon => Severity switch
    {
        OutputSeverity.Warning => FlatIcons.Command("CompileWarning"),
        OutputSeverity.Error => FlatIcons.Command("CompileError"),
        OutputSeverity.Success => FlatIcons.Command("CompileSuccessful"),
        _ => null,
    };

    /// <summary>
    /// Classifies one output line with the build's own parser (<see cref="CompilerDiagnostic.TryParse"/>, which also
    /// accepts <see cref="Builder.Localize"/>d lines). Non-diagnostic lines become plain rows, like the original.
    /// </summary>
    public static OutputRow FromLine(string line, string successText)
    {
        if (line == successText) return new OutputRow(line, null, null, "", OutputSeverity.Success);
        var d = CompilerDiagnostic.TryParse(line);
        if (d == null) return new OutputRow(line, null, null, "", OutputSeverity.Info);
        return new OutputRow(d.Message, d.Line > 0 ? d.Line : null, d.Column, d.File,
            d.IsError ? OutputSeverity.Error : OutputSeverity.Warning);
    }
}

/// <summary>Output pane. Activating a located row raises <see cref="NavigateRequested"/> (original listView_DoubleClick).</summary>
public sealed partial class OutputViewModel : ObservableObject
{
    public ObservableCollection<OutputRow> Rows { get; } = [];

    [ObservableProperty]
    private OutputRow? _selectedRow;

    public event EventHandler<OutputRow>? NavigateRequested;

    /// <summary>
    /// Replaces the content with <paramref name="text"/>, one row per line (original DockOutputBox.SetOutput).
    /// The first error (else the success line) is selected so the list scrolls to it — GCC 15 builds can print many
    /// warning/context lines before it.
    /// </summary>
    public void SetOutput(string text)
    {
        Rows.Clear();
        var success = Localizer.Instance.T("CompilationSuccessful");
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Length == 0) continue;
            Rows.Add(OutputRow.FromLine(line, success));
        }
        SelectedRow = Rows.FirstOrDefault(r => r.Severity == OutputSeverity.Error)
                      ?? Rows.LastOrDefault(r => r.Severity == OutputSeverity.Success);
    }

    public void Activate(OutputRow? row)
    {
        if (row is { CanNavigate: true }) NavigateRequested?.Invoke(this, row);
    }

    [RelayCommand]
    private void Clear() => Rows.Clear();

    /// <summary>Text of the selected row, columns joined by spaces like the original menuItemCopy.</summary>
    public string? SelectedRowText => SelectedRow is { } r ? $"{r.Message} {r.LineText} {r.File} " : null;
}
