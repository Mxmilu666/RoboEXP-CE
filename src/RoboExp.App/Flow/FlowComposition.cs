using CommunityToolkit.Mvvm.Input;
using RoboExp.App.Documents;
using RoboExp.App.Flow.Editing;
using RoboExp.App.ViewModels;

namespace RoboExp.App.Flow;

/// <summary>
/// Registration of the flow editor slice: the .rcu document kind (flow projects and code projects), their views, the
/// Code window content and the flowchart toolbar commands (MainForm tool/align buttons, keys <c>Flow.*</c> in
/// <see cref="MainWindowViewModel.FeatureCommands"/>).
/// </summary>
public static class FlowComposition
{
    public static void Register()
    {
        DocumentKinds.Register(FlowDocumentViewModel.KindInfo);
        ViewLocator.Register<FlowDocumentViewModel>(_ => new FlowDocumentView(), reuseView: true);
        ViewLocator.Register<CodeProjectDocumentViewModel>(_ => new CodeProjectDocumentView(), reuseView: true);
        ViewLocator.Register<FlowCodePanelViewModel>(_ => new FlowCodePanelView());
        MainWindowViewModel.Created += Attach;
    }

    private static void Attach(MainWindowViewModel main)
    {
        var code = new FlowCodePanelViewModel();
        main.Panel(PanelIds.Code).Content = code;

        var commands = new List<IRelayCommand>();
        void Add(string key, Action<FlowDocumentViewModel> run)
        {
            var command = new RelayCommand(
                () => { if (main.ActiveDocument is FlowDocumentViewModel f) run(f); },
                () => main.ActiveDocument is FlowDocumentViewModel);
            main.FeatureCommands["Flow." + key] = command;
            commands.Add(command);
        }
        Add("AutoTool", f => f.Tool = FlowTool.AutoTool);
        Add("Scroll", f => f.Tool = FlowTool.Scroll);
        Add("Select", f => f.Tool = FlowTool.Select);
        Add("Connect", f => f.Tool = FlowTool.Connect);
        Add("Text", f => f.Tool = FlowTool.Text);
        Add("AlignLeft", f => f.Align(FlowEditor.Align.Left));
        Add("AlignRight", f => f.Align(FlowEditor.Align.Right));
        Add("AlignTop", f => f.Align(FlowEditor.Align.Top));
        Add("AlignBottom", f => f.Align(FlowEditor.Align.Bottom));
        Add("AutoAlign", f => f.AutoAlign());

        main.ActiveDocumentChanged += (_, doc) =>
        {
            code.SetActiveDocument(doc);
            foreach (var c in commands) c.NotifyCanExecuteChanged();
        };
        main.Documents.CollectionChanged += (_, e) =>
        {
            foreach (var old in e.OldItems?.OfType<DocumentViewModel>() ?? []) code.Forget(old);
        };
    }
}
