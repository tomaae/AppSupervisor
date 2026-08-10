namespace AppSupervisor.Notifications;

/// <summary>
/// Displays classic modal Windows message boxes on isolated background threads so supervision never blocks.
/// </summary>
internal sealed class PopupNotificationProvider : INotificationProvider, IDisposable
{
    private readonly Action<SupervisorNotification>? _showDialog;
    private readonly object _dialogLock = new();
    private readonly HashSet<Form> _dialogOwners = [];
    private bool _disposed;

    /// <summary>Creates a provider that uses the production Windows message-box presenter.</summary>
    public PopupNotificationProvider()
    {
    }

    /// <summary>Creates a provider with an injectable dialog presenter for isolated verification.</summary>
    /// <param name="showDialog">The callback that presents one acknowledged modal dialog.</param>
    internal PopupNotificationProvider(Action<SupervisorNotification> showDialog)
    {
        _showDialog = showDialog;
    }

    /// <summary>Gets the classic popup-dialog target handled by this provider.</summary>
    public NotificationTarget Target => NotificationTarget.Popup;

    /// <summary>Queues a message box on a background STA thread and returns without waiting for acknowledgment.</summary>
    /// <param name="notification">The notification content to display.</param>
    /// <param name="cancellationToken">Cancels delivery before the dialog thread is started.</param>
    /// <returns>A completed result indicating whether the background dialog thread was started.</returns>
    public Task<bool> SendAsync(
        SupervisorNotification notification,
        CancellationToken cancellationToken)
    {
        lock (_dialogLock)
        {
            if (_disposed || cancellationToken.IsCancellationRequested)
                return Task.FromResult(false);
        }

        try
        {
            var dialogThread = new Thread(() => ShowDialogSafely(notification))
            {
                IsBackground = true,
                Name = "AppSupervisor popup notification"
            };
            dialogThread.SetApartmentState(ApartmentState.STA);
            dialogThread.Start();
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    /// <summary>Contains dialog presentation failures inside the disposable background thread.</summary>
    /// <param name="notification">The notification to present.</param>
    private void ShowDialogSafely(SupervisorNotification notification)
    {
        try
        {
            if (_showDialog is not null)
                _showDialog(notification);
            else
                ShowTrackedMessageBox(notification);
        }
        catch
        {
            // Popup delivery is best-effort and must never affect supervision.
        }
    }

    /// <summary>Shows a native message box owned by a tracked hidden form that shutdown can destroy.</summary>
    /// <param name="notification">The notification content and severity to display.</param>
    private void ShowTrackedMessageBox(SupervisorNotification notification)
    {
        using Form owner = CreateDialogOwner();

        lock (_dialogLock)
        {
            if (_disposed)
                return;

            _dialogOwners.Add(owner);
        }

        try
        {
            ShowMessageBox(owner, notification);
        }
        finally
        {
            lock (_dialogLock)
                _dialogOwners.Remove(owner);
        }
    }

    /// <summary>Creates an invisible per-thread owner for one native notification message box.</summary>
    /// <returns>A handle-backed hidden form that can dismiss its owned message box.</returns>
    private static Form CreateDialogOwner()
    {
        var owner = new Form
        {
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.FixedToolWindow,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            Size = new Size(1, 1),
            Opacity = 0
        };

        _ = owner.Handle;
        return owner;
    }

    /// <summary>Shows the classic Windows OK dialog with an icon matching the notification severity.</summary>
    /// <param name="owner">The tracked hidden owner used to dismiss the modal during shutdown.</param>
    /// <param name="notification">The notification content and severity to display.</param>
    private static void ShowMessageBox(
        IWin32Window owner,
        SupervisorNotification notification)
    {
        MessageBoxIcon icon = notification.Severity switch
        {
            NotificationSeverity.Warning => MessageBoxIcon.Warning,
            NotificationSeverity.Error => MessageBoxIcon.Error,
            _ => MessageBoxIcon.Information
        };

        MessageBox.Show(
            owner,
            notification.Message,
            notification.Title,
            MessageBoxButtons.OK,
            icon
        );
    }

    /// <summary>Prevents new popup delivery and asynchronously closes every active native notification modal.</summary>
    public void Dispose()
    {
        Form[] owners;

        lock (_dialogLock)
        {
            if (_disposed)
                return;

            _disposed = true;
            owners = _dialogOwners.ToArray();
            _dialogOwners.Clear();
        }

        foreach (Form owner in owners)
        {
            try
            {
                if (!owner.IsDisposed && owner.IsHandleCreated)
                    owner.BeginInvoke(new Action(owner.Dispose));
            }
            catch (InvalidOperationException)
            {
                // The popup thread already closed the owner between the snapshot and dispatch.
            }
        }
    }
}
