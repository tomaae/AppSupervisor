using AppSupervisor.Configuration;
using AppSupervisor.ConfigurationUI;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace AppSupervisor.Tests;

/// <summary>Verifies concise resource-list labels and recognizable resource-type marks.</summary>
public sealed class ConfigurationEditorResourceListPresentationTests
{
    /// <summary>Confirms icon-bearing rows no longer repeat their type in square brackets.</summary>
    [Fact]
    public void GetResourceListDisplayName_AllResourceTypes_OmitsBracketedTypePrefix()
    {
        ManagedResourceConfig[] resources =
        [
            new ManagedApplicationConfig { Path = @"C:\Tools\Helper.exe" },
            new ManagedServiceConfig { ServiceName = "Spooler", Enabled = false },
            new DelayResourceConfig { DurationMilliseconds = 1_500 },
            new HomeAssistantResourceConfig
            {
                EntityId = "light.desk",
                EntityName = "Desk light"
            },
            new ObsResourceConfig(),
            new StreamDeckResourceConfig { ActionName = "Start VR" },
            new TwitchResourceConfig(),
            new AudioInterfaceResourceConfig()
        ];

        string[] labels = resources
            .Select(ConfigurationEditorForm.GetResourceListDisplayName)
            .ToArray();

        Assert.Equal("Helper.exe", labels[0]);
        Assert.Equal("Spooler (disabled)", labels[1]);
        Assert.Equal("Desk light", labels[3]);
        Assert.All(labels, label =>
        {
            Assert.NotEmpty(label);
            Assert.DoesNotContain('[', label);
            Assert.DoesNotContain(']', label);
        });

        Assert.True(ConfigurationEditorForm.UsesRuntimeStatusIcon(resources[0]));
        Assert.True(ConfigurationEditorForm.UsesRuntimeStatusIcon(resources[1]));
        Assert.All(resources.Skip(2), resource =>
            Assert.False(ConfigurationEditorForm.UsesRuntimeStatusIcon(resource))
        );
    }

    /// <summary>Confirms every requested resource mark renders in its expected distinct color.</summary>
    [Fact]
    public void Draw_BrandedResourceMarks_UsesDistinctRecognizableColors()
    {
        const int iconSize = 20;
        using var preview = new Bitmap(iconSize * 5, iconSize, PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(preview);
        graphics.Clear(Color.White);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var selectionColor = Color.White;

        ResourceListIconRenderer.DrawService(
            graphics,
            new Rectangle(0, 0, iconSize, iconSize),
            selectionColor,
            selected: false
        );
        ResourceListIconRenderer.DrawHomeAssistant(
            graphics,
            new Rectangle(iconSize, 0, iconSize, iconSize),
            selectionColor,
            selected: false
        );
        ResourceListIconRenderer.DrawTwitch(
            graphics,
            new Rectangle(iconSize * 2, 0, iconSize, iconSize),
            selectionColor,
            selected: false
        );
        ResourceListIconRenderer.DrawObs(
            graphics,
            new Rectangle(iconSize * 3, 0, iconSize, iconSize),
            selectionColor,
            selected: false
        );
        ResourceListIconRenderer.DrawStreamDeck(
            graphics,
            new Rectangle(iconSize * 4, 0, iconSize, iconSize),
            selectionColor,
            selected: false
        );

        AssertColorCoverage(preview, 0, iconSize, ResourceListIconRenderer.ServiceRearColor);
        AssertColorCoverage(preview, 0, iconSize, ResourceListIconRenderer.ServiceFrontColor);
        AssertColorCoverage(
            preview,
            iconSize,
            iconSize * 2,
            ResourceListIconRenderer.HomeAssistantColor
        );
        AssertColorCoverage(
            preview,
            iconSize * 2,
            iconSize * 3,
            ResourceListIconRenderer.TwitchColor
        );
        AssertColorCoverage(
            preview,
            iconSize * 3,
            iconSize * 4,
            ResourceListIconRenderer.ObsColor
        );
        AssertColorCoverage(
            preview,
            iconSize * 4,
            iconSize * 5,
            ResourceListIconRenderer.StreamDeckColor
        );

    }

    private static void AssertColorCoverage(Bitmap bitmap, int left, int right, Color color)
    {
        int matchingPixels = 0;

        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = left; x < right; x++)
            {
                Color pixel = bitmap.GetPixel(x, y);

                if (Math.Abs(pixel.R - color.R) <= 48 &&
                    Math.Abs(pixel.G - color.G) <= 48 &&
                    Math.Abs(pixel.B - color.B) <= 48)
                {
                    matchingPixels++;
                }
            }
        }

        Assert.True(
            matchingPixels >= 5,
            $"Expected at least five pixels near {color}, but found {matchingPixels}."
        );
    }
}
