using AppSupervisor.ConfigurationUI;
using AppSupervisor.Store;
using System.Reflection;
using System.Windows.Forms;

namespace AppSupervisor.Tests;

/// <summary>Verifies picker status text reports rows visible after all filters.</summary>
[Collection(WinFormsTestCollection.Name)]
public sealed class ApplicationPickerVisibleCountTests
{
    /// <summary>Confirms default Microsoft/system filtering is reflected in both picker counts.</summary>
    [Fact]
    public void Populate_DefaultFilters_StatusReportsVisibleRows()
    {
        Exception? threadException = null;

        var thread = new Thread(() =>
        {
            try
            {
                AssertRunningPickerCount();
                AssertStorePickerCount();
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
            "Application picker visible-count test timed out."
        );
        Assert.Null(threadException);
    }

    private static void AssertRunningPickerCount()
    {
        using var picker = new RunningProcessPickerDialog();
        SetField(picker, "_allRows", new List<RunningProcessPickerDialog.ProcessRow>
        {
            new("ThirdParty.exe", @"D:\Apps\ThirdParty.exe", false),
            new("System.exe", null, true)
        });

        Invoke(picker, "PopulateList");

        Assert.Single(GetField<ListView>(picker, "_processList").Items.Cast<ListViewItem>());
        Assert.Equal(
            "1 unique running application shown. 1 Microsoft/Windows application filtered out.",
            GetField<Label>(picker, "_statusLabel").Text
        );
    }

    private static void AssertStorePickerCount()
    {
        using var picker = new StoreApplicationPickerDialog();
        SetField<IReadOnlyList<InstalledStoreApplication>>(
            picker,
            "_applications",
            [
                new("Third Party", "Vendor.Application", "Vendor_1", "App", "app.exe", @"D:\Apps\app.exe", false),
                new("System App", "Microsoft.System", "Microsoft_1", "App", "system.exe", @"C:\Windows\system.exe", true)
            ]
        );

        Invoke(picker, "PopulateApplications");

        Assert.Single(GetField<ListView>(picker, "_applicationList").Items.Cast<ListViewItem>());
        Assert.Equal(
            "1 application shown. 1 Microsoft/system application filtered out.",
            GetField<Label>(picker, "_statusLabel").Text
        );
    }

    private static T GetField<T>(object instance, string name) where T : class =>
        Assert.IsType<T>(instance.GetType().GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic
        )?.GetValue(instance));

    private static void SetField<T>(object instance, string name, T value)
    {
        FieldInfo? field = instance.GetType().GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        Assert.NotNull(field);
        field.SetValue(instance, value);
    }

    private static void Invoke(object instance, string name)
    {
        MethodInfo? method = instance.GetType().GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        Assert.NotNull(method);
        method.Invoke(instance, null);
    }
}
