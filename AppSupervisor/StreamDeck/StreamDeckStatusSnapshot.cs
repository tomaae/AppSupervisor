using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace AppSupervisor.StreamDeck;

/// <summary>Stable visual states shared by the tray presentation and Stream Deck status action.</summary>
internal enum StreamDeckVisualState
{
    Idle,
    Paused,
    Supervising,
    Error,
    StartingSupervising,
    StartingError,
    Stopping,
    StoppingSupervising,
    StoppingError
}

/// <summary>Immutable presentation sent to a connected Stream Deck plugin.</summary>
internal sealed record StreamDeckStatusSnapshot(
    StreamDeckVisualState State,
    string Title,
    string Tooltip,
    string Image);

/// <summary>Pre-renders the tray icon variants once at Stream Deck's high-DPI key size.</summary>
internal sealed class StreamDeckStatusImages
{
    private const int ImageSize = 144;
    private const string PngDataUrlPrefix = "data:image/png;base64,";
    private readonly IReadOnlyDictionary<StreamDeckVisualState, string> _images;

    private StreamDeckStatusImages(IReadOnlyDictionary<StreamDeckVisualState, string> images)
    {
        _images = images;
    }

    public string this[StreamDeckVisualState state] => _images[state];

    /// <summary>Loads the largest available executable icon and creates every tray-equivalent variant.</summary>
    public static StreamDeckStatusImages Create(string executablePath, Icon fallbackIcon)
    {
        Icon? executableIcon = null;

        try
        {
            executableIcon = new Icon(executablePath, new Size(ImageSize, ImageSize));
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or System.Runtime.InteropServices.ExternalException
        )
        {
            SupervisorLog.WriteWarning(
                $"Could not load the high-resolution Stream Deck icon; using the tray icon instead. {exception.Message}"
            );
        }

        using Icon sourceIcon = executableIcon ?? (Icon)fallbackIcon.Clone();
        return Create(sourceIcon);
    }

    /// <summary>Creates every icon variant from a caller-supplied source icon.</summary>
    internal static StreamDeckStatusImages Create(Icon sourceIcon)
    {
        var images = new Dictionary<StreamDeckVisualState, string>
        {
            [StreamDeckVisualState.Idle] = EncodePng(sourceIcon)
        };

        Add(images, StreamDeckVisualState.Paused, TrayIconFactory.CreatePausedIcon(sourceIcon));
        Add(
            images,
            StreamDeckVisualState.Supervising,
            TrayIconFactory.CreateSupervisingIcon(sourceIcon)
        );
        Add(images, StreamDeckVisualState.Error, TrayIconFactory.CreateErrorIcon(sourceIcon));

        using Icon supervisingIcon = TrayIconFactory.CreateSupervisingIcon(sourceIcon);
        using Icon errorIcon = TrayIconFactory.CreateErrorIcon(sourceIcon);
        Add(
            images,
            StreamDeckVisualState.StartingSupervising,
            TrayIconFactory.CreateStartingIcon(supervisingIcon)
        );
        Add(
            images,
            StreamDeckVisualState.StartingError,
            TrayIconFactory.CreateStartingIcon(errorIcon)
        );
        Add(images, StreamDeckVisualState.Stopping, TrayIconFactory.CreateStoppingIcon(sourceIcon));
        Add(
            images,
            StreamDeckVisualState.StoppingSupervising,
            TrayIconFactory.CreateStoppingIcon(supervisingIcon)
        );
        Add(
            images,
            StreamDeckVisualState.StoppingError,
            TrayIconFactory.CreateStoppingIcon(errorIcon)
        );

        return new StreamDeckStatusImages(images);
    }

    private static void Add(
        IDictionary<StreamDeckVisualState, string> images,
        StreamDeckVisualState state,
        Icon icon)
    {
        using (icon)
            images.Add(state, EncodePng(icon));
    }

    private static string EncodePng(Icon icon)
    {
        using Bitmap source = icon.ToBitmap();
        using var output = new Bitmap(ImageSize, ImageSize, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(output))
        {
            graphics.Clear(Color.Transparent);
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.DrawImage(source, new Rectangle(0, 0, ImageSize, ImageSize));
        }

        using var stream = new MemoryStream();
        output.Save(stream, ImageFormat.Png);
        return PngDataUrlPrefix + Convert.ToBase64String(
            stream.GetBuffer(),
            0,
            checked((int)stream.Length)
        );
    }
}
