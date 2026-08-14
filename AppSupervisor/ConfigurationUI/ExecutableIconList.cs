namespace AppSupervisor.ConfigurationUI;

/// <summary>Owns a compact DPI-aware image list populated from executable file icons.</summary>
internal sealed class ExecutableIconList : IDisposable
{
    internal const string FallbackKey = "fallback:application";

    private readonly HashSet<string> _failedPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a standard 16 logical-pixel small-image list.</summary>
    /// <param name="deviceDpi">The owning window's current DPI.</param>
    public ExecutableIconList(int deviceDpi)
    {
        int iconSize = Math.Max(16, 16 * deviceDpi / 96);
        Images = new ImageList
        {
            ColorDepth = ColorDepth.Depth32Bit,
            ImageSize = new Size(iconSize, iconSize)
        };
        Images.Images.Add(FallbackKey, SystemIcons.Application);
    }

    /// <summary>Gets the image list assigned to compact ListView controls.</summary>
    public ImageList Images { get; }

    /// <summary>Gets or creates the image key for an executable, falling back safely when unavailable.</summary>
    /// <param name="path">The executable path whose associated icon is requested.</param>
    /// <returns>A key that is always present in <see cref="Images"/>.</returns>
    public string GetImageKey(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return FallbackKey;

        string fullPath;

        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch
        {
            return FallbackKey;
        }

        string key = $"executable:{fullPath}";

        if (Images.Images.ContainsKey(key))
            return key;

        if (_failedPaths.Contains(fullPath))
            return FallbackKey;

        try
        {
            using Icon? icon = Icon.ExtractAssociatedIcon(fullPath);

            if (icon is not null)
            {
                Images.Images.Add(key, icon);
                return key;
            }
        }
        catch
        {
            // Missing, inaccessible, and non-Win32 files use the standard application icon.
        }

        _failedPaths.Add(fullPath);
        return FallbackKey;
    }

    /// <summary>Draws one executable icon inside an owner-drawn compact control.</summary>
    public void Draw(Graphics graphics, Rectangle bounds, string? path)
    {
        string key = GetImageKey(path);
        int imageIndex = Images.Images.IndexOfKey(key);

        if (imageIndex < 0)
            imageIndex = Images.Images.IndexOfKey(FallbackKey);

        Images.Draw(
            graphics,
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height,
            imageIndex
        );
    }

    /// <summary>Releases every native image-list resource.</summary>
    public void Dispose() => Images.Dispose();
}
