namespace RoboExp.App.Hardware;

/// <summary>Registers the hardware views with <see cref="ViewLocator"/> (called once at startup).</summary>
public static class HardwareComposition
{
    public static void RegisterViews() =>
        ViewLocator.Register<HardwarePanelViewModel>(_ => new HardwarePanelView(), reuseView: true);
}
