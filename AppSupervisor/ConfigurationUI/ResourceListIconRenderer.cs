using System.Drawing.Drawing2D;
using MediaBezierSegment = System.Windows.Media.BezierSegment;
using MediaFillRule = System.Windows.Media.FillRule;
using MediaGeometry = System.Windows.Media.Geometry;
using MediaLineSegment = System.Windows.Media.LineSegment;
using MediaPathFigure = System.Windows.Media.PathFigure;
using MediaPathGeometry = System.Windows.Media.PathGeometry;
using MediaPoint = System.Windows.Point;
using MediaPolyBezierSegment = System.Windows.Media.PolyBezierSegment;
using MediaPolyLineSegment = System.Windows.Media.PolyLineSegment;
using MediaToleranceType = System.Windows.Media.ToleranceType;

namespace AppSupervisor.ConfigurationUI;

/// <summary>Draws recognizable integration and Windows resource marks in the compact resource list.</summary>
internal static class ResourceListIconRenderer
{
    internal static readonly Color HomeAssistantColor = Color.FromArgb(24, 188, 242);
    internal static readonly Color TwitchColor = Color.FromArgb(145, 70, 255);
    internal static readonly Color ObsColor = Color.FromArgb(48, 46, 49);
    internal static readonly Color StreamDeckColor = Color.FromArgb(55, 139, 232);
    internal static readonly Color ServiceRearColor = Color.FromArgb(57, 127, 193);
    internal static readonly Color ServiceFrontColor = Color.FromArgb(87, 181, 229);

    private const string CogPath =
        "M12,15.5A3.5,3.5 0 0,1 8.5,12A3.5,3.5 0 0,1 12,8.5" +
        "A3.5,3.5 0 0,1 15.5,12A3.5,3.5 0 0,1 12,15.5" +
        "M19.43,12.97C19.47,12.65 19.5,12.33 19.5,12C19.5,11.67 19.47,11.34 19.43,11" +
        "L21.54,9.37C21.73,9.22 21.78,8.95 21.66,8.73L19.66,5.27" +
        "C19.54,5.05 19.27,4.96 19.05,5.05L16.56,6.05C16.04,5.66 15.5,5.32 14.87,5.07" +
        "L14.5,2.42C14.46,2.18 14.25,2 14,2H10C9.75,2 9.54,2.18 9.5,2.42L9.13,5.07" +
        "C8.5,5.32 7.96,5.66 7.44,6.05L4.95,5.05C4.73,4.96 4.46,5.05 4.34,5.27" +
        "L2.34,8.73C2.21,8.95 2.27,9.22 2.46,9.37L4.57,11" +
        "C4.53,11.34 4.5,11.67 4.5,12C4.5,12.33 4.53,12.65 4.57,12.97L2.46,14.63" +
        "C2.27,14.78 2.21,15.05 2.34,15.27L4.34,18.73C4.46,18.95 4.73,19.03 4.95,18.95" +
        "L7.44,17.94C7.96,18.34 8.5,18.68 9.13,18.93L9.5,21.58" +
        "C9.54,21.82 9.75,22 10,22H14C14.25,22 14.46,21.82 14.5,21.58L14.87,18.93" +
        "C15.5,18.67 16.04,18.34 16.56,17.94L19.05,18.95" +
        "C19.27,19.03 19.54,18.95 19.66,18.73L21.66,15.27" +
        "C21.78,15.05 21.73,14.78 21.54,14.63L19.43,12.97Z";

    private const string HomeAssistantPath =
        "M21.8,13H20V21H13V17.67L15.79,14.88L16.5,15" +
        "C17.66,15 18.6,14.06 18.6,12.9C18.6,11.74 17.66,10.8 16.5,10.8" +
        "A2.1,2.1 0 0,0 14.4,12.9L14.5,13.61L13,15.13V9.65" +
        "C13.66,9.29 14.1,8.6 14.1,7.8A2.1,2.1 0 0,0 12,5.7" +
        "A2.1,2.1 0 0,0 9.9,7.8C9.9,8.6 10.34,9.29 11,9.65V15.13" +
        "L9.5,13.61L9.6,12.9A2.1,2.1 0 0,0 7.5,10.8A2.1,2.1 0 0,0 5.4,12.9" +
        "A2.1,2.1 0 0,0 7.5,15L8.21,14.88L11,17.67V21H4V13H2.25" +
        "C1.83,13 1.42,13 1.42,12.79C1.43,12.57 1.85,12.15 2.28,11.72L11,3" +
        "C11.33,2.67 11.67,2.33 12,2.33C12.33,2.33 12.67,2.67 13,3L17,7V6H19V9" +
        "L21.78,11.78C22.18,12.18 22.59,12.59 22.6,12.8C22.6,13 22.2,13 21.8,13" +
        "M7.5,12A0.9,0.9 0 0,1 8.4,12.9A0.9,0.9 0 0,1 7.5,13.8" +
        "A0.9,0.9 0 0,1 6.6,12.9A0.9,0.9 0 0,1 7.5,12" +
        "M16.5,12C17,12 17.4,12.4 17.4,12.9C17.4,13.4 17,13.8 16.5,13.8" +
        "A0.9,0.9 0 0,1 15.6,12.9A0.9,0.9 0 0,1 16.5,12" +
        "M12,6.9C12.5,6.9 12.9,7.3 12.9,7.8C12.9,8.3 12.5,8.7 12,8.7" +
        "C11.5,8.7 11.1,8.3 11.1,7.8C11.1,7.3 11.5,6.9 12,6.9Z";

    private const string TwitchPath =
        "M11.64 5.93H13.07V10.21H11.64M15.57 5.93H17V10.21H15.57" +
        "M7 2L3.43 5.57V18.43H7.71V22L11.29 18.43H14.14L20.57 12V2" +
        "M19.14 11.29L16.29 14.14H13.43L10.93 16.64V14.14H7.71V3.43H19.14Z";

    private const string ObsPath =
        "M12,24C5.383,24,0,18.617,0,12S5.383,0,12,0S24,5.383,24,12S18.617,24,12,24Z" +
        "M12,1.109C5.995,1.109,1.11,5.995,1.11,12C1.11,18.005,5.995,22.89,12,22.89" +
        "S22.89,18.005,22.89,12C22.89,5.995,18.005,1.109,12,1.109Z" +
        "M6.182,5.99C6.534,4.292,7.685,2.761,9.232,1.994" +
        "C8.963,2.267,8.637,2.477,8.388,2.774C7.368,3.874,6.908,5.466,7.189,6.93" +
        "C7.544,9.165,9.644,10.99,11.921,10.958C13.686,11.037,15.406,10.021,16.269,8.49" +
        "C18.117,8.553,19.914,9.507,20.969,11.038C21.509,11.837,21.931,12.774,21.96,13.749" +
        "C21.618,12.454,20.758,11.303,19.585,10.654C18.45,10.015,17.056,9.852,15.813,10.229" +
        "C14.253,10.677,12.964,11.952,12.52,13.522C12.143,14.772,12.304,16.15,12.897,17.294" +
        "C12.072,18.723,10.582,19.743,8.965,20.05C7.721,20.311,6.414,20.109,5.256,19.586" +
        "C6.292,19.888,7.417,19.941,8.447,19.575C9.828,19.118,10.969,18.008,11.471,16.64" +
        "C12.027,15.15,11.816,13.379,10.88,12.1C10.18,11.093,9.077,10.383,7.878,10.131" +
        "C7.498,10.063,7.114,10.033,6.73,9.997C6.119,8.766,5.896,7.337,6.202,6.001L6.182,5.99Z";

    private static readonly MediaPathGeometry CogGeometry = Parse(CogPath);
    private static readonly MediaPathGeometry HomeAssistantGeometry = Parse(HomeAssistantPath);
    private static readonly MediaPathGeometry TwitchGeometry = Parse(TwitchPath);
    private static readonly MediaPathGeometry ObsGeometry = Parse(ObsPath);

    /// <summary>Draws two overlapping blue gears in the familiar Windows Services style.</summary>
    internal static void DrawService(Graphics graphics, Rectangle bounds, Color selectionColor, bool selected)
    {
        Color rearColor = selected
            ? Color.FromArgb(190, selectionColor)
            : ServiceRearColor;
        Color frontColor = selected ? selectionColor : ServiceFrontColor;
        Draw(CogGeometry, graphics, RelativeBounds(bounds, 0.31f, 0.00f, 0.69f, 0.69f), rearColor);
        Draw(CogGeometry, graphics, RelativeBounds(bounds, 0.00f, 0.38f, 0.62f, 0.62f), frontColor);
    }

    /// <summary>Draws the Home Assistant house-and-nodes brand mark.</summary>
    internal static void DrawHomeAssistant(Graphics graphics, Rectangle bounds, Color selectionColor, bool selected) =>
        Draw(HomeAssistantGeometry, graphics, bounds, selected ? selectionColor : HomeAssistantColor);

    /// <summary>Draws the Twitch chat-glyph brand mark.</summary>
    internal static void DrawTwitch(Graphics graphics, Rectangle bounds, Color selectionColor, bool selected) =>
        Draw(TwitchGeometry, graphics, bounds, selected ? selectionColor : TwitchColor);

    /// <summary>Draws the OBS Studio three-blade brand mark.</summary>
    internal static void DrawObs(Graphics graphics, Rectangle bounds, Color selectionColor, bool selected) =>
        Draw(ObsGeometry, graphics, bounds, selected ? selectionColor : ObsColor);

    /// <summary>Draws Stream Deck's recognizable three-by-two key grid.</summary>
    internal static void DrawStreamDeck(
        Graphics graphics,
        Rectangle bounds,
        Color selectionColor,
        bool selected)
    {
        Color color = selected ? selectionColor : StreamDeckColor;
        float gap = Math.Max(1f, bounds.Width * 0.08f);
        float keyWidth = (bounds.Width - gap * 2f) / 3f;
        float keyHeight = (bounds.Height - gap) / 2f;
        using var brush = new SolidBrush(color);

        for (int row = 0; row < 2; row++)
        {
            for (int column = 0; column < 3; column++)
            {
                graphics.FillRectangle(
                    brush,
                    bounds.Left + column * (keyWidth + gap),
                    bounds.Top + row * (keyHeight + gap),
                    keyWidth,
                    keyHeight
                );
            }
        }
    }

    private static RectangleF RelativeBounds(
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

    private static MediaPathGeometry Parse(string pathData)
    {
        MediaPathGeometry geometry = MediaGeometry.Parse(pathData)
            .GetFlattenedPathGeometry(0.02, MediaToleranceType.Absolute);
        geometry.FillRule = MediaFillRule.Nonzero;
        geometry.Freeze();
        return geometry;
    }

    private static void Draw(
        MediaPathGeometry geometry,
        Graphics graphics,
        Rectangle bounds,
        Color color) =>
        Draw(geometry, graphics, (RectangleF)bounds, color);

    private static void Draw(
        MediaPathGeometry geometry,
        Graphics graphics,
        RectangleF bounds,
        Color color)
    {
        using GraphicsPath path = CreateGraphicsPath(geometry);
        using var transform = new Matrix(
            bounds.Width / 24f,
            0,
            0,
            bounds.Height / 24f,
            bounds.Left,
            bounds.Top
        );
        path.Transform(transform);
        using var brush = new SolidBrush(color);
        graphics.FillPath(brush, path);
    }

    private static GraphicsPath CreateGraphicsPath(MediaPathGeometry geometry)
    {
        var path = new GraphicsPath(
            geometry.FillRule == MediaFillRule.EvenOdd ? FillMode.Alternate : FillMode.Winding
        );

        foreach (MediaPathFigure figure in geometry.Figures)
        {
            path.StartFigure();
            PointF current = ToPoint(figure.StartPoint);

            foreach (System.Windows.Media.PathSegment segment in figure.Segments)
            {
                switch (segment)
                {
                    case MediaLineSegment line:
                    {
                        PointF next = ToPoint(line.Point);
                        path.AddLine(current, next);
                        current = next;
                        break;
                    }
                    case MediaPolyLineSegment lines:
                        foreach (MediaPoint point in lines.Points)
                        {
                            PointF next = ToPoint(point);
                            path.AddLine(current, next);
                            current = next;
                        }
                        break;
                    case MediaBezierSegment bezier:
                    {
                        PointF next = ToPoint(bezier.Point3);
                        path.AddBezier(current, ToPoint(bezier.Point1), ToPoint(bezier.Point2), next);
                        current = next;
                        break;
                    }
                    case MediaPolyBezierSegment beziers:
                        for (int index = 0; index + 2 < beziers.Points.Count; index += 3)
                        {
                            PointF next = ToPoint(beziers.Points[index + 2]);
                            path.AddBezier(
                                current,
                                ToPoint(beziers.Points[index]),
                                ToPoint(beziers.Points[index + 1]),
                                next
                            );
                            current = next;
                        }
                        break;
                }
            }

            if (figure.IsClosed)
                path.CloseFigure();
        }

        return path;
    }

    private static PointF ToPoint(MediaPoint point) => new((float)point.X, (float)point.Y);
}
