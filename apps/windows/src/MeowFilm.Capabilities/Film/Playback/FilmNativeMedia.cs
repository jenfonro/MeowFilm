using Microsoft.UI.Xaml;
using System.ComponentModel;
using MeowFilm.Media;

namespace MeowFilm.Capabilities;

/// <summary>Movie-only adapter; source sessions and credentials retain their existing owners.</summary>
internal sealed class FilmNativeMedia(FilmPlayback native) : INotifyPropertyChanged
{
    public FrameworkElement View => native.VideoView;
    public string? Identity => native.Identity;
    public NativePlaybackScope? Scope => native.Scope;
    public bool HasOutput => native.HasOutput;
    public bool Buffering => native.IsBuffering;
    public string? FailureCode => native.LastErrorCode;
    public bool Paused => native.IsPaused;
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
