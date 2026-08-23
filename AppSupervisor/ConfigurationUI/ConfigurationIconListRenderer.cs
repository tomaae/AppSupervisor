namespace AppSupervisor.ConfigurationUI;

/// <summary>Provides consistent 20-pixel icon and text layout for owner-drawn configuration lists.</summary>
internal static class ConfigurationIconListRenderer
{
    private const int LogicalIconSize = 20;

    /// <summary>Returns the standard icon size scaled for the control's display.</summary>
    internal static int GetIconSize(Control control) =>
        Math.Max(LogicalIconSize, LogicalIconSize * control.DeviceDpi / 96);

    /// <summary>Returns a row height that accommodates the standard icon and the control font.</summary>
    internal static int GetItemHeight(Control control) =>
        Math.Max(control.Font.Height + 2, GetIconSize(control) + 2);

    /// <summary>Draws an icon, an ellipsized label, and the standard selection and focus states.</summary>
    internal static void DrawItem(
        DrawItemEventArgs e,
        Font font,
        string text,
        Action<Graphics, Rectangle, Color, bool>? drawIcon) =>
        DrawItem(e, font, text, drawIcon, drawSecondIcon: null);

    /// <summary>Draws two independent icons followed by an ellipsized label.</summary>
    internal static void DrawItem(
        DrawItemEventArgs e,
        Font font,
        string text,
        Action<Graphics, Rectangle, Color, bool>? drawIcon,
        Action<Graphics, Rectangle, Color, bool>? drawSecondIcon)
    {
        e.DrawBackground();

        int textLeft = e.Bounds.Left + 3;
        int preferredIconSize = Math.Max(
            LogicalIconSize,
            (int)Math.Round(LogicalIconSize * e.Graphics.DpiX / 96f)
        );
        int iconSize = Math.Max(0, Math.Min(preferredIconSize, e.Bounds.Height - 2));
        int iconTop = e.Bounds.Top + (e.Bounds.Height - iconSize) / 2;
        bool selected = (e.State & DrawItemState.Selected) != 0;

        void DrawNextIcon(Action<Graphics, Rectangle, Color, bool>? iconDrawer)
        {
            if (iconDrawer is null)
                return;

            var iconBounds = new Rectangle(textLeft, iconTop, iconSize, iconSize);
            iconDrawer(e.Graphics, iconBounds, e.ForeColor, selected);
            textLeft = iconBounds.Right + 4;
        }

        DrawNextIcon(drawIcon);
        DrawNextIcon(drawSecondIcon);

        var textBounds = new Rectangle(
            textLeft,
            e.Bounds.Top,
            Math.Max(0, e.Bounds.Right - textLeft - 3),
            e.Bounds.Height
        );
        TextRenderer.DrawText(
            e.Graphics,
            text,
            font,
            textBounds,
            e.ForeColor,
            TextFormatFlags.Left |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPrefix
        );
        e.DrawFocusRectangle();
    }
}
