using System;
namespace RynthCore.Engine.WebPanel;

internal readonly record struct PanelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

/// <summary>Panel placement as fractions (0..1) of the game client rect.</summary>
internal readonly record struct PanelFractions(double X, double Y, double W, double H);

/// <summary>Pure visibility and layout rules for the web panel window. No Win32.</summary>
internal static class WebPanelWindowPolicy
{
    public const int DefaultPercent = 88, MinPercent = 30, MaxPercent = 100, MinWidthPx = 480, MinHeightPx = 320;

    /// <summary>
    /// false when closed, the game is gone, iconic or invisible; null (keep current) when the
    /// foreground window is null mid alt-tab, so the panel never flickers; otherwise true only
    /// when the foreground window belongs to the game process.
    /// </summary>
    public static bool? Visible(bool open, bool gameAlive, bool gameIconic, bool gameVisible,
        bool foregroundNull, uint foregroundPid, uint ourPid)
    {
        if (!open || !gameAlive || gameIconic || !gameVisible) return false;
        if (foregroundNull) return null;
        return foregroundPid == ourPid;
    }

    /// <summary>Centered fractions for a size in percent of the client; 0 means the default (88).</summary>
    public static PanelFractions Default(int widthPercent, int heightPercent)
    {
        double w = Pct(widthPercent) / 100.0;
        double h = Pct(heightPercent) / 100.0;
        return new PanelFractions((1 - w) / 2, (1 - h) / 2, w, h);
    }

    private static int Pct(int p) => p == 0 ? DefaultPercent : Math.Clamp(p, MinPercent, MaxPercent);

    /// <summary>Screen rect for the fractions: at least 480x320 (or the whole client if smaller), never outside the client.</summary>
    public static PanelRect Place(PanelRect gameClientScreen, PanelFractions f)
    {
        int cw = Math.Max(0, gameClientScreen.Width);
        int ch = Math.Max(0, gameClientScreen.Height);
        int w = Math.Clamp((int)Math.Round(Clamp01(f.W) * cw), Math.Min(MinWidthPx, cw), cw);
        int h = Math.Clamp((int)Math.Round(Clamp01(f.H) * ch), Math.Min(MinHeightPx, ch), ch);
        int x = Math.Clamp((int)Math.Round(Clamp01(f.X) * cw), 0, cw - w);
        int y = Math.Clamp((int)Math.Round(Clamp01(f.Y) * ch), 0, ch - h);
        int left = gameClientScreen.Left + x;
        int top = gameClientScreen.Top + y;
        return new PanelRect(left, top, left + w, top + h);
    }

    /// <summary>Fractions of the client for a panel rect the user dragged or resized, clamped to 0..1.</summary>
    public static PanelFractions FromUserRect(PanelRect gameClientScreen, PanelRect panelScreen)
    {
        int cw = gameClientScreen.Width, ch = gameClientScreen.Height;
        if (cw <= 0 || ch <= 0) return Default(0, 0);
        double w = Clamp01((double)panelScreen.Width / cw);
        double h = Clamp01((double)panelScreen.Height / ch);
        double x = Math.Clamp((double)(panelScreen.Left - gameClientScreen.Left) / cw, 0, 1 - w);
        double y = Math.Clamp((double)(panelScreen.Top - gameClientScreen.Top) / ch, 0, 1 - h);
        return new PanelFractions(x, y, w, h);
    }

    private static double Clamp01(double v) => double.IsNaN(v) ? 0 : Math.Clamp(v, 0, 1);
}
