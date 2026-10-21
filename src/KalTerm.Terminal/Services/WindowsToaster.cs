using CommunityToolkit.WinUI.Notifications;

namespace KalTerm.Terminal.Services;

/// <summary>
/// The real toasts. An unpackaged app gets them through the toolkit, which registers a notification identity and a click handler for it under the current user; a click reaches <see cref="Activated"/> whether the
/// terminal was running or was started by the click. The UI thread's context, taken when this is made, carries the click back to the UI thread.
/// </summary>
internal sealed class WindowsToaster : IToaster
{
    private const string TickerArgument = "ticker";
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;

    public WindowsToaster() => ToastNotificationManagerCompat.OnActivated += OnActivated;

    public event Action<string?>? Activated;

    public void Show(ToastMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        try
        {
            var toast = new ToastContentBuilder().AddText(message.Title).AddText(message.Body);
            if (message.Ticker is not null)
            {
                _ = toast.AddArgument(TickerArgument, message.Ticker);
            }

            if (message.Silent)
            {
                _ = toast.AddAudio(new ToastAudio { Silent = true });
            }

            toast.Show();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // toasts are off or unavailable on this computer: the fire is still in the Alerts page
        }
    }

    private void OnActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        var arguments = ToastArguments.Parse(e.Argument);
        var ticker = arguments.TryGetValue(TickerArgument, out var value) && value.Length > 0 ? value : null;
        if (_context is { } context)
        {
            context.Post(_ => Activated?.Invoke(ticker), null);
        }
        else
        {
            Activated?.Invoke(ticker);
        }
    }
}
