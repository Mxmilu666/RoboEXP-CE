using Avalonia;
using RoboExp.App.Services;

namespace RoboExp.Tests.App.Ui;

public class StartupWindowBoundsTests
{
    [Theory]
    [InlineData(0, 0, 1366, 728, 1.0)]
    [InlineData(0, 0, 1920, 1040, 1.5)]
    [InlineData(0, 0, 1920, 1040, 2.0)]
    [InlineData(0, 48, 1280, 672, 1.25)]
    [InlineData(-1920, -1080, 1920, 1040, 1.5)]
    [InlineData(1920, 0, 800, 560, 2.0)]
    public void Initial_window_and_menu_fit_the_work_area_in_physical_pixels(int x, int y, int width, int height, double scaling)
    {
        var area = new PixelRect(x, y, width, height);
        var insets = new Size(16, 39);
        var bounds = StartupWindowBounds.Fit(area, scaling, new Size(1280, 820), new Size(800, 500), insets);
        var outerWidth = (int)Math.Ceiling((bounds.Client.Width + insets.Width) * scaling);
        var outerHeight = (int)Math.Ceiling((bounds.Client.Height + insets.Height) * scaling);
        Assert.InRange(bounds.Position.X, area.X, area.Right - outerWidth);
        Assert.InRange(bounds.Position.Y, area.Y, area.Bottom - outerHeight);
        Assert.True(bounds.Client.Width >= bounds.Minimum.Width);
        Assert.True(bounds.Client.Height >= bounds.Minimum.Height);
        Assert.True(bounds.Minimum.Width <= 800);
        Assert.True(bounds.Minimum.Height <= 500);
    }

    [Fact]
    public void Large_desktop_keeps_preferred_client_size_and_centers_native_frame()
    {
        var bounds = StartupWindowBounds.Fit(new PixelRect(0, 40, 3840, 2080), 2,
            new Size(1280, 820), new Size(800, 500), new Size(16, 39));
        Assert.Equal(new Size(1280, 820), bounds.Client);
        Assert.Equal(new Size(800, 500), bounds.Minimum);
        Assert.Equal(new PixelPoint(624, 221), bounds.Position);
    }
}
