using AppSupervisor.ConfigurationUI;
using System.Drawing;
using System.Windows.Forms;

namespace AppSupervisor.Tests;

/// <summary>Verifies compact executable icons are configured across every application picker.</summary>
[Collection(WinFormsTestCollection.Name)]
public sealed class ApplicationPickerIconTests
{
    /// <summary>Confirms each picker uses the same DPI-aware small-image presentation.</summary>
    [Fact]
    public void Constructors_AllApplicationPickers_AssignCompactImageLists()
    {
        Exception? threadException = null;

        var thread = new Thread(() =>
        {
            try
            {
                using var running = new RunningProcessPickerDialog();
                using var steam = new SteamApplicationPickerDialog();
                using var store = new StoreApplicationPickerDialog();

                AssertPickerImageList(running);
                AssertPickerImageList(steam);
                AssertPickerImageList(store);

                ComboBox executableSelector = Assert.Single(
                    EnumerateControls(steam).OfType<ComboBox>()
                );
                Assert.Equal(DrawMode.OwnerDrawFixed, executableSelector.DrawMode);
            }
            catch (Exception exception)
            {
                threadException = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(
            thread.Join(TimeSpan.FromSeconds(10)),
            "Application picker icon test timed out."
        );
        Assert.Null(threadException);
    }

    /// <summary>Confirms executable extraction is cached and invalid paths use the fallback image.</summary>
    [Fact]
    public void GetImageKey_ExecutableAndInvalidPath_ReturnsPresentKeys()
    {
        using var icons = new ExecutableIconList(96);

        string executableKey = icons.GetImageKey(Environment.ProcessPath);
        string fallbackKey = icons.GetImageKey("\0invalid");

        Assert.True(icons.Images.Images.ContainsKey(executableKey));
        Assert.Equal(ExecutableIconList.FallbackKey, fallbackKey);
        Assert.True(icons.Images.Images.ContainsKey(fallbackKey));
        Assert.Equal(new Size(16, 16), icons.Images.ImageSize);
    }

    /// <summary>Checks the one detailed result list owned by an application picker.</summary>
    private static void AssertPickerImageList(Form picker)
    {
        ListView list = Assert.Single(EnumerateControls(picker).OfType<ListView>());
        Assert.NotNull(list.SmallImageList);
        int expectedSize = Math.Max(16, 16 * picker.DeviceDpi / 96);
        Assert.Equal(new Size(expectedSize, expectedSize), list.SmallImageList.ImageSize);
        Assert.True(list.SmallImageList.Images.ContainsKey(ExecutableIconList.FallbackKey));
    }

    /// <summary>Recursively enumerates one control and all descendants.</summary>
    private static IEnumerable<Control> EnumerateControls(Control root)
    {
        yield return root;

        foreach (Control child in root.Controls)
        {
            foreach (Control descendant in EnumerateControls(child))
                yield return descendant;
        }
    }
}
