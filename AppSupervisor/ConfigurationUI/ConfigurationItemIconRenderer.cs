using System.Drawing.Drawing2D;

namespace AppSupervisor.ConfigurationUI;

/// <summary>Draws compact configuration-item pictograms that remain legible at 20 pixels.</summary>
internal static class ConfigurationItemIconRenderer
{
    /// <summary>Draws a pictogram for one startup macro action type.</summary>
    internal static void DrawStartupMacro(
        Graphics graphics,
        Rectangle bounds,
        StartupMacroActionType? type,
        Color color)
    {
        SmoothingMode previousSmoothingMode = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        try
        {
            float strokeWidth = Math.Max(1.35f, bounds.Width / 12f);
            using var pen = new Pen(color, strokeWidth)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };

            switch (type)
            {
                case StartupMacroActionType.Delay:
                    DrawClock(graphics, pen, bounds);
                    break;
                case StartupMacroActionType.Hotkey:
                    DrawKeyboard(graphics, pen, bounds);
                    break;
                case StartupMacroActionType.MoveWindow:
                    DrawMove(graphics, pen, bounds);
                    break;
                case StartupMacroActionType.ResizeWindow:
                    DrawResize(graphics, pen, bounds);
                    break;
                case StartupMacroActionType.Minimize:
                    DrawMinimize(graphics, pen, bounds);
                    break;
                case StartupMacroActionType.Maximize:
                    DrawMaximize(graphics, pen, bounds);
                    break;
                case StartupMacroActionType.Restore:
                    DrawRestore(graphics, pen, bounds);
                    break;
                case StartupMacroActionType.BringToFront:
                    DrawBringToFront(graphics, pen, bounds);
                    break;
                default:
                    DrawQuestionMark(graphics, pen, bounds);
                    break;
            }
        }
        finally
        {
            graphics.SmoothingMode = previousSmoothingMode;
        }
    }

    private static void DrawClock(Graphics graphics, Pen pen, Rectangle bounds)
    {
        RectangleF clock = RelativeRectangle(bounds, 0.12f, 0.12f, 0.76f, 0.76f);
        graphics.DrawEllipse(pen, clock);
        PointF center = RelativePoint(bounds, 0.5f, 0.5f);
        graphics.DrawLine(pen, center, RelativePoint(bounds, 0.5f, 0.28f));
        graphics.DrawLine(pen, center, RelativePoint(bounds, 0.7f, 0.5f));
    }

    private static void DrawKeyboard(Graphics graphics, Pen pen, Rectangle bounds)
    {
        RectangleF keyboard = RelativeRectangle(bounds, 0.07f, 0.22f, 0.86f, 0.6f);
        graphics.DrawRectangle(pen, keyboard.X, keyboard.Y, keyboard.Width, keyboard.Height);
        graphics.DrawLine(
            pen,
            RelativePoint(bounds, 0.2f, 0.42f),
            RelativePoint(bounds, 0.8f, 0.42f)
        );
        graphics.DrawLine(
            pen,
            RelativePoint(bounds, 0.26f, 0.62f),
            RelativePoint(bounds, 0.74f, 0.62f)
        );
    }

    private static void DrawMove(Graphics graphics, Pen pen, Rectangle bounds)
    {
        PointF center = RelativePoint(bounds, 0.5f, 0.5f);
        DrawArrow(graphics, pen, center, RelativePoint(bounds, 0.1f, 0.5f), 0.11f * bounds.Width);
        DrawArrow(graphics, pen, center, RelativePoint(bounds, 0.9f, 0.5f), 0.11f * bounds.Width);
        DrawArrow(graphics, pen, center, RelativePoint(bounds, 0.5f, 0.1f), 0.11f * bounds.Width);
        DrawArrow(graphics, pen, center, RelativePoint(bounds, 0.5f, 0.9f), 0.11f * bounds.Width);
    }

    private static void DrawResize(Graphics graphics, Pen pen, Rectangle bounds)
    {
        graphics.DrawRectangle(
            pen,
            RelativeRectangle(bounds, 0.12f, 0.12f, 0.76f, 0.76f)
        );
        PointF start = RelativePoint(bounds, 0.28f, 0.72f);
        PointF end = RelativePoint(bounds, 0.72f, 0.28f);
        DrawArrow(graphics, pen, start, end, 0.11f * bounds.Width);
        DrawArrow(graphics, pen, end, start, 0.11f * bounds.Width);
    }

    private static void DrawMinimize(Graphics graphics, Pen pen, Rectangle bounds)
    {
        graphics.DrawRectangle(
            pen,
            RelativeRectangle(bounds, 0.12f, 0.12f, 0.76f, 0.5f)
        );
        DrawArrow(
            graphics,
            pen,
            RelativePoint(bounds, 0.5f, 0.4f),
            RelativePoint(bounds, 0.5f, 0.78f),
            0.11f * bounds.Width
        );
        graphics.DrawLine(
            pen,
            RelativePoint(bounds, 0.27f, 0.88f),
            RelativePoint(bounds, 0.73f, 0.88f)
        );
    }

    private static void DrawMaximize(Graphics graphics, Pen pen, Rectangle bounds)
    {
        RectangleF window = RelativeRectangle(bounds, 0.12f, 0.12f, 0.76f, 0.76f);
        graphics.DrawRectangle(pen, window.X, window.Y, window.Width, window.Height);
        graphics.DrawLine(
            pen,
            RelativePoint(bounds, 0.12f, 0.32f),
            RelativePoint(bounds, 0.88f, 0.32f)
        );
    }

    private static void DrawRestore(Graphics graphics, Pen pen, Rectangle bounds)
    {
        RectangleF rear = RelativeRectangle(bounds, 0.3f, 0.12f, 0.58f, 0.58f);
        RectangleF front = RelativeRectangle(bounds, 0.12f, 0.3f, 0.58f, 0.58f);
        graphics.DrawRectangle(pen, rear.X, rear.Y, rear.Width, rear.Height);
        graphics.DrawRectangle(pen, front.X, front.Y, front.Width, front.Height);
    }

    private static void DrawBringToFront(Graphics graphics, Pen pen, Rectangle bounds)
    {
        RectangleF rear = RelativeRectangle(bounds, 0.34f, 0.1f, 0.56f, 0.56f);
        RectangleF middle = RelativeRectangle(bounds, 0.22f, 0.22f, 0.56f, 0.56f);
        RectangleF front = RelativeRectangle(bounds, 0.1f, 0.34f, 0.56f, 0.56f);
        graphics.DrawRectangle(pen, rear.X, rear.Y, rear.Width, rear.Height);
        graphics.DrawRectangle(pen, middle.X, middle.Y, middle.Width, middle.Height);
        using var brush = new SolidBrush(pen.Color);
        graphics.FillRectangle(brush, front);
    }

    private static void DrawQuestionMark(Graphics graphics, Pen pen, Rectangle bounds)
    {
        graphics.DrawArc(
            pen,
            RelativeRectangle(bounds, 0.28f, 0.1f, 0.44f, 0.48f),
            190,
            260
        );
        graphics.DrawLine(
            pen,
            RelativePoint(bounds, 0.5f, 0.52f),
            RelativePoint(bounds, 0.5f, 0.67f)
        );
        graphics.DrawEllipse(pen, RelativeRectangle(bounds, 0.47f, 0.83f, 0.06f, 0.06f));
    }

    private static void DrawArrow(
        Graphics graphics,
        Pen pen,
        PointF start,
        PointF end,
        float headSize)
    {
        graphics.DrawLine(pen, start, end);
        float angle = MathF.Atan2(end.Y - start.Y, end.X - start.X);
        const float spread = MathF.PI / 4f;
        graphics.DrawLine(
            pen,
            end,
            new PointF(
                end.X - headSize * MathF.Cos(angle - spread),
                end.Y - headSize * MathF.Sin(angle - spread)
            )
        );
        graphics.DrawLine(
            pen,
            end,
            new PointF(
                end.X - headSize * MathF.Cos(angle + spread),
                end.Y - headSize * MathF.Sin(angle + spread)
            )
        );
    }

    private static PointF RelativePoint(Rectangle bounds, float x, float y) =>
        new(bounds.Left + bounds.Width * x, bounds.Top + bounds.Height * y);

    private static RectangleF RelativeRectangle(
        Rectangle bounds,
        float x,
        float y,
        float width,
        float height) =>
        new(
            bounds.Left + bounds.Width * x,
            bounds.Top + bounds.Height * y,
            bounds.Width * width,
            bounds.Height * height
        );
}
