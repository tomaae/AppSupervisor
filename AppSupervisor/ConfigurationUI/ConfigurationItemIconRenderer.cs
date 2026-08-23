using System.Drawing.Drawing2D;
using AppSupervisor.Core;

namespace AppSupervisor.ConfigurationUI;

/// <summary>Draws compact configuration-item pictograms that remain legible at 20 pixels.</summary>
internal static class ConfigurationItemIconRenderer
{
    internal static readonly Color StartingColor = Color.FromArgb(22, 137, 216);
    internal static readonly Color RunningColor = Color.FromArgb(21, 148, 71);
    internal static readonly Color StoppingColor = Color.FromArgb(217, 119, 6);
    internal static readonly Color InactiveColor = Color.FromArgb(117, 117, 117);
    internal static readonly Color UnknownColor = Color.FromArgb(138, 138, 138);

    /// <summary>Draws the approved second-icon runtime status pictogram.</summary>
    internal static void DrawRuntimeStatus(
        Graphics graphics,
        Rectangle bounds,
        ConfigurationResourceRuntimeStatus status,
        Color selectionColor,
        bool selected)
    {
        SmoothingMode previousSmoothingMode = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        try
        {
            Color color = selected
                ? selectionColor
                : status switch
                {
                    ConfigurationResourceRuntimeStatus.Starting => StartingColor,
                    ConfigurationResourceRuntimeStatus.Running => RunningColor,
                    ConfigurationResourceRuntimeStatus.Stopping => StoppingColor,
                    ConfigurationResourceRuntimeStatus.NotRunning => InactiveColor,
                    _ => UnknownColor
                };
            float strokeWidth = Math.Max(1.4f, bounds.Width / 10f);
            using var pen = new Pen(color, strokeWidth)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };

            switch (status)
            {
                case ConfigurationResourceRuntimeStatus.Starting:
                    DrawStartingStatus(graphics, pen, bounds);
                    break;
                case ConfigurationResourceRuntimeStatus.Running:
                    DrawRunningStatus(graphics, pen, bounds, selected);
                    break;
                case ConfigurationResourceRuntimeStatus.Stopping:
                    DrawStoppingStatus(graphics, pen, bounds);
                    break;
                case ConfigurationResourceRuntimeStatus.NotRunning:
                    DrawNotRunningStatus(graphics, pen, bounds, selected);
                    break;
                default:
                    DrawUnknownStatus(graphics, pen, bounds);
                    break;
            }
        }
        finally
        {
            graphics.SmoothingMode = previousSmoothingMode;
        }
    }

    /// <summary>Draws a speaker for output endpoints and a microphone for input endpoints.</summary>
    internal static void DrawAudio(
        Graphics graphics,
        Rectangle bounds,
        AudioInterfaceDirection direction,
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

            if (direction == AudioInterfaceDirection.Input)
                DrawMicrophone(graphics, pen, bounds);
            else
                DrawSpeaker(graphics, pen, bounds);
        }
        finally
        {
            graphics.SmoothingMode = previousSmoothingMode;
        }
    }

    /// <summary>Draws a network-listener or OSCQuery pictogram for one health-check type.</summary>
    internal static void DrawHealthCheck(
        Graphics graphics,
        Rectangle bounds,
        HealthCheckType? type,
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

            if (type == HealthCheckType.Listener)
                DrawListener(graphics, pen, bounds);
            else if (type == HealthCheckType.Vrcosc)
                DrawOscQuery(graphics, pen, bounds);
            else
                DrawQuestionMark(graphics, pen, bounds);
        }
        finally
        {
            graphics.SmoothingMode = previousSmoothingMode;
        }
    }

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

    private static void DrawStartingStatus(Graphics graphics, Pen pen, Rectangle bounds)
    {
        RectangleF ring = RelativeRectangle(bounds, 0.14f, 0.14f, 0.72f, 0.72f);
        graphics.DrawArc(pen, ring, 38, 292);
        graphics.DrawLines(pen,
        [
            RelativePoint(bounds, 0.64f, 0.15f),
            RelativePoint(bounds, 0.83f, 0.23f),
            RelativePoint(bounds, 0.76f, 0.42f)
        ]);
        using var brush = new SolidBrush(pen.Color);
        graphics.FillPolygon(brush,
        [
            RelativePoint(bounds, 0.41f, 0.34f),
            RelativePoint(bounds, 0.68f, 0.5f),
            RelativePoint(bounds, 0.41f, 0.66f)
        ]);
    }

    private static void DrawRunningStatus(
        Graphics graphics,
        Pen pen,
        Rectangle bounds,
        bool selected)
    {
        RectangleF circle = RelativeRectangle(bounds, 0.1f, 0.1f, 0.8f, 0.8f);

        if (selected)
        {
            graphics.DrawEllipse(pen, circle);
            graphics.DrawLines(pen,
            [
                RelativePoint(bounds, 0.29f, 0.51f),
                RelativePoint(bounds, 0.44f, 0.66f),
                RelativePoint(bounds, 0.73f, 0.35f)
            ]);
            return;
        }

        using var brush = new SolidBrush(pen.Color);
        graphics.FillEllipse(brush, circle);
        using var checkPen = new Pen(Color.White, Math.Max(1.4f, bounds.Width / 10f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        graphics.DrawLines(checkPen,
        [
            RelativePoint(bounds, 0.29f, 0.51f),
            RelativePoint(bounds, 0.44f, 0.66f),
            RelativePoint(bounds, 0.73f, 0.35f)
        ]);
    }

    private static void DrawStoppingStatus(Graphics graphics, Pen pen, Rectangle bounds)
    {
        RectangleF ring = RelativeRectangle(bounds, 0.14f, 0.14f, 0.72f, 0.72f);
        graphics.DrawArc(pen, ring, 210, 292);
        graphics.DrawLines(pen,
        [
            RelativePoint(bounds, 0.36f, 0.15f),
            RelativePoint(bounds, 0.17f, 0.23f),
            RelativePoint(bounds, 0.24f, 0.42f)
        ]);
        using var brush = new SolidBrush(pen.Color);
        graphics.FillRectangle(brush, RelativeRectangle(bounds, 0.37f, 0.37f, 0.26f, 0.26f));
    }

    private static void DrawNotRunningStatus(
        Graphics graphics,
        Pen pen,
        Rectangle bounds,
        bool selected)
    {
        RectangleF outer = RelativeRectangle(bounds, 0.15f, 0.15f, 0.7f, 0.7f);
        using GraphicsPath path = CreateRoundedRectangle(outer, bounds.Width * 0.14f);
        using var brush = new SolidBrush(pen.Color);
        graphics.FillPath(brush, path);
        Color cutoutColor = selected ? SystemColors.Highlight : SystemColors.Window;
        using var cutoutBrush = new SolidBrush(cutoutColor);
        graphics.FillRectangle(
            cutoutBrush,
            RelativeRectangle(bounds, 0.35f, 0.35f, 0.3f, 0.3f)
        );
    }

    private static void DrawUnknownStatus(Graphics graphics, Pen pen, Rectangle bounds)
    {
        graphics.DrawLine(
            pen,
            RelativePoint(bounds, 0.22f, 0.22f),
            RelativePoint(bounds, 0.78f, 0.78f)
        );
        graphics.DrawLine(
            pen,
            RelativePoint(bounds, 0.78f, 0.22f),
            RelativePoint(bounds, 0.22f, 0.78f)
        );
    }

    private static GraphicsPath CreateRoundedRectangle(RectangleF bounds, float radius)
    {
        float diameter = Math.Min(radius * 2f, Math.Min(bounds.Width, bounds.Height));
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(
            bounds.Right - diameter,
            bounds.Bottom - diameter,
            diameter,
            diameter,
            0,
            90
        );
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void DrawSpeaker(Graphics graphics, Pen pen, Rectangle bounds)
    {
        float middleY = bounds.Top + bounds.Height / 2f;
        var speaker = new PointF[]
        {
            new(bounds.Left + bounds.Width * 0.1f, middleY - bounds.Height * 0.15f),
            new(bounds.Left + bounds.Width * 0.32f, middleY - bounds.Height * 0.15f),
            new(bounds.Left + bounds.Width * 0.52f, bounds.Top + bounds.Height * 0.17f),
            new(bounds.Left + bounds.Width * 0.52f, bounds.Bottom - bounds.Height * 0.17f),
            new(bounds.Left + bounds.Width * 0.32f, middleY + bounds.Height * 0.15f),
            new(bounds.Left + bounds.Width * 0.1f, middleY + bounds.Height * 0.15f)
        };
        graphics.DrawPolygon(pen, speaker);
        graphics.DrawArc(
            pen,
            RelativeRectangle(bounds, 0.4f, 0.24f, 0.42f, 0.52f),
            -55,
            110
        );
    }

    private static void DrawMicrophone(Graphics graphics, Pen pen, Rectangle bounds)
    {
        graphics.DrawEllipse(pen, RelativeRectangle(bounds, 0.33f, 0.07f, 0.34f, 0.55f));
        graphics.DrawArc(
            pen,
            RelativeRectangle(bounds, 0.18f, 0.27f, 0.64f, 0.52f),
            0,
            180
        );
        graphics.DrawLine(
            pen,
            RelativePoint(bounds, 0.5f, 0.79f),
            RelativePoint(bounds, 0.5f, 0.9f)
        );
        graphics.DrawLine(
            pen,
            RelativePoint(bounds, 0.3f, 0.9f),
            RelativePoint(bounds, 0.7f, 0.9f)
        );
    }

    private static void DrawListener(Graphics graphics, Pen pen, Rectangle bounds)
    {
        graphics.DrawEllipse(pen, RelativeRectangle(bounds, 0.4f, 0.4f, 0.2f, 0.2f));
        graphics.DrawArc(
            pen,
            RelativeRectangle(bounds, 0.23f, 0.23f, 0.54f, 0.54f),
            215,
            110
        );
        graphics.DrawArc(
            pen,
            RelativeRectangle(bounds, 0.08f, 0.08f, 0.84f, 0.84f),
            215,
            110
        );
        graphics.DrawLine(
            pen,
            RelativePoint(bounds, 0.5f, 0.59f),
            RelativePoint(bounds, 0.5f, 0.9f)
        );
    }

    private static void DrawOscQuery(Graphics graphics, Pen pen, Rectangle bounds)
    {
        PointF top = RelativePoint(bounds, 0.5f, 0.16f);
        PointF left = RelativePoint(bounds, 0.2f, 0.76f);
        PointF right = RelativePoint(bounds, 0.8f, 0.76f);
        graphics.DrawLine(pen, top, left);
        graphics.DrawLine(pen, left, right);
        graphics.DrawLine(pen, right, top);

        foreach (PointF node in new[] { top, left, right })
        {
            float diameter = bounds.Width * 0.22f;
            graphics.DrawEllipse(
                pen,
                node.X - diameter / 2f,
                node.Y - diameter / 2f,
                diameter,
                diameter
            );
        }
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
