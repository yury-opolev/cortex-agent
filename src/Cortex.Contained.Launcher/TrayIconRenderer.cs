using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Cortex.Contained.Launcher;

/// <summary>
/// Renders the tray icon dynamically: brain-circuit shape (from embedded PNGs)
/// with a colored status dot overlay. Theme-aware (light/dark taskbar).
/// Dot positioned top-right, matching remote-terminal style.
/// </summary>
internal static class TrayIconRenderer
{
    private static readonly Color ConnectedColor = Color.FromRgb(0x40, 0xA0, 0x2B);
    private static readonly Color StartingColor = Color.FromRgb(0xDF, 0x8E, 0x1D);
    private static readonly Color DisconnectedColor = Color.FromRgb(0xD2, 0x0F, 0x39);
    private static readonly Color IdleColor = Color.FromRgb(0xBB, 0xBB, 0xBB);

    /// <summary>
    /// Renders the tray icon as PNG bytes for use with Avalonia's WindowIcon.
    /// </summary>
    public static byte[] RenderPng(int size, bool isLightTheme, CortexState state)
    {
        var baseIconPath = GetBaseIconPath(isLightTheme, size);
        var dotColor = GetDotColor(state);

        var dotRadius = size * 0.16;
        var gap = size * 0.08;
        var dotCenterX = size - dotRadius - gap;
        var dotCenterY = dotRadius + gap + (size * 0.04);

        // Pass 1: draw base icon, then erase the dot area to create transparent gap
        var iconLayer = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var ctx = iconLayer.CreateDrawingContext())
        {
            if (baseIconPath is not null && File.Exists(baseIconPath))
            {
                using var baseBitmap = new Bitmap(baseIconPath);
                ctx.DrawImage(baseBitmap, new Rect(0, 0, size, size));
            }
        }

        // Pass 2: copy icon pixels and punch transparent hole + draw dot
        var final = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var ctx = final.CreateDrawingContext())
        {
            // Draw icon with clip that excludes the dot+gap area
            var fullRect = new RectangleGeometry(new Rect(0, 0, size, size));
            var dotHole = new EllipseGeometry(new Rect(
                dotCenterX - dotRadius - gap,
                dotCenterY - dotRadius - gap,
                (dotRadius + gap) * 2,
                (dotRadius + gap) * 2));

            var clipGeometry = new CombinedGeometry(
                GeometryCombineMode.Exclude,
                fullRect,
                dotHole);

            using (ctx.PushGeometryClip(clipGeometry))
            {
                ctx.DrawImage(iconLayer, new Rect(0, 0, size, size));
            }

            // Draw the status dot
            var dotBrush = new SolidColorBrush(dotColor);
            ctx.DrawEllipse(
                dotBrush,
                null,
                new Point(dotCenterX, dotCenterY),
                dotRadius,
                dotRadius);
        }

        using var ms = new MemoryStream();
        final.Save(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Detects whether the Windows taskbar uses a light theme.
    /// </summary>
    public static bool IsSystemLightTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("SystemUsesLightTheme");
            return value is int i && i == 1;
        }
        catch
        {
            return false;
        }
    }

    private static string? GetBaseIconPath(bool isLight, int size)
    {
        var suffix = isLight ? "_altform-lightunplated" : "_altform-unplated";

        int[] targetSizes = [16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256];
        var closest = targetSizes.OrderBy(s => Math.Abs(s - size)).First();

        var fileName = $"Square44x44Logo.targetsize-{closest}{suffix}.png";
        var baseDir = AppContext.BaseDirectory;
        var path = Path.Combine(baseDir, "Assets", fileName);

        if (File.Exists(path))
        {
            return path;
        }

        var fallback = Path.Combine(baseDir, "Assets", $"Square44x44Logo.targetsize-{closest}.png");
        return File.Exists(fallback) ? fallback : null;
    }

    private static Color GetDotColor(CortexState state)
    {
        return state switch
        {
            CortexState.Running => ConnectedColor,
            CortexState.Starting or CortexState.Stopping => StartingColor,
            CortexState.Error => DisconnectedColor,
            _ => IdleColor,
        };
    }
}
