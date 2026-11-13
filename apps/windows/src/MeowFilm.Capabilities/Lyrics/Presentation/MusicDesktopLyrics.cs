using System.Windows;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

public sealed partial class MusicCapability
{
    private Window? desktopLyricsOwner;
    private Action? activateDesktopLyricsOwner;
    private MusicDesktopLyricsWindow? desktopLyricsWindow;
    private MusicDesktopLyricsWindowState? restoreDesktopLyrics;
    private bool desktopLyricsOpening, desktopLyricsReopenAfterClose;
    private long desktopLyricsOpenEpoch;
    private bool showLyricTranslation = true;

    public bool IsDesktopLyricsOpen => desktopLyricsWindow is { IsClosePending: false };
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
    public MusicLyricRow? DesktopLyricTopRow => DesktopLyricRow(bottom: false);
    public MusicLyricRow? DesktopLyricBottomRow => DesktopLyricRow(bottom: true);

    private MusicLyricRow? DesktopLyricRow(bool bottom)
    {
        var index = ActiveLyricIndex;
        if (!OwnsCurrent || !playback.HasMedia || index < 0) return null;
        if (DesktopLyricLayout != "double") return bottom ? null : Lyrics[index];
        // Each fixed track becomes the next line after its current line finishes.
        var row = (index & 1) == (bottom ? 1 : 0) ? index : index + 1;
        return row < Lyrics.Count ? Lyrics[row] : null;
    }

    public void AttachDesktopLyricsOwner(Window window, Action? activate = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        Presentation.Dispatcher.VerifyAccess();
        window.Dispatcher.VerifyAccess();
        if (stopping || disposed) throw new InvalidOperationException("CLOSED");
        activateDesktopLyricsOwner = activate;
        if (ReferenceEquals(desktopLyricsOwner, window)) return;
        if (desktopLyricsOwner is not null) desktopLyricsOwner.Closed -= DesktopLyricsOwnerClosed;
        desktopLyricsOwner = window;
        window.Closed += DesktopLyricsOwnerClosed;
        Changed(nameof(CanToggleDesktopLyrics));
    }

    public void ActivateDesktopLyricsOwner()
    {
        if (CanToggleDesktopLyrics) activateDesktopLyricsOwner?.Invoke();
    }

    private async Task ToggleDesktopLyricsAsync(MusicDesktopLyricsWindowState? restore = null)
    {
        if (desktopLyricsWindow is { } active)
        {
            if (active.IsClosePending)
            {
                desktopLyricsReopenAfterClose = !desktopLyricsReopenAfterClose;
                return;
            }
            await active.CloseAsync();
            return;
        }
        if (desktopLyricsOpening)
        {
            desktopLyricsOpenEpoch++;
            desktopLyricsOpening = false;
            return;
        }
        if (!CanToggleDesktopLyrics) { await Notify("桌面歌词尚未绑定主窗口"); return; }
        desktopLyricsOpening = true;
        long epoch = ++desktopLyricsOpenEpoch;
        try
        {
            await EnsureDesktopLyricSettingsAsync();
            if (epoch != desktopLyricsOpenEpoch || stopping || disposed || !CanToggleDesktopLyrics ||
                !desktopLyricSettingsLoaded) return;
            var window = new MusicDesktopLyricsWindow(this, desktopLyricsOwner!,
                restore is { } savedState ? savedState.Geometry : desktopLyricGeometry);
            if (restore is { } state) window.RestorePresentationState(state);
            desktopLyricsWindow = window;
            window.Closed += DesktopLyricsClosed;
            try { window.Show(); }
            catch { CloseDesktopLyrics(); throw; }
            desktopLyricsOpening = false;
            DesktopLyricsBindings();
            await RestoreLyricsAsync();
        }
        finally
        {
            if (epoch == desktopLyricsOpenEpoch) desktopLyricsOpening = false;
        }
    }

    private void CloseDesktopLyrics() => desktopLyricsWindow?.Close();

    internal void DesktopLyricsClosing()
    {
        DesktopLyricsBindings();
        if (!NeedsLyrics) CancelLyrics();
    }

    private void DesktopLyricsClosed(object? sender, EventArgs e)
    {
        var window = (MusicDesktopLyricsWindow)sender!;
        window.Closed -= DesktopLyricsClosed;
        if (!ReferenceEquals(desktopLyricsWindow, window)) return;
        desktopLyricsWindow = null;
        bool reopen = desktopLyricsReopenAfterClose;
        desktopLyricsReopenAfterClose = false;
        DesktopLyricsBindings();
        if (!NeedsLyrics) CancelLyrics();
        if (reopen && CanToggleDesktopLyrics)
            _ = ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
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
        Changed(nameof(DesktopLyricTopRow)); Changed(nameof(DesktopLyricBottomRow));
    }

    private void ToggleLyricTranslation()
    {
        showLyricTranslation = !showLyricTranslation;
        Changed(nameof(ShowLyricTranslation));
        Changed(nameof(DesktopLyricTranslation));
    }

    private void PrepareDesktopLyricsShutdown(MusicDesktopLyricsWindowState? state)
    {
        desktopLyricsReopenAfterClose = false;
        restoreDesktopLyrics = state;
        desktopLyricsWindow?.CloseForShutdown();
        Changed(nameof(CanToggleDesktopLyrics));
    }

    private async Task ResumeDesktopLyricsAsync()
    {
        var restore = restoreDesktopLyrics;
        restoreDesktopLyrics = null;
        if (stopping || disposed || disposal is not null || abortion is not null) return;
        if (restore is { } state && CanToggleDesktopLyrics)
        {
            await ToggleDesktopLyricsAsync(state);
        }
        else if (NeedsLyrics) await RestoreLyricsAsync();
        Changed(nameof(CanToggleDesktopLyrics));
    }

    private void ReleaseDesktopLyrics()
    {
        desktopLyricsOpenEpoch++;
        desktopLyricsOpening = false;
        desktopLyricsReopenAfterClose = false;
        restoreDesktopLyrics = null;
        if (desktopLyricsOwner is not null) desktopLyricsOwner.Closed -= DesktopLyricsOwnerClosed;
        desktopLyricsOwner = null;
        activateDesktopLyricsOwner = null;
        CloseDesktopLyrics();
        Changed(nameof(CanToggleDesktopLyrics));
    }
}
