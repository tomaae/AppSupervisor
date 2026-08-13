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
    /// Creates an icon with a green play badge over the normal application icon.
    /// </summary>
    /// <param name="baseIcon">The normal application icon.</param>
    /// <returns>A standalone icon that can be disposed independently.</returns>
    public static Icon CreateSupervisingIcon(Icon baseIcon)
    {
        return CreateOverlayIcon(baseIcon, Color.FromArgb(35, 165, 70), (graphics, bounds) =>
        {
            float left = bounds.Left + bounds.Width * 0.34f;
            float right = bounds.Left + bounds.Width * 0.72f;
            float top = bounds.Top + bounds.Height * 0.25f;
            float bottom = bounds.Top + bounds.Height * 0.75f;

            using var brush = new SolidBrush(Color.White);
            graphics.FillPolygon(brush,
            [
                new PointF(left, top),
                new PointF(right, bounds.Top + bounds.Height / 2f),
                new PointF(left, bottom)
            ]);
        });
    }

    /// <summary>
    /// Creates an icon with a red X badge over the normal application icon.
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
    /// Creates an icon with a blue clock badge in the top-left corner while preserving any
    /// existing bottom-right state badge.
    /// </summary>
    /// <param name="baseIcon">The normal or already state-badged application icon.</param>
    /// <returns>A standalone icon that can be disposed independently.</returns>
    public static Icon CreateStartingIcon(Icon baseIcon)
    {
        return CreateOverlayIcon(
            baseIcon,
            Color.FromArgb(35, 125, 210),
            (graphics, bounds) =>
            {
                float inset = bounds.Width * 0.22f;
                float penWidth = Math.Max(1.2f, bounds.Width * 0.09f);
                float centerX = bounds.Left + bounds.Width / 2f;
                float centerY = bounds.Top + bounds.Height / 2f;

                using var pen = new Pen(Color.White, penWidth)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                };

                graphics.DrawEllipse(
                    pen,
                    bounds.Left + inset,
                    bounds.Top + inset,
                    bounds.Width - inset * 2f,
                    bounds.Height - inset * 2f
                );
                graphics.DrawLine(
                    pen,
                    centerX,
                    centerY,
                    centerX,
                    bounds.Top + bounds.Height * 0.31f
                );
                graphics.DrawLine(
                    pen,
                    centerX,
                    centerY,
                    bounds.Left + bounds.Width * 0.66f,
                    bounds.Top + bounds.Height * 0.60f
                );
            },
            topLeft: true
        );
    }

    /// <summary>
    /// Creates an icon with an orange stop badge in the top-left corner while preserving any
    /// existing bottom-right state badge.
    /// </summary>
    /// <param name="baseIcon">The normal or already state-badged application icon.</param>
    /// <returns>A standalone icon that can be disposed independently.</returns>
    public static Icon CreateStoppingIcon(Icon baseIcon)
    {
        return CreateOverlayIcon(
            baseIcon,
            Color.FromArgb(220, 105, 25),
            (graphics, bounds) =>
            {
                float inset = bounds.Width * 0.30f;

                using var brush = new SolidBrush(Color.White);
                graphics.FillRectangle(
                    brush,
                    bounds.Left + inset,
                    bounds.Top + inset,
                    bounds.Width - inset * 2f,
                    bounds.Height - inset * 2f
                );
            },
            topLeft: true
        );
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
        Action<Graphics, RectangleF> drawGlyph,
        bool topLeft = false)
    {
        using Bitmap bitmap = baseIcon.ToBitmap();
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;

            float badgeSize = Math.Max(8f, Math.Min(bitmap.Width, bitmap.Height) * 0.55f);
            var badgeBounds = new RectangleF(
                topLeft ? 0 : bitmap.Width - badgeSize,
                topLeft ? 0 : bitmap.Height - badgeSize,
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
