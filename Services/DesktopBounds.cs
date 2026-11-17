using System;
using System.Drawing;
namespace MyFences.Services;
internal static class DesktopBounds
{
    internal static Rectangle Clamp(Rectangle desired, Rectangle area)
    {
        int width = Math.Clamp(desired.Width, 1, Math.Max(1, area.Width));
        int height = Math.Clamp(desired.Height, 1, Math.Max(1, area.Height));
        return new Rectangle(Math.Clamp(desired.X, area.Left, area.Right - width), Math.Clamp(desired.Y, area.Top, area.Bottom - height), width, height);
    }
}
