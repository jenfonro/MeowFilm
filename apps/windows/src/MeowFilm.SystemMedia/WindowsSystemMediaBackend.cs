using System.Diagnostics;
using Microsoft.UI.Xaml;
using Windows.Media;

namespace MeowFilm.SystemMedia;

/// <summary>Real HWND-bound Windows SMTC; no MediaPlayer/second decoder is created.</summary>
public sealed class WindowsSystemMediaBackend : ISystemMediaBackend
{
    private const double TimelineThrottleIntervalSeconds = 1.0;
    private const double TimelineJumpThresholdSeconds = 1.5;
    private const double TimelineDurationChangeThresholdSeconds = 0.5;

    private static int activeSession;
    private readonly SystemMediaTransportControls controls;
    private bool disposed;
    private bool? lastHasMedia, lastIsPlaying, lastCanNext, lastCanPrevious;
    private string? displayedOwner, displayedIdentity, displayedTitle;
    private SystemPlaybackKind? displayedKind;
    private bool timelinePublished;
    private double lastTimelinePosition;
    private double lastTimelineDuration;
    private bool lastTimelinePlaying;
    private long lastTimelinePublishedAt;
    public event EventHandler<SystemPlaybackRequest>? Requested;

    public WindowsSystemMediaBackend(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!window.DispatcherQueue.HasThreadAccess) throw new InvalidOperationException("SYSTEM_MEDIA_THREAD_ACCESS");
        if (Interlocked.CompareExchange(ref activeSession, 1, 0) != 0)
            throw new InvalidOperationException("SYSTEM_MEDIA_ALREADY_OWNED");
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
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

        bool hasMedia = state.HasMedia;
        bool isPlaying = hasMedia && state.IsPlaying;
        bool canNext = hasMedia && state.CanNext;
        bool canPrevious = hasMedia && state.CanPrevious;

        if (lastHasMedia != hasMedia)
        {
            controls.IsEnabled = hasMedia;
            controls.IsPlayEnabled = hasMedia;
            controls.IsPauseEnabled = hasMedia;
            controls.IsStopEnabled = hasMedia;
        }
        if (lastCanNext != canNext) controls.IsNextEnabled = canNext;
        if (lastCanPrevious != canPrevious) controls.IsPreviousEnabled = canPrevious;
        if (lastHasMedia != hasMedia || lastIsPlaying != isPlaying)
        {
            controls.PlaybackStatus = !hasMedia ? MediaPlaybackStatus.Closed :
                isPlaying ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
        }
        lastHasMedia = hasMedia;
        lastIsPlaying = isPlaying;
        lastCanNext = canNext;
        lastCanPrevious = canPrevious;

        if (!hasMedia)
        {
            if (displayedOwner is not null || displayedIdentity is not null || displayedTitle is not null || timelinePublished)
            {
                controls.DisplayUpdater.ClearAll();
                controls.DisplayUpdater.Update();
                displayedOwner = displayedIdentity = displayedTitle = null;
                displayedKind = null;
                timelinePublished = false;
            }
            return;
        }

        bool trackChanged = displayedOwner != state.OwnerId || displayedIdentity != state.Identity;
        if (trackChanged || displayedTitle != state.Title || displayedKind != state.Kind)
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
            displayedKind = state.Kind;
        }

        double duration = double.IsFinite(state.DurationSeconds) ? Math.Clamp(state.DurationSeconds, 0, 604800) : 0;
        double position = double.IsFinite(state.PositionSeconds) ? Math.Clamp(state.PositionSeconds, 0, duration) : 0;
        long now = Stopwatch.GetTimestamp();

        if (!ShouldPublishTimeline(trackChanged, isPlaying, position, duration, now))
            return;

        controls.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
        {
            StartTime = TimeSpan.Zero,
            EndTime = TimeSpan.FromSeconds(duration),
            MinSeekTime = TimeSpan.Zero,
            MaxSeekTime = TimeSpan.FromSeconds(duration),
            Position = TimeSpan.FromSeconds(position)
        });
        timelinePublished = true;
        lastTimelinePosition = position;
        lastTimelineDuration = duration;
        lastTimelinePlaying = isPlaying;
        lastTimelinePublishedAt = now;
    }

    private bool ShouldPublishTimeline(bool trackChanged, bool isPlaying, double position, double duration, long now)
    {
        if (!timelinePublished || trackChanged) return true;
        if (lastTimelinePlaying != isPlaying) return true;
        if (Math.Abs(duration - lastTimelineDuration) >= TimelineDurationChangeThresholdSeconds) return true;

        double elapsed = Stopwatch.GetElapsedTime(lastTimelinePublishedAt, now).TotalSeconds;
        double expected = lastTimelinePlaying
            ? Math.Clamp(lastTimelinePosition + elapsed, 0, duration)
            : lastTimelinePosition;
        if (Math.Abs(position - expected) >= TimelineJumpThresholdSeconds) return true;

        return elapsed >= TimelineThrottleIntervalSeconds &&
            (isPlaying || Math.Abs(position - lastTimelinePosition) >= 0.5);
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
        lastHasMedia = lastIsPlaying = lastCanNext = lastCanPrevious = null;
        displayedOwner = displayedIdentity = displayedTitle = null;
        displayedKind = null;
        timelinePublished = false;
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
