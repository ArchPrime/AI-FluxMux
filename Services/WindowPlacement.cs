using System;
using System.Collections.Generic;

namespace FluxMux.Avalonia.Services;

public readonly record struct ScreenBox(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;
}

public static class WindowPlacement
{
    public static bool HasUsableOnScreenArea(ScreenBox window, IReadOnlyList<ScreenBox> workingAreas)
    {
        if (workingAreas.Count == 0)
        {
            return true;
        }

        const int minVisibleWidth = 120;
        const int minVisibleHeight = 48;
        foreach (var area in workingAreas)
        {
            var left = Math.Max(window.X, area.X);
            var top = Math.Max(window.Y, area.Y);
            var right = Math.Min(window.Right, area.Right);
            var bottom = Math.Min(window.Bottom, area.Bottom);
            if (right - left >= minVisibleWidth && bottom - top >= minVisibleHeight)
            {
                return true;
            }
        }

        return false;
    }
}