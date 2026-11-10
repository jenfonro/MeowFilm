using System.ComponentModel;
using System.Windows;
using MeowFilm.Media;

namespace MeowFilm.Capabilities;

internal interface IFilmNativeMedia : INotifyPropertyChanged
{
    FrameworkElement? View { get; }
    NativePlaybackScope? Scope { get; }
    string? Identity { get; }
    bool HasOutput { get; }
    bool Paused { get; }
    double Position { get; }
    double Duration { get; }
    int Volume { get; set; }
    event EventHandler? Failed;
    event EventHandler? Ended;
    Task PrepareAsync(RuntimeMediaPlaybackIntent intent, FilmMediaReceipt receipt,
        INativeMediaResourceLease? lease, CancellationToken token);
    Task<bool> TryCommitAsync(RuntimeMediaPlaybackIntent intent, Func<bool> canCommit,
        Action committed, CancellationToken token);
    Task DiscardAsync(RuntimeMediaPlaybackIntent intent);
    Task<IAsyncDisposable> OwnOutputAsync(NativePlaybackScope scope, IAsyncDisposable output, CancellationToken token);
    Task PauseAsync(NativePlaybackScope scope, bool paused);
    Task SeekAsync(NativePlaybackScope scope, double position);
    Task RateAsync(NativePlaybackScope scope, double rate);
    Task AspectAsync(NativePlaybackScope scope, string? aspect);
    Task StopAsync(NativePlaybackScope scope);
}

/// <summary>Thin consumer of the App's one NativePlayback. Never owns another libVLC instance or player.</summary>
internal sealed class FilmNativeMedia(NativePlayback native) : IFilmNativeMedia
{
    public FrameworkElement View => native.VideoView;
    public string? Identity => native.OwnerId == "film" ? native.Identity : null;
    public NativePlaybackScope? Scope => native.ObservedVideoOutput is { OwnerId: "film" } output &&
        output.Identity == native.Identity && native.OwnerId == "film"
        ? new(output.OwnerId, output.Identity, output.Generation) : null;
    public bool HasOutput => native.OwnerId == "film" && native.HasMedia && Scope is not null;
    public bool Paused => native.PauseRequested;
    public double Position => native.PositionSeconds;
    public double Duration => native.DurationSeconds;
    public int Volume { get => native.Volume; set => native.Volume = value; }
    public event PropertyChangedEventHandler? PropertyChanged
    { add => native.PropertyChanged += value; remove => native.PropertyChanged -= value; }
    public event EventHandler? Failed { add => native.Failed += value; remove => native.Failed -= value; }
    public event EventHandler? Ended { add => native.Ended += value; remove => native.Ended -= value; }

    public async Task PrepareAsync(RuntimeMediaPlaybackIntent intent, FilmMediaReceipt receipt,
        INativeMediaResourceLease? lease, CancellationToken token)
    {
        NativeVideoInput input;
        try { input = FilmVideoInput.Read(receipt.Format, receipt.Drm); }
        catch
        {
            if (lease is not null) await lease.DisposeAsync();
            throw;
        }
        // Both typed overloads take exclusive ownership on entry, even when preparation rejects.
        await (lease is null ? native.PrepareRuntimeMediaAsync(intent, receipt.Address, input, token)
            : native.PrepareRuntimeMediaAsync(intent, lease, input, token));
    }
    public Task<bool> TryCommitAsync(RuntimeMediaPlaybackIntent intent, Func<bool> canCommit,
        Action committed, CancellationToken token) =>
        native.TryCommitRuntimeMediaAsync(intent, canCommit, committed, token);
    public Task DiscardAsync(RuntimeMediaPlaybackIntent intent) => native.DiscardRuntimeMediaAsync(intent);
    public Task<IAsyncDisposable> OwnOutputAsync(NativePlaybackScope scope, IAsyncDisposable output, CancellationToken token) =>
        native.OwnRuntimeOutputAsync(scope, output, token);
    public Task PauseAsync(NativePlaybackScope scope, bool paused) => native.SetRuntimePausedAsync(scope, paused);
    public Task SeekAsync(NativePlaybackScope scope, double position) => native.SeekRuntimeAsync(scope, position);
    public Task RateAsync(NativePlaybackScope scope, double rate) => native.SetRateAsync(scope, rate);
    public Task AspectAsync(NativePlaybackScope scope, string? aspect) => native.SetRuntimeAspectAsync(scope, aspect);
    public Task StopAsync(NativePlaybackScope scope) => native.StopRuntimeAsync(scope);
}
