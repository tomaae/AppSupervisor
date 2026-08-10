using System.Drawing.Drawing2D;

namespace AppSupervisor;

/// <summary>
/// Creates tray-state icons by drawing small status overlays on the normal application icon.
/// </summary>
internal static class TrayIconFactory
{
    /// <summary>
    /// Creates an icon with an amber pause badge over the normal application icon.
    /// </summary>
    /// <param name="baseIcon">The normal application icon.</param>
    /// <returns>A standalone icon that can be disposed independently.</returns>
    public static Icon CreatePausedIcon(Icon baseIcon)
    {
        return CreateOverlayIcon(baseIcon, Color.FromArgb(230, 150, 20), (graphics, bounds) =>
        {
            float barWidth = bounds.Width * 0.16f;
            float gap = bounds.Width * 0.12f;
            float left = bounds.Left + bounds.Width / 2f - gap / 2f - barWidth;
            float top = bounds.Top + bounds.Height * 0.24f;
            float height = bounds.Height * 0.52f;

            using var brush = new SolidBrush(Color.White);
            graphics.FillRectangle(brush, left, top, barWidth, height);
            graphics.FillRectangle(brush, left + barWidth + gap, top, barWidth, height);
        });
    }

    /// <summary>
    /// Creates an icon with a red error badge over the normal application icon.
    /// </summary>
    /// <param name="baseIcon">The normal application icon.</param>
    /// <returns>A standalone icon that can be disposed independently.</returns>
    public static Icon CreateErrorIcon(Icon baseIcon)
    {
        return CreateOverlayIcon(baseIcon, Color.FromArgb(210, 35, 35), (graphics, bounds) =>
        {
            float inset = bounds.Width * 0.28f;
            float penWidth = Math.Max(1.5f, bounds.Width * 0.10f);

            using var pen = new Pen(Color.White, penWidth)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };

            graphics.DrawLine(
                pen,
                bounds.Left + inset,
                bounds.Top + inset,
                bounds.Right - inset,
                bounds.Bottom - inset
            );
            graphics.DrawLine(
                pen,
                bounds.Right - inset,
                bounds.Top + inset,
                bounds.Left + inset,
                bounds.Bottom - inset
            );
        });
    }

    /// <summary>
    /// Draws a colored circular badge and caller-supplied glyph on a copy of an icon.
    /// </summary>
    /// <param name="baseIcon">The icon to copy.</param>
    /// <param name="badgeColor">The badge background color.</param>
    /// <param name="drawGlyph">The function that draws the white badge glyph.</param>
    /// <returns>A standalone icon containing the requested overlay.</returns>
    private static Icon CreateOverlayIcon(
        Icon baseIcon,
        Color badgeColor,
        Action<Graphics, RectangleF> drawGlyph)
    {
        using Bitmap bitmap = baseIcon.ToBitmap();
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;

            float badgeSize = Math.Max(8f, Math.Min(bitmap.Width, bitmap.Height) * 0.55f);
            var badgeBounds = new RectangleF(
                bitmap.Width - badgeSize,
                bitmap.Height - badgeSize,
                badgeSize,
                badgeSize
            );

            using var badgeBrush = new SolidBrush(badgeColor);
            graphics.FillEllipse(badgeBrush, badgeBounds);
            drawGlyph(graphics, badgeBounds);
        }

        IntPtr iconHandle = bitmap.GetHicon();

        try
        {
            return (Icon)Icon.FromHandle(iconHandle).Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(iconHandle);
        }
    }
}
