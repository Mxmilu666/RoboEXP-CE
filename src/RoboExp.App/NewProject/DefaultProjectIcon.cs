using RoboExp.App.Services;
using RoboExp.Core.Model;

namespace RoboExp.App.NewProject;

/// <summary>
/// The icon FileNewFrm gives every new project (its <c>picIcon.Image</c>, also ProjectInfoFrm's default): a 32×32 PNG
/// in the vendor's <c>RoboExp.FileNewFrm.resources</c>. Without it a new subroutine cannot be placed on a flowchart
/// (FlowView.AddFuncIcon requires a ProjectIcon).
/// </summary>
public static class DefaultProjectIcon
{
    public static EncodedImage? Get() =>
        FormResourceImages.GetBytes("FileNewFrm", "picIcon.Image") is { } png ? new EncodedImage(png) : null;
}
