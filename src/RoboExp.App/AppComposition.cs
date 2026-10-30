using RoboExp.App.Documents;
using RoboExp.App.Palette;
using RoboExp.App.ViewModels;
using RoboExp.App.Views;

namespace RoboExp.App;

/// <summary>
/// The single registration point for document kinds and view-model → view mappings.
/// Later slices add their kinds/views here, e.g.
/// <code>
/// DocumentKinds.Register(FlowDocumentViewModel.KindInfo);                     // ".rcu"
/// ViewLocator.Register&lt;FlowDocumentViewModel&gt;(_ => new FlowDocumentView(), reuseView: true);
/// ViewLocator.Register&lt;PropertyGridViewModel&gt;(_ => new PropertyGridView());   // content of PanelIds.Properties
/// </code>
/// </summary>
public static class AppComposition
{
    private static bool _registered;

    public static void Register()
    {
        if (_registered) return;
        _registered = true;

        Flow.FlowComposition.Register(); // first: ".rcu" (RCU File) leads the Open dialog filters like the original
        DocumentKinds.Register(CodeDocumentViewModel.KindInfo);
        ViewLocator.Register<CodeDocumentViewModel>(_ => new CodeDocumentView(), reuseView: true);

        ViewLocator.Register<PaletteViewModel>(_ => new PaletteView());
        ViewLocator.Register<OutputViewModel>(_ => new OutputView());
        ViewLocator.Register<TemplateCodeViewModel>(_ => new TemplateCodeView());
        Properties.PropertiesComposition.Register();
    }
}
