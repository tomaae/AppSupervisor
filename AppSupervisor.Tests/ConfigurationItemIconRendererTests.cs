using AppSupervisor.ConfigurationUI;
using AppSupervisor.Core;
using System.Drawing;

namespace AppSupervisor.Tests;

/// <summary>Verifies direction-specific audio pictograms are both visible and distinct.</summary>
public sealed class ConfigurationItemIconRendererTests
{
    [Fact]
    public void DrawAudio_InputAndOutput_RenderDistinctPictograms()
    {
        string output = RenderAudioSignature(AudioInterfaceDirection.Output);
        string input = RenderAudioSignature(AudioInterfaceDirection.Input);

        Assert.NotEmpty(output);
        Assert.NotEmpty(input);
        Assert.NotEqual(output, input);
    }

    [Fact]
    public void DrawRuntimeStatus_AllStates_RenderVisibleDistinctPictograms()
    {
        string[] signatures = Enum.GetValues<ConfigurationResourceRuntimeStatus>()
            .Select(RenderRuntimeStatusSignature)
            .ToArray();

        Assert.All(signatures, Assert.NotEmpty);
        Assert.Equal(signatures.Length, signatures.Distinct().Count());
    }

    private static string RenderAudioSignature(AudioInterfaceDirection direction)
    {
        using var bitmap = new Bitmap(24, 24);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        ConfigurationItemIconRenderer.DrawAudio(
            graphics,
            new Rectangle(2, 2, 20, 20),
            direction,
            Color.Black
        );

        return string.Join(
            ',',
            from y in Enumerable.Range(0, bitmap.Height)
            from x in Enumerable.Range(0, bitmap.Width)
            let pixel = bitmap.GetPixel(x, y)
            where pixel.A > 0
            select $"{x}:{y}:{pixel.A}"
        );
    }

    private static string RenderRuntimeStatusSignature(
        ConfigurationResourceRuntimeStatus status)
    {
        using var bitmap = new Bitmap(24, 24);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        ConfigurationItemIconRenderer.DrawRuntimeStatus(
            graphics,
            new Rectangle(2, 2, 20, 20),
            status,
            Color.White,
            selected: false
        );

        return string.Join(
            ',',
            from y in Enumerable.Range(0, bitmap.Height)
            from x in Enumerable.Range(0, bitmap.Width)
            let pixel = bitmap.GetPixel(x, y)
            where pixel.A > 0
            select $"{x}:{y}:{pixel.ToArgb()}"
        );
    }
}
