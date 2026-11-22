using System.ComponentModel;
using Microsoft.UI.Xaml;
using MeowFilm.Desktop;
using MeowFilm.Media;

namespace MeowFilm.Capabilities;

// Module-level seams for deterministic UI orchestration tests. Production wraps
// the App-owned independent native audio player.
public interface IMusicPlayback : INotifyPropertyChanged
{
    string? OwnerId { get; }
    string? Identity { get; }
    bool HasMedia { get; }
    bool IsPlaying { get; }
    double PositionSeconds { get; }
    double DurationSeconds { get; }
    int Volume { get; set; }
    event EventHandler? Started;
    event EventHandler? Ended;
    event EventHandler? Failed;
    Task OpenAsync(string owner, string identity, Uri uri, string title, CancellationToken token);
    Task OpenAsync(string owner, string identity, Uri uri, string title,
        IReadOnlyDictionary<string, string> headers, CancellationToken token) =>
        headers.Count == 0 ? OpenAsync(owner, identity, uri, title, token)
            : Task.FromException(new NotSupportedException("MEDIA_HEADERS_UNSUPPORTED"));
    async Task OpenReadAsync(string owner, string identity, INativeMediaReadLease lease, string title,
        CancellationToken token = default)
    {
        await lease.DisposeAsync();
        throw new NotSupportedException("MEDIA_READ_UNSUPPORTED");
    }
    Task PauseAsync();
    Task ResumeAsync();
    Task StopAsync();
    Task SeekAsync(double seconds);
}

// Rate support is optional for injected engines; it must never silently succeed.
// The scope fences commands from an old song or a replaced native input.
public sealed record MusicPlaybackRate(NativePlaybackScope Scope, double RequestedRate);
public interface IMusicPlaybackRate
{
    Task<MusicPlaybackRate?> ReadRateAsync(CancellationToken token = default);
    Task<MusicPlaybackRate> SetRateAsync(NativePlaybackScope scope, double rate, CancellationToken token = default);
}

internal sealed class NativeMusicPlayback(NativeAudioPlayback native) : IMusicPlayback, INativePlaybackContinuation,
    IMusicPlaybackRate, INativePlaybackHandoff
{
    public Task StopOutputForHandoffAsync() => native.StopOutputForHandoffAsync();
    public Task PrepareHandoffAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken token) =>
        native.PrepareHandoffAsync(uri, headers, token);
    public Task PrepareReadHandoffAsync(INativeMediaReadLease lease, CancellationToken token) => native.PrepareReadHandoffAsync(lease, token);
    public double PreparedHandoffDurationSeconds => native.PreparedHandoffDurationSeconds;
    public Task CommitHandoffAsync(Action committed, CancellationToken token) => native.CommitHandoffAsync(committed, token);
    public Task DiscardHandoffAsync() => native.DiscardHandoffAsync();
    public async Task<MusicPlaybackRate?> ReadRateAsync(CancellationToken token = default)
    {
        var rate = await native.ReadRateAsync(token);
        return rate is null ? null : new(rate.Scope, rate.RequestedRate);
    }
    public async Task<MusicPlaybackRate> SetRateAsync(NativePlaybackScope scope, double rate, CancellationToken token = default)
    {
        var actual = await native.SetRateAsync(scope, rate, token);
        return new(actual.Scope, actual.RequestedRate);
    }
    public bool PauseRequested => native.PauseRequested;
    public string? LastErrorCode => native.LastErrorCode;
    public NativePlaybackContinuation? CaptureContinuation() => native.CaptureContinuation();
    public Task ReopenAsync(NativePlaybackContinuation continuation, Uri uri,
        IReadOnlyDictionary<string, string> headers, CancellationToken token = default) =>
        native.ReopenAsync(continuation, uri, headers, token);
    public Task ReopenReadAsync(NativePlaybackContinuation continuation, INativeMediaReadLease lease,
        CancellationToken token = default) => native.ReopenReadAsync(continuation, lease, token);
    public string? OwnerId => native.OwnerId;
    public string? Identity => native.Identity;
    public bool HasMedia => native.HasMedia;
    public bool IsPlaying => native.IsPlaying;
    public double PositionSeconds => native.PositionSeconds;
    public double DurationSeconds => native.DurationSeconds;
    public int Volume { get => native.Volume; set => native.Volume = value; }
    public event PropertyChangedEventHandler? PropertyChanged { add => native.PropertyChanged += value; remove => native.PropertyChanged -= value; }
    public event EventHandler? Started { add => native.Started += value; remove => native.Started -= value; }
    public event EventHandler? Ended { add => native.Ended += value; remove => native.Ended -= value; }
    public event EventHandler? Failed { add => native.Failed += value; remove => native.Failed -= value; }
    public Task OpenAsync(string owner, string identity, Uri uri, string title, CancellationToken token) => native.OpenAsync(owner, identity, uri, title, token);
    public Task OpenAsync(string owner, string identity, Uri uri, string title,
        IReadOnlyDictionary<string, string> headers, CancellationToken token) =>
        native.OpenAsync(owner, identity, uri, title, headers, token);
    public Task OpenReadAsync(string owner, string identity, INativeMediaReadLease lease, string title,
        CancellationToken token = default) => native.OpenReadAsync(owner, identity, lease, title, token);
    public Task PauseAsync() => native.PauseAsync();
    public Task ResumeAsync() => native.ResumeAsync();
    public Task StopAsync() => native.StopAsync();
    public Task SeekAsync(double seconds) => native.SeekAsync(seconds);
}

public interface IMusicDialogs
{
    Task<bool> ConfirmAsync(FrameworkElement owner, string message, string title, CancellationToken cancellationToken = default);
    Task<string[]?> SelectAudioFilesAsync(FrameworkElement owner);
    Task<string[]?> SelectScriptFilesAsync(FrameworkElement owner) => Task.FromResult<string[]?>(null);
    Task<string?> SelectScriptFileAsync(FrameworkElement owner) => Task.FromResult<string?>(null);
    Task<string?> SelectLyricFileAsync(FrameworkElement owner) => Task.FromResult<string?>(null);
}

internal sealed class MusicDialogs : IMusicDialogs
{
    public async Task<bool> ConfirmAsync(FrameworkElement owner, string message, string title, CancellationToken cancellationToken = default)
    {
        var request = new DesktopDialogRequest
        {
            Title = title,
            Message = message,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = DesktopDialogDefaultButton.Close
        };
        return await DesktopModalHost.ShowDialogAsync(owner, request, cancellationToken, stack: true) == DesktopDialogResult.Primary;
    }
    public Task<string[]?> SelectAudioFilesAsync(FrameworkElement owner) =>
        CapabilityFilePicker.OpenMultipleAsync(owner, "添加音乐", ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".wma");
    public Task<string[]?> SelectScriptFilesAsync(FrameworkElement owner) =>
        CapabilityFilePicker.OpenMultipleAsync(owner, "导入 LX 音源", ".js");
    public Task<string?> SelectScriptFileAsync(FrameworkElement owner) =>
        CapabilityFilePicker.OpenAsync(owner, "更新 LX 音源", ".js");
    public Task<string?> SelectLyricFileAsync(FrameworkElement owner) =>
        CapabilityFilePicker.OpenAsync(owner, "导入歌词", ".lrc", ".qrc", ".yrc", ".txt");
}
