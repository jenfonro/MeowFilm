using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using MeowFilm.Core.Debug;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using MeowFilm.Desktop;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

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
    internal MusicDesktopLyricsWindow? DesktopLyricsWindow => desktopLyricsWindow;

    public string DesktopLyricText => !host.Playback.OwnsCurrent || !playback.HasMedia ? "未在播放" :
        ActiveLyricIndex is var index && index >= 0 ? Lyrics[index].Text :
        lyricStatus.Length > 0 ? lyricStatus : CanCancelLyrics ? "正在加载歌词" :
        Lyrics.Count > 0 ? "前奏" : "暂无歌词";
    public string DesktopLyricTranslation => showLyricTranslation && host.Playback.OwnsCurrent && playback.HasMedia &&
        ActiveLyricIndex is var index && index >= 0 ? Lyrics[index].Translation : "";
    public MusicLyricRow? DesktopLyricTopRow => DesktopLyricRow(bottom: false);
    public MusicLyricRow? DesktopLyricBottomRow => DesktopLyricRow(bottom: true);

    private MusicLyricRow? DesktopLyricRow(bool bottom)
    {
        var index = ActiveLyricIndex;
        if (!host.Playback.OwnsCurrent || !playback.HasMedia || index < 0) return null;
        if (host.Settings.DesktopLyricLayout != "double") return bottom ? null : Lyrics[index];
        // Each fixed track becomes the next line after its current line finishes.
        var row = (index & 1) == (bottom ? 1 : 0) ? index : index + 1;
        return row < Lyrics.Count ? Lyrics[row] : null;
    }

    public void AttachDesktopLyricsOwner(Window window, Action? activate = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        Presentation.Dispatcher.VerifyAccess();
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

    public Task ToggleDesktopLyricsAsync() => ToggleDesktopLyricsCoreAsync(null);

    private async Task ToggleDesktopLyricsCoreAsync(MusicDesktopLyricsWindowState? restore)
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
            await host.Settings.EnsureDesktopLyricSettingsAsync();
            if (epoch != desktopLyricsOpenEpoch || stopping || disposed || !CanToggleDesktopLyrics ||
                !host.Settings.DesktopLyricSettingsLoaded) return;
            var window = new MusicDesktopLyricsWindow(this, host.Playback, host.Settings, desktopLyricsOwner!,
                restore is { } savedState ? savedState.Geometry : host.Settings.DesktopLyricGeometry);
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
            _ = host.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleDesktopLyrics);
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
        if (desktopLyricsWindow is { } activeLyrics)
        {
            var state = activeLyrics.CapturePresentationState();
            if (host.Settings.DesktopLyricSettingsLoaded && state.Geometry is { } geometry)
                _ = activeLyrics.SaveGeometryForShutdown(geometry);
        }
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

    internal Task ExecuteMediaShortcutAsync(MediaShortcutAction action) => host.ExecuteMediaShortcutAsync(action);

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
            layout = host.Settings.DesktopLyricLayout,
            showTranslation = ShowLyricTranslation,
            normalColor = host.Settings.DesktopLyricNormalColor,
            highlightColor = host.Settings.DesktopLyricHighlightColor,
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
            await host.Settings.EnsureDesktopLyricSettingsAsync();
            await host.Settings.SetDesktopLyricLayoutAsync(normalized);
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

        try
        {
            var surface = window.LyricsSurface;
            if (surface.IsLoaded && surface.ActualWidth > 0 && surface.ActualHeight > 0)
            {
                var rtb = new RenderTargetBitmap();
                await rtb.RenderAsync(surface);
                var buffer = await rtb.GetPixelsAsync();
                var pixels = buffer.ToArray();
                if (rtb.PixelWidth > 0 && rtb.PixelHeight > 0 && HasVisiblePixels(pixels))
                {
                    using var ras = new InMemoryRandomAccessStream();
                    var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, ras);
                    encoder.SetPixelData(
                        BitmapPixelFormat.Bgra8,
                        BitmapAlphaMode.Premultiplied,
                        (uint)rtb.PixelWidth,
                        (uint)rtb.PixelHeight,
                        96, 96, pixels);
                    await encoder.FlushAsync();
                    ras.Seek(0);
                    using var stream = ras.AsStreamForRead();
                    using var ms = new MemoryStream();
                    await stream.CopyToAsync(ms, cancellationToken);
                    return ms.ToArray();
                }
            }
        }
        catch { }

        return await CaptureWindowHandlePngAsync(window.Handle, cancellationToken);
    }

    private static bool HasVisiblePixels(byte[] pixels)
    {
        for (int i = 3; i < pixels.Length; i += 4)
            if (pixels[i] != 0) return true;
        return false;
    }

    private static async Task<byte[]> CaptureWindowHandlePngAsync(nint hwnd, CancellationToken cancellationToken)
    {
        if (!MusicDesktopLyricsNative.GetWindowRect(hwnd, out var rect))
            throw DebugApiException.Internal("Unable to query desktop lyrics window bounds for screenshot.");
        int width = Math.Max(1, rect.Right - rect.Left);
        int height = Math.Max(1, rect.Bottom - rect.Top);
        nint screenDc = DebugCaptureNative.GetDC(0);
        if (screenDc == 0)
            throw DebugApiException.Internal("GetDC failed while capturing desktop lyrics screenshot.");
        nint memDc = DebugCaptureNative.CreateCompatibleDC(screenDc);
        if (memDc == 0)
        {
            DebugCaptureNative.ReleaseDC(0, screenDc);
            throw DebugApiException.Internal("CreateCompatibleDC failed while capturing desktop lyrics screenshot.");
        }
        var bmi = new DebugCaptureNative.BitmapInfoHeader
        {
            biSize = (uint)Marshal.SizeOf<DebugCaptureNative.BitmapInfoHeader>(),
            biWidth = width,
            biHeight = -height,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0
        };
        nint dib = DebugCaptureNative.CreateDIBSection(memDc, ref bmi, 0, out nint bits, 0, 0);
        if (dib == 0 || bits == 0)
        {
            if (dib != 0) DebugCaptureNative.DeleteObject(dib);
            DebugCaptureNative.DeleteDC(memDc);
            DebugCaptureNative.ReleaseDC(0, screenDc);
            throw DebugApiException.Internal("CreateDIBSection failed while capturing desktop lyrics screenshot.");
        }
        nint old = DebugCaptureNative.SelectObject(memDc, dib);
        try
        {
            DebugCaptureNative.PrintWindow(hwnd, memDc, 0x00000002); // PW_RENDERFULLCONTENT
            var pixels = new byte[width * height * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            using var ras = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, ras);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, pixels);
            await encoder.FlushAsync();
            ras.Seek(0);
            using var stream = ras.AsStreamForRead();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, cancellationToken);
            return ms.ToArray();
        }
        finally
        {
            DebugCaptureNative.SelectObject(memDc, old);
            DebugCaptureNative.DeleteObject(dib);
            DebugCaptureNative.DeleteDC(memDc);
            DebugCaptureNative.ReleaseDC(0, screenDc);
        }
    }

    private static class DebugCaptureNative
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct BitmapInfoHeader
        {
            internal uint biSize;
            internal int biWidth, biHeight;
            internal ushort biPlanes, biBitCount;
            internal uint biCompression, biSizeImage;
            internal int biXPelsPerMeter, biYPelsPerMeter;
            internal uint biClrUsed, biClrImportant;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PrintWindow(nint hwnd, nint hdcBlt, uint nFlags);

        [DllImport("user32.dll")]
        internal static extern nint GetDC(nint hWnd);

        [DllImport("user32.dll")]
        internal static extern int ReleaseDC(nint hWnd, nint hDC);

        [DllImport("gdi32.dll")]
        internal static extern nint CreateCompatibleDC(nint hdc);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteDC(nint hdc);

        [DllImport("gdi32.dll")]
        internal static extern nint CreateDIBSection(nint hdc, ref BitmapInfoHeader pbmi, uint usage,
            out nint ppvBits, nint hSection, uint offset);

        [DllImport("gdi32.dll")]
        internal static extern nint SelectObject(nint hdc, nint h);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteObject(nint ho);
    }
}
