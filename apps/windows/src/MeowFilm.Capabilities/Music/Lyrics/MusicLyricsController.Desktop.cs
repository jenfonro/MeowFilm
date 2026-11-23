using MeowFilm.Core.Debug;
using MeowFilm.Desktop;
using Microsoft.UI.Xaml;

namespace MeowFilm.Capabilities;

public sealed partial class MusicLyricsController
{
    private Window? desktopLyricsOwner;
    private Action? activateDesktopLyricsOwner;
    private MusicDesktopLyricsWindow? desktopLyricsWindow;
    private bool desktopLyricsOpening, desktopLyricsReopenAfterClose;
    private long desktopLyricsOpenEpoch;
    private bool showLyricTranslation = true;

    public bool IsDesktopLyricsOpen => desktopLyricsWindow is { IsClosePending: false };
    public bool CanToggleDesktopLyrics => !stopping && !disposed && desktopLyricsOwner is not null;
    public string DesktopLyricsLabel => IsDesktopLyricsOpen ? "关闭桌面歌词" : "打开桌面歌词";
    public bool ShowLyricTranslation => showLyricTranslation;
    internal bool NeedsLyrics => !stopping && (IsLyrics || IsDesktopLyricsOpen);

    public string DesktopLyricText => !GetOwnsCurrent() || !playback.HasMedia ? "未在播放" :
        ActiveLyricIndex is var index && index >= 0 ? Lyrics[index].Text :
        lyricStatus.Length > 0 ? lyricStatus : CanCancelLyrics ? "正在加载歌词" :
        Lyrics.Count > 0 ? "前奏" : "暂无歌词";
    public string DesktopLyricTranslation => showLyricTranslation && GetOwnsCurrent() && playback.HasMedia &&
        ActiveLyricIndex is var index && index >= 0 ? Lyrics[index].Translation : "";
    public MusicLyricRow? DesktopLyricTopRow => DesktopLyricRow(bottom: false);
    public MusicLyricRow? DesktopLyricBottomRow => DesktopLyricRow(bottom: true);

    private MusicLyricRow? DesktopLyricRow(bool bottom)
    {
        var index = ActiveLyricIndex;
        if (!GetOwnsCurrent() || !playback.HasMedia || index < 0) return null;
        if (settings.DesktopLyricLayout != "double") return bottom ? null : Lyrics[index];
        // Each fixed track becomes the next line after its current line finishes.
        var row = (index & 1) == (bottom ? 1 : 0) ? index : index + 1;
        return row < Lyrics.Count ? Lyrics[row] : null;
    }

    public void AttachDesktopLyricsOwner(Window window, Action? activate = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        ctx.Dispatcher.VerifyAccess();
        if (!window.DispatcherQueue.HasThreadAccess) throw new InvalidOperationException("Desktop lyrics owner requires its UI thread.");
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

    public async Task ToggleDesktopLyricsAsync()
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
            await settings.EnsureDesktopLyricSettingsAsync();
            if (epoch != desktopLyricsOpenEpoch || stopping || disposed || !CanToggleDesktopLyrics ||
                !settings.DesktopLyricSettingsLoaded) return;
            var window = CreateDesktopLyricsWindow(desktopLyricsOwner!, settings.DesktopLyricGeometry);
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

    private void DesktopLyricsClosed(object sender, WindowEventArgs e)
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
            _ = ctx.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
    }

    private void DesktopLyricsOwnerClosed(object sender, WindowEventArgs e) => ReleaseDesktopLyrics();

    private void DesktopLyricsBindings()
    {
        Changed(nameof(IsDesktopLyricsOpen));
        Changed(nameof(DesktopLyricsLabel));
        Changed(nameof(CanToggleDesktopLyrics));
        DesktopLyricBindings();
    }

    internal void DesktopLyricBindings()
    {
        Changed(nameof(DesktopLyricText));
        Changed(nameof(DesktopLyricTranslation));
        Changed(nameof(DesktopLyricTopRow));
        Changed(nameof(DesktopLyricBottomRow));
    }

    public void ToggleLyricTranslation()
    {
        showLyricTranslation = !showLyricTranslation;
        Changed(nameof(ShowLyricTranslation));
        Changed(nameof(DesktopLyricTranslation));
    }

    internal void PrepareDesktopLyricsShutdown()
    {
        if (desktopLyricsWindow is { } activeLyrics && settings.DesktopLyricSettingsLoaded &&
            activeLyrics.TryCaptureGeometry(out var geometry))
            _ = settings.SaveDesktopLyricGeometryAsync(geometry);
        desktopLyricsOpenEpoch++;
        desktopLyricsOpening = false;
        desktopLyricsReopenAfterClose = false;
        desktopLyricsWindow?.CloseForShutdown();
        Changed(nameof(CanToggleDesktopLyrics));
    }

    internal void ReleaseDesktopLyrics()
    {
        desktopLyricsOpenEpoch++;
        desktopLyricsOpening = false;
        desktopLyricsReopenAfterClose = false;
        if (desktopLyricsOwner is not null) desktopLyricsOwner.Closed -= DesktopLyricsOwnerClosed;
        desktopLyricsOwner = null;
        activateDesktopLyricsOwner = null;
        CloseDesktopLyrics();
        Changed(nameof(CanToggleDesktopLyrics));
    }

    internal Task ExecuteMediaShortcutAsync(MediaShortcutAction action) => ctx.ExecuteMediaShortcutAsync(action);

    internal object DescribeDebugDesktopLyrics()
    {
        var window = desktopLyricsWindow;
        bool alive = window is { IsWindowClosed: false };
        return new
        {
            supported = CanToggleDesktopLyrics,
            open = IsDesktopLyricsOpen,
            opening = desktopLyricsOpening,
            closePending = window?.IsClosePending == true,
            visible = alive && window!.AppWindow.IsVisible,
            locked = alive && window!.IsLyricsLocked,
            hasUnlockWindow = alive && window!.UnlockWindow is not null,
            layout = settings.DesktopLyricLayout,
            showTranslation = ShowLyricTranslation,
            normalColor = settings.DesktopLyricNormalColor,
            highlightColor = settings.DesktopLyricHighlightColor,
            text = DesktopLyricText,
            translation = DesktopLyricTranslation,
            topLine = DesktopLyricTopRow?.Text,
            bottomLine = DesktopLyricBottomRow?.Text,
            bounds = alive ? window!.DescribeDebugBounds() : null
        };
    }

    internal async Task<object> DebugControlDesktopLyricsAsync(bool? open, bool? locked, string? layout, bool? translation)
    {
        if (open is { } wantOpen)
        {
            if (wantOpen)
            {
                if (!CanToggleDesktopLyrics)
                    throw DebugApiException.InvalidState("Desktop lyrics owner window is not attached.");
                if (desktopLyricsWindow is { IsClosePending: true } closing)
                    await closing.CloseAsync();
                if (!IsDesktopLyricsOpen && !desktopLyricsOpening)
                    await ToggleDesktopLyricsAsync();
            }
            else
            {
                if (desktopLyricsOpening)
                {
                    desktopLyricsOpenEpoch++;
                    desktopLyricsOpening = false;
                }
                if (desktopLyricsWindow is { } active)
                    await active.CloseAsync();
            }
        }

        if (!string.IsNullOrWhiteSpace(layout))
        {
            var normalized = layout.Trim().ToLowerInvariant();
            if (normalized is not ("single" or "double"))
                throw DebugApiException.InvalidArgument($"Unsupported desktop lyrics layout '{layout}' (expected 'single' or 'double').");
            await settings.EnsureDesktopLyricSettingsAsync();
            await settings.SetDesktopLyricLayoutAsync(normalized);
        }

        if (translation is { } wantTranslation && ShowLyricTranslation != wantTranslation)
            ToggleLyricTranslation();

        if (locked is { } wantLocked)
        {
            if (desktopLyricsWindow is not { IsWindowClosed: false, IsClosePending: false } activeWindow)
                throw DebugApiException.InvalidState("Desktop lyrics window is not open.");
            activeWindow.SetLocked(wantLocked);
        }

        return DescribeDebugDesktopLyrics();
    }

    internal async Task<byte[]> CaptureDesktopLyricsWindowPngAsync(CancellationToken cancellationToken)
    {
        if (desktopLyricsWindow is not { IsWindowClosed: false, IsClosePending: false } window)
            throw DebugApiException.InvalidState("Desktop lyrics window is not open.");
        return await DesktopWindowCapture.TryRenderElementPngAsync(window.LyricsSurface, requireVisiblePixels: true, cancellationToken)
            ?? await DesktopWindowCapture.CaptureWindowHandlePngAsync(window.Handle, cancellationToken);
    }
}
