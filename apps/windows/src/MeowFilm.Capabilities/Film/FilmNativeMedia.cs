using System.ComponentModel;
using System.Windows.Controls;
using MeowFilm.Media;

namespace MeowFilm.Capabilities;

internal interface IFilmNativeMedia : INotifyPropertyChanged
{
    ContentControl? View { get; }
    NativePlaybackScope? Scope { get; }
    string? Identity { get; }
    bool HasOutput { get; }
    bool Buffering { get; }
    string? FailureCode { get; }
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
    Task PauseAsync(NativePlaybackScope scope, bool paused);
    Task SeekAsync(NativePlaybackScope scope, double position);
    Task RateAsync(NativePlaybackScope scope, double rate);
    Task AspectAsync(NativePlaybackScope scope, string? aspect);
    Task StopAsync(NativePlaybackScope scope);
}

/// <summary>Movie-only adapter; source sessions and credentials retain their existing owners.</summary>
internal sealed class FilmNativeMedia(FilmPlayback native) : IFilmNativeMedia
{
    public ContentControl View => native.VideoView;
    public string? Identity => native.Identity;
    public NativePlaybackScope? Scope => native.Scope;
    public bool HasOutput => native.HasOutput;
    public bool Buffering => native.IsBuffering;
    public string? FailureCode => native.LastErrorCode;
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
        await native.PrepareAsync(intent, receipt.Address, input, lease, token);
    }
    public Task<bool> TryCommitAsync(RuntimeMediaPlaybackIntent intent, Func<bool> canCommit,
        Action committed, CancellationToken token) =>
        native.TryCommitAsync(intent, canCommit, committed, token);
    public Task DiscardAsync(RuntimeMediaPlaybackIntent intent) => native.DiscardAsync(intent);
    public Task PauseAsync(NativePlaybackScope scope, bool paused) => native.SetPausedAsync(scope, paused);
    public Task SeekAsync(NativePlaybackScope scope, double position) => native.SeekAsync(scope, position);
    public Task RateAsync(NativePlaybackScope scope, double rate) => native.RateAsync(scope, rate);
    public Task AspectAsync(NativePlaybackScope scope, string? aspect) => native.AspectAsync(scope, aspect);
    public Task StopAsync(NativePlaybackScope scope) => native.StopAsync(scope);
}
