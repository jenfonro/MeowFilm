using System.Windows;
using System.Windows.Interop;
using Windows.Media;

namespace MeowFilm.SystemMedia;

/// <summary>Real HWND-bound Windows SMTC; no MediaPlayer/second decoder is created.</summary>
public sealed class WindowsSystemMediaBackend : ISystemMediaBackend
{
    private static int activeSession;
    private readonly SystemMediaTransportControls controls;
    private bool disposed;
    private string? displayedOwner, displayedIdentity, displayedTitle;
    public event EventHandler<SystemPlaybackRequest>? Requested;

    public WindowsSystemMediaBackend(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.Dispatcher.VerifyAccess();
        if (Interlocked.CompareExchange(ref activeSession, 1, 0) != 0)
            throw new InvalidOperationException("SYSTEM_MEDIA_ALREADY_OWNED");
        try
        {
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            controls = SystemMediaTransportControlsInterop.GetForWindow(hwnd);
            controls.IsEnabled = false;
            controls.ButtonPressed += ButtonPressed;
            try { controls.PlaybackPositionChangeRequested += PositionRequested; }
            catch { controls.ButtonPressed -= ButtonPressed; throw; }
        }
        catch
        {
            Interlocked.Exchange(ref activeSession, 0);
            throw;
        }
    }

    public void Update(SystemPlaybackState state)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        controls.IsEnabled = state.HasMedia;
        controls.IsPlayEnabled = state.HasMedia;
        controls.IsPauseEnabled = state.HasMedia;
        controls.IsStopEnabled = state.HasMedia;
        controls.IsNextEnabled = state.HasMedia && state.CanNext;
        controls.IsPreviousEnabled = state.HasMedia && state.CanPrevious;
        controls.PlaybackStatus = !state.HasMedia ? MediaPlaybackStatus.Closed :
            state.IsPlaying ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
        if (!state.HasMedia)
        {
            controls.DisplayUpdater.ClearAll();
            displayedOwner = displayedIdentity = displayedTitle = null;
            return;
        }
        if (displayedOwner != state.OwnerId || displayedIdentity != state.Identity || displayedTitle != state.Title)
        {
            var display = controls.DisplayUpdater;
            display.ClearAll();
            display.Type = state.Kind == SystemPlaybackKind.Video ? MediaPlaybackType.Video : MediaPlaybackType.Music;
            if (state.Kind == SystemPlaybackKind.Video) display.VideoProperties.Title = state.Title;
            else display.MusicProperties.Title = state.Title;
            display.Update();
            displayedOwner = state.OwnerId;
            displayedIdentity = state.Identity;
            displayedTitle = state.Title;
        }
        double duration = double.IsFinite(state.DurationSeconds) ? Math.Clamp(state.DurationSeconds, 0, 604800) : 0;
        double position = double.IsFinite(state.PositionSeconds) ? Math.Clamp(state.PositionSeconds, 0, duration) : 0;
        controls.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
        {
            StartTime = TimeSpan.Zero, EndTime = TimeSpan.FromSeconds(duration),
            MinSeekTime = TimeSpan.Zero, MaxSeekTime = TimeSpan.FromSeconds(duration),
            Position = TimeSpan.FromSeconds(position)
        });
    }

    private void ButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        if (disposed) return;
        SystemPlaybackCommand? command = args.Button switch
        {
            SystemMediaTransportControlsButton.Play => SystemPlaybackCommand.Play,
            SystemMediaTransportControlsButton.Pause => SystemPlaybackCommand.Pause,
            SystemMediaTransportControlsButton.Stop => SystemPlaybackCommand.Stop,
            SystemMediaTransportControlsButton.Next => SystemPlaybackCommand.Next,
            SystemMediaTransportControlsButton.Previous => SystemPlaybackCommand.Previous,
            _ => null
        };
        if (command is not null) Requested?.Invoke(this, new(command.Value));
    }

    private void PositionRequested(SystemMediaTransportControls sender, PlaybackPositionChangeRequestedEventArgs args)
    {
        if (!disposed) Requested?.Invoke(this, new(SystemPlaybackCommand.Seek, args.RequestedPlaybackPosition.TotalSeconds));
    }

    public void Disable()
    {
        if (disposed) return;
        controls.IsEnabled = false;
        controls.PlaybackStatus = MediaPlaybackStatus.Closed;
        controls.DisplayUpdater.ClearAll();
        controls.DisplayUpdater.Update();
    }

    public void Dispose()
    {
        if (disposed) return;
        try { Disable(); }
        finally
        {
            disposed = true;
            try
            {
                controls.ButtonPressed -= ButtonPressed;
                controls.PlaybackPositionChangeRequested -= PositionRequested;
            }
            finally
            {
                Requested = null;
                Interlocked.Exchange(ref activeSession, 0);
            }
        }
    }
}
