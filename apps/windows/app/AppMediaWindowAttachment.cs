using System.ComponentModel;
using System.Windows;
using MeowFilm.Desktop;

namespace MeowFilm.App;

/// <summary>Owns window media attachments and their settings subscription, including partial startup cleanup.</summary>
internal sealed class AppMediaWindowAttachment : IAsyncDisposable
{
    private readonly Window window;
    private readonly IDesktopMediaControlSettings settings;
    private IAsyncDisposable? systemMedia, shortcuts;
    private Action<bool>? setSystemEnabled;
    private bool stopped;
    private Task? disposing;

    internal AppMediaWindowAttachment(Window window, IDesktopMediaControlSettings settings)
    {
        window.Dispatcher.VerifyAccess();
        this.window = window; this.settings = settings;
        settings.PropertyChanged += Changed;
        window.Closed += Closed;
    }

    internal void OwnSystemMedia(IAsyncDisposable attachment, Action<bool> setEnabled)
    {
        systemMedia = attachment;
        setSystemEnabled = setEnabled;
        UpdateSystemMedia();
    }

    internal void OwnShortcuts(IAsyncDisposable attachment) => shortcuts = attachment;

    private void Changed(object? sender, PropertyChangedEventArgs args)
    {
        if (stopped || args.PropertyName is not (null or "" or
            nameof(IDesktopMediaControlSettings.Preferences) or nameof(IDesktopMediaControlSettings.IsAvailable))) return;
        if (window.Dispatcher.CheckAccess()) UpdateSystemMedia();
        else if (!window.Dispatcher.HasShutdownStarted)
            _ = window.Dispatcher.InvokeAsync(() => { if (!stopped) UpdateSystemMedia(); });
    }

    private void UpdateSystemMedia() => setSystemEnabled?.Invoke(settings.IsAvailable && settings.Preferences.SystemMediaEnabled);

    private void Closed(object? sender, EventArgs args) => Stop();

    private void Stop()
    {
        if (stopped) return;
        stopped = true;
        settings.PropertyChanged -= Changed;
        window.Closed -= Closed;
        setSystemEnabled?.Invoke(false);
    }

    public ValueTask DisposeAsync()
    {
        window.Dispatcher.VerifyAccess();
        if (disposing is null || disposing.IsFaulted || disposing.IsCanceled) disposing = DisposeCore();
        return new(disposing);
    }

    private async Task DisposeCore()
    {
        Stop();
        // The shortcut callback still uses the product's playback owners until
        // its actual Task settles. Keep those attachments alive across that wait.
        if (shortcuts is not null)
        {
            await shortcuts.DisposeAsync();
            shortcuts = null;
        }
        if (systemMedia is not null)
        {
            await systemMedia.DisposeAsync();
            systemMedia = null;
            setSystemEnabled = null;
        }
    }
}
