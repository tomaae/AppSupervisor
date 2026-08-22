using AppSupervisor.SteamVr;

namespace AppSupervisor.Tests;

/// <summary>Verifies the SteamVR incident window lifecycle.</summary>
[Collection(WinFormsTestCollection.Name)]
public sealed class SteamVrOfflineDevicesFormTests
{
    [Fact]
    public void UpdateDevices_AllShownDevicesSilenced_ClosesVisibleWindow()
    {
        Exception? threadException = null;

        var thread = new Thread(() =>
        {
            try
            {
                using var form = new SteamVrOfflineDevicesForm(_ => { });
                form.UpdateDevices([CreateDevice("LHR-ONE", silenced: false)]);
                form.Show();
                Assert.True(form.Visible);

                form.UpdateDevices(
                [
                    CreateDevice("LHR-ONE", silenced: true),
                    CreateDevice("LHR-TWO", silenced: true)
                ]);

                Assert.False(form.Visible);
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
            "SteamVR offline-device window test timed out."
        );
        Assert.Null(threadException);
    }

    [Fact]
    public void UpdateDevices_AtLeastOneUnsilencedDevice_KeepsVisibleWindowOpen()
    {
        Exception? threadException = null;

        var thread = new Thread(() =>
        {
            try
            {
                using var form = new SteamVrOfflineDevicesForm(_ => { });
                form.UpdateDevices([CreateDevice("LHR-ONE", silenced: false)]);
                form.Show();

                form.UpdateDevices(
                [
                    CreateDevice("LHR-ONE", silenced: true),
                    CreateDevice("LHR-TWO", silenced: false)
                ]);

                Assert.True(form.Visible);
                form.Close();
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
            "SteamVR offline-device window test timed out."
        );
        Assert.Null(threadException);
    }

    private static SteamVrOfflineDevice CreateDevice(string serialNumber, bool silenced)
        => new(
            serialNumber,
            "Test tracker",
            SteamVrDeviceClass.GenericTracker,
            SteamVrDeviceRole.Waist,
            DateTime.UtcNow - TimeSpan.FromMinutes(1),
            silenced
        );
}
