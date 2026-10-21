namespace KalTerm.Terminal.Layouts;

/// <summary>The main window as the layout sees it, implemented by the window itself.</summary>
internal interface IWindowPlacement
{
    /// <summary>Where the window is now.</summary>
    /// <returns>Its place.</returns>
    WindowLayout Capture();

    /// <summary>Puts the window where a layout says. A place the screens no longer show is moved so the window is visible.</summary>
    /// <param name="layout">The place.</param>
    void Apply(WindowLayout layout);
}

/// <summary>The arithmetic of putting a saved window on the screens there are now.</summary>
internal static class WindowPlacement
{
    /// <summary>The window can never be made smaller than this (the window's own minimum).</summary>
    public const double MinWidth = 720;

    /// <summary>The window can never be made smaller than this (the window's own minimum).</summary>
    public const double MinHeight = 480;

    /// <summary>
    /// Fits a saved place onto the screens: no larger than the screens, and moved so that at least a corner big enough to grab (the title bar) is on a screen. Layouts are saved on one computer and read on another, and a
    /// monitor may be gone.
    /// </summary>
    /// <param name="saved">The saved place.</param>
    /// <param name="screenLeft">The left of the area all screens cover.</param>
    /// <param name="screenTop">The top of that area.</param>
    /// <param name="screenWidth">Its width.</param>
    /// <param name="screenHeight">Its height.</param>
    /// <returns>A place that is visible.</returns>
    public static WindowLayout Fit(WindowLayout saved, double screenLeft, double screenTop, double screenWidth, double screenHeight)
    {
        ArgumentNullException.ThrowIfNull(saved);
        var width = Math.Clamp(double.IsFinite(saved.Width) ? saved.Width : MinWidth, MinWidth, Math.Max(MinWidth, screenWidth));
        var height = Math.Clamp(double.IsFinite(saved.Height) ? saved.Height : MinHeight, MinHeight, Math.Max(MinHeight, screenHeight));
        var left = double.IsFinite(saved.Left) ? saved.Left : screenLeft;
        var top = double.IsFinite(saved.Top) ? saved.Top : screenTop;
        left = Math.Clamp(left, screenLeft - width + 100, screenLeft + screenWidth - 100);
        top = Math.Clamp(top, screenTop, screenTop + screenHeight - 40);
        return new WindowLayout(left, top, width, height, saved.Maximized);
    }
}
