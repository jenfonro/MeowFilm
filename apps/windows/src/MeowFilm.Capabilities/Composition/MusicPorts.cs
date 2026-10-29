using System.ComponentModel;
using System.Windows;
using MeowFilm.Media;
using Microsoft.Win32;

namespace MeowFilm.Capabilities;

// Module-level seams for deterministic UI orchestration tests. Production always
// wraps the App's one shared NativePlayback; this is not another media engine.
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

internal sealed class NativeMusicPlayback(NativePlayback native) : IMusicPlayback, INativePlaybackContinuation, IMusicPlaybackRate
{
    public async Task<MusicPlaybackRate?> ReadRateAsync(CancellationToken token = default)
    {
        var tracks = await native.ReadTracksAsync(token);
        return tracks is null ? null : new(tracks.Scope, tracks.Rate);
    }
    public async Task<MusicPlaybackRate> SetRateAsync(NativePlaybackScope scope, double rate, CancellationToken token = default)
    {
        await native.SetRateAsync(scope, rate, token);
        var actual = await ReadRateAsync(token);
        if (actual is null || actual.Scope != scope) throw new MusicOperationException(PlaybackError.Superseded);
        return actual;
    }
    public bool PauseRequested => native.PauseRequested;
    public string? LastErrorCode => native.LastErrorCode;
    public NativePlaybackContinuation? CaptureContinuation() => native.CaptureContinuation();
    public Task ReopenAsync(NativePlaybackContinuation continuation, Uri uri,
        IReadOnlyDictionary<string, string> headers, CancellationToken token = default) =>
        native.ReopenAsync(continuation, uri, headers, token);
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
    bool Confirm(string message, string title);
    string[]? SelectAudioFiles();
    string[]? SelectScriptFiles() => SelectScriptFile() is { } path ? [path] : null;
    string? SelectScriptFile() => null;
    string? SelectLyricFile() => null;
}

internal sealed class MusicDialogs : IMusicDialogs
{
    public bool Confirm(string message, string title) =>
        MessageBox.Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;
    public string[]? SelectAudioFiles()
    {
        var dialog = new OpenFileDialog { Multiselect = true, CheckFileExists = true, Title = "添加音乐",
            Filter = "音频文件|*.mp3;*.flac;*.m4a;*.aac;*.ogg;*.opus;*.wav;*.wma" };
        return dialog.ShowDialog() == true ? dialog.FileNames : null;
    }
    public string[]? SelectScriptFiles()
    {
        var dialog = new OpenFileDialog { CheckFileExists = true, Multiselect = true,
            Title = "导入 LX 音源", Filter = "JavaScript|*.js" };
        return dialog.ShowDialog() == true ? dialog.FileNames : null;
    }
    public string? SelectScriptFile() => Select("更新 LX 音源", "JavaScript|*.js");
    public string? SelectLyricFile() => Select("导入歌词", "歌词文件|*.lrc;*.qrc;*.yrc;*.txt");
    private static string? Select(string title, string filter)
    {
        var dialog = new OpenFileDialog { CheckFileExists = true, Multiselect = false, Title = title, Filter = filter };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
