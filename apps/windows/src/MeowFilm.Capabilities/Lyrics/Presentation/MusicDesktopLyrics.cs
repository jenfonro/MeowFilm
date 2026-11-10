using System.Windows;

namespace MeowFilm.Capabilities;

public sealed partial class MusicCapability
{
    private Window? desktopLyricsOwner;
    private MusicDesktopLyricsWindow? desktopLyricsWindow;
    private bool restoreDesktopLyrics;
    private bool showLyricTranslation = true;

    public bool IsDesktopLyricsOpen => desktopLyricsWindow is not null;
    public bool CanToggleDesktopLyrics => !stopping && !disposed && desktopLyricsOwner is not null;
    public string DesktopLyricsLabel => IsDesktopLyricsOpen ? "关闭桌面歌词" : "打开桌面歌词";
    public bool ShowLyricTranslation => showLyricTranslation;
    internal bool NeedsLyrics => !stopping && (IsLyrics || IsDesktopLyricsOpen);
    internal MusicDesktopLyricsWindow? DesktopLyricsWindow => desktopLyricsWindow;
    public string DesktopLyricText => !OwnsCurrent || !playback.HasMedia ? "未在播放" :
        ActiveLyricIndex is var index && index >= 0 ? Lyrics[index].Text :
        lyricStatus.Length > 0 ? lyricStatus : CanCancelLyrics ? "正在加载歌词" :
        Lyrics.Count > 0 ? "前奏" : "暂无歌词";
    public string DesktopLyricTranslation => showLyricTranslation && OwnsCurrent && playback.HasMedia &&
        ActiveLyricIndex is var index && index >= 0 ? Lyrics[index].Translation : "";

    public void AttachDesktopLyricsOwner(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        Presentation.Dispatcher.VerifyAccess();
        window.Dispatcher.VerifyAccess();
        if (stopping || disposed) throw new InvalidOperationException("CLOSED");
        if (ReferenceEquals(desktopLyricsOwner, window)) return;
        if (desktopLyricsOwner is not null) desktopLyricsOwner.Closed -= DesktopLyricsOwnerClosed;
        desktopLyricsOwner = window;
        window.Closed += DesktopLyricsOwnerClosed;
        Changed(nameof(CanToggleDesktopLyrics));
    }

    private async Task ToggleDesktopLyricsAsync()
    {
        if (desktopLyricsWindow is not null) { CloseDesktopLyrics(); return; }
        if (!CanToggleDesktopLyrics) { await Notify("桌面歌词尚未绑定主窗口"); return; }
        var window = new MusicDesktopLyricsWindow(this, desktopLyricsOwner!);
        desktopLyricsWindow = window;
        window.Closed += DesktopLyricsClosed;
        try { window.Show(); }
        catch { CloseDesktopLyrics(); throw; }
        DesktopLyricsBindings();
        await RestoreLyricsAsync();
    }

    private void CloseDesktopLyrics() => desktopLyricsWindow?.Close();

    private void DesktopLyricsClosed(object? sender, EventArgs e)
    {
        var window = (MusicDesktopLyricsWindow)sender!;
        window.Closed -= DesktopLyricsClosed;
        if (!ReferenceEquals(desktopLyricsWindow, window)) return;
        desktopLyricsWindow = null;
        DesktopLyricsBindings();
        if (!NeedsLyrics) CancelLyrics();
    }

    private void DesktopLyricsOwnerClosed(object? sender, EventArgs e) => ReleaseDesktopLyrics();

    private void DesktopLyricsBindings()
    {
        Changed(nameof(IsDesktopLyricsOpen)); Changed(nameof(DesktopLyricsLabel));
        Changed(nameof(CanToggleDesktopLyrics));
        DesktopLyricBindings();
    }

    private void DesktopLyricBindings()
    {
        Changed(nameof(DesktopLyricText)); Changed(nameof(DesktopLyricTranslation));
    }

    private void ToggleLyricTranslation()
    {
        showLyricTranslation = !showLyricTranslation;
        Changed(nameof(ShowLyricTranslation));
        Changed(nameof(DesktopLyricTranslation));
    }

    private void PrepareDesktopLyricsShutdown()
    {
        restoreDesktopLyrics = IsDesktopLyricsOpen;
        CloseDesktopLyrics();
        Changed(nameof(CanToggleDesktopLyrics));
    }

    private async Task ResumeDesktopLyricsAsync()
    {
        bool reopen = restoreDesktopLyrics;
        restoreDesktopLyrics = false;
        if (stopping || disposed || disposal is not null || abortion is not null) return;
        if (reopen && CanToggleDesktopLyrics) await ToggleDesktopLyricsAsync();
        else if (NeedsLyrics) await RestoreLyricsAsync();
        Changed(nameof(CanToggleDesktopLyrics));
    }

    private void ReleaseDesktopLyrics()
    {
        restoreDesktopLyrics = false;
        if (desktopLyricsOwner is not null) desktopLyricsOwner.Closed -= DesktopLyricsOwnerClosed;
        desktopLyricsOwner = null;
        CloseDesktopLyrics();
        Changed(nameof(CanToggleDesktopLyrics));
    }
}
