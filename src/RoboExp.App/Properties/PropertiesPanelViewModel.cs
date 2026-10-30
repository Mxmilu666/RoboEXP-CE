using CommunityToolkit.Mvvm.ComponentModel;
using RoboExp.App.Flow;
using RoboExp.Core.Model.Graphics;

namespace RoboExp.App.Properties;

/// <summary>
/// Content of the docked Properties panel (original PropertyFrm fed by FlowView.UpdateGOBJ): follows the active flow
/// document's single selection and reloads after undo/redo/model changes.
/// </summary>
public sealed partial class PropertiesPanelViewModel : ObservableObject
{
    private readonly IBlockEditorHost _host;
    private FlowDocumentViewModel? _doc;

    public PropertiesPanelViewModel(IBlockEditorHost host) => _host = host;

    [ObservableProperty] private BlockPropertiesViewModel? _editor;

    public bool IsEmpty => Editor == null;

    partial void OnEditorChanged(BlockPropertiesViewModel? value) => OnPropertyChanged(nameof(IsEmpty));

    public void SetDocument(FlowDocumentViewModel? doc)
    {
        if (_doc == doc) return;
        if (_doc != null)
        {
            _doc.SelectionChanged -= OnSelectionChanged;
            _doc.ModelChanged -= OnModelChanged;
        }
        _doc = doc;
        if (doc != null)
        {
            doc.SelectionChanged += OnSelectionChanged;
            doc.ModelChanged += OnModelChanged;
        }
        Rebuild();
    }

    /// <summary>Shows <paramref name="target"/> regardless of the selection (double-click on X3/X4).</summary>
    public void Show(FlowDocumentViewModel doc, GraphicObject target)
    {
        SetDocument(doc);
        Editor = new BlockPropertiesViewModel(doc, target, docked: true, _host);
    }

    private void OnSelectionChanged(object? sender, EventArgs e) => Rebuild();

    private void OnModelChanged(object? sender, EventArgs e)
    {
        if (Editor == null) return;
        if (_doc?.Session.File.FlowData?.DesignSurfaceData?.DrawingObjects?.List?.Contains(Editor.Target) == true) Editor.Reload();
        else Rebuild();
    }

    private void Rebuild()
    {
        var sel = _doc?.Selection;
        var target = sel is { Count: 1 } ? sel[0] : null;
        if (target == null || BlockPropertiesViewModel.KindOf(target) == BlockEditorKind.None) { Editor = null; return; }
        if (Editor?.Target == target && Editor.Doc == _doc) { Editor.Reload(); return; }
        Editor = new BlockPropertiesViewModel(_doc!, target, docked: true, _host);
    }
}
