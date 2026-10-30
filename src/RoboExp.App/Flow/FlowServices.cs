using RoboExp.Core.Model.Graphics;

namespace RoboExp.App.Flow;

/// <summary>
/// Opens the property editor of one flowchart block (original PropertyFrmNew popup / PropertyFrm dock). Called by the
/// canvas on double-click, Enter, right-click on the single selected block and the context menu "Properties".
/// Returns true when the block was changed.
/// </summary>
public interface IBlockEditorService
{
    Task<bool> EditAsync(FlowDocumentViewModel doc, GraphicObject target);
}

/// <summary>Services the flow editor consumes from other slices; set during <see cref="AppComposition.Register"/>.</summary>
public static class FlowServices
{
    public static IBlockEditorService? BlockEditor { get; set; }

    /// <summary>
    /// Resolves a dropped "func:" name that is neither already used nor a system function (user library functions of
    /// UserInclude.xml): the callee's ProjectInfo with ProjectPath at its .rcu, so AddUsedProjectInfo copies it in.
    /// </summary>
    public static Func<FlowDocumentViewModel, string, RoboExp.Core.Model.ProjectInfo?>? CalleeResolver { get; set; }
}
