using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MeowFilm.Media;

/// <summary>The picture and controls are siblings in the application's composition tree.</summary>
public sealed class FilmVideoSurface : Grid, IDisposable
{
    private const int MaxDimension = 16384;
    private const nuint SubclassId = 0x4D465356; // "MFSV"
    private const uint WmDisplayChange = 0x007E;
    private const uint WmDpiChanged = 0x02E0;
    private const uint WmSettingChange = 0x001A;
    private const uint WmNcDestroy = 0x0082;

    private readonly DispatcherQueue uiQueue;
    private readonly SwapChainPanel picture = new();
    private readonly SubclassProc subclassCallback;
    private UIElement? controls;
    private XamlRoot? root;
    private AppWindow? hostWindow;
    private nint subclassHwnd;
    private DisplayId lastDisplayId;
    private DisplayAreaWatcher? displayWatcher;
    private nint swapchain;
    private long displayEpoch;
    private volatile bool closed;
    private TaskCompletionSource mounted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FilmVideoSurface()
    {
        uiQueue = DispatcherQueue.GetForCurrentThread();
        subclassCallback = OnWindowMessage;
        Background = new SolidColorBrush(Colors.Black);
        AutomationProperties.SetName(this, "视频画面");
        Children.Add(picture);
        Loaded += SurfaceLoadedChanged;
        Unloaded += SurfaceLoadedChanged;
        SizeChanged += SizeChangedSurface;
        picture.CompositionScaleChanged += ScaleChangedSurface;
        StartDisplayWatcher();
    }

    public UIElement? Content
    {
        get => controls;
        set
        {
            VerifyAccess();
            if (ReferenceEquals(controls, value)) return;
            if (controls is not null) Children.Remove(controls);
            controls = value;
            if (value is not null) Children.Add(value);
        }
    }

    internal event EventHandler? TargetChanged;
    internal long DisplayEpoch => Volatile.Read(ref displayEpoch);

    internal nint DisplayWindow
    {
        get
        {
            if (closed || !IsLoaded || XamlRoot is not { } current) return 0;
            try
            {
                var windowId = current.ContentIslandEnvironment.AppWindowId;
                return windowId.Value == 0 ? 0 : Win32Interop.GetWindowFromWindowId(windowId);
            }
            catch
            {
                return 0;
            }
        }
    }

    internal FilmKernelNative.Surface Describe()
    {
        float scaleX = float.IsFinite(picture.CompositionScaleX) && picture.CompositionScaleX > 0
            ? picture.CompositionScaleX : 1f;
        float scaleY = float.IsFinite(picture.CompositionScaleY) && picture.CompositionScaleY > 0
            ? picture.CompositionScaleY : 1f;
        nint hwnd = DisplayWindow;
        bool visible = !closed && hwnd != 0 && IsLoaded && XamlRoot?.IsHostVisible == true &&
            ActualWidth > 0 && ActualHeight > 0;
        int width = visible ? Math.Clamp((int)Math.Round(ActualWidth * scaleX), 1, MaxDimension) : 0;
        int height = visible ? Math.Clamp((int)Math.Round(ActualHeight * scaleY), 1, MaxDimension) : 0;
        return new()
        {
            Size = (uint)Marshal.SizeOf<FilmKernelNative.Surface>(),
            Abi = FilmKernelNative.Abi,
            DisplayWindow = hwnd,
            Width = width,
            Height = height,
            ScaleX = scaleX,
            ScaleY = scaleY,
            Visible = visible ? 1 : 0,
            Reserved = 0
        };
    }

    internal async Task WaitForTargetAsync(CancellationToken token)
    {
        VerifyAccess();
        while (!closed && Describe() is not { DisplayWindow: not 0, Visible: 1 })
            await mounted.Task.WaitAsync(token);
        if (closed) throw new PlaybackException(PlaybackError.Disposed);
    }

    // The caller transfers the AddRef returned by mfk_attach, including on failure.
    internal void BindSwapchain(nint value)
    {
        VerifyAccess();
        try
        {
            SetSwapchain(value);
            var previous = swapchain;
            swapchain = value;
            value = 0;
            if (previous != 0) Marshal.Release(previous);
        }
        finally { if (value != 0) Marshal.Release(value); }
    }

    private void SetSwapchain(nint value)
    {
        var identity = new Guid("63aad0b8-7c24-40ff-85a8-640d944cc325");
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(
            ((WinRT.IWinRTObject)picture).NativeObject.ThisPtr, in identity, out var panel));
        try
        {
            var table = Marshal.ReadIntPtr(panel);
            var set = Marshal.GetDelegateForFunctionPointer<SetSwapchainCallback>(
                Marshal.ReadIntPtr(table, 3 * IntPtr.Size));
            Marshal.ThrowExceptionForHR(set(panel, value));
        }
        finally { Marshal.Release(panel); }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetSwapchainCallback(nint panel, nint swapchain);

    private void StartDisplayWatcher()
    {
        try
        {
            displayWatcher = DisplayArea.CreateWatcher();
            displayWatcher.Updated += DisplayTopologyChanged;
            displayWatcher.Added += DisplayTopologyChanged;
            displayWatcher.Removed += DisplayTopologyChanged;
            displayWatcher.Start();
        }
        catch
        {
            displayWatcher = null;
        }
    }

    private void DisplayTopologyChanged(DisplayAreaWatcher sender, DisplayArea args)
    {
        if (closed) return;
        Interlocked.Increment(ref displayEpoch);
        uiQueue.TryEnqueue(() =>
        {
            if (!closed) Changed(bumpDisplayEpoch: false);
        });
    }

    private nint OnWindowMessage(nint hwnd, uint msg, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (msg == WmNcDestroy)
        {
            DetachWindowSubclass();
        }
        else if (!closed && msg is WmDisplayChange or WmDpiChanged or WmSettingChange)
        {
            // System HDR toggle broadcasts WM_DISPLAYCHANGE/WM_SETTINGCHANGE even when display geometry is unchanged.
            // Never let a WinRT exception escape the native comctl32 SubclassProc frame.
            try { Changed(bumpDisplayEpoch: true); } catch { }
        }
        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    private void AttachWindowSubclass(nint hwnd)
    {
        if (hwnd == subclassHwnd) return;
        DetachWindowSubclass();
        if (hwnd != 0 && SetWindowSubclass(hwnd, subclassCallback, SubclassId, 0))
            subclassHwnd = hwnd;
    }

    private void DetachWindowSubclass()
    {
        nint hwnd = subclassHwnd;
        if (hwnd == 0) return;
        subclassHwnd = 0;
        try { RemoveWindowSubclass(hwnd, subclassCallback, SubclassId); } catch { }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate nint SubclassProc(nint hwnd, uint msg, nuint wParam, nint lParam, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint hwnd, SubclassProc callback, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc callback, nuint id);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hwnd, uint msg, nuint wParam, nint lParam);

    private void SurfaceLoadedChanged(object sender, RoutedEventArgs args) => Changed(bumpDisplayEpoch: true);
    private void SizeChangedSurface(object sender, SizeChangedEventArgs args) => Changed(bumpDisplayEpoch: false);
    private void ScaleChangedSurface(SwapChainPanel sender, object args) => Changed(bumpDisplayEpoch: true);
    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => Changed(bumpDisplayEpoch: true);

    private void AppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (closed) return;
        if (args.DidPositionChange)
        {
            try
            {
                var currentDisplay = DisplayArea.GetFromWindowId(sender.Id, DisplayAreaFallback.Nearest).DisplayId;
                if (currentDisplay.Value != lastDisplayId.Value)
                {
                    lastDisplayId = currentDisplay;
                    Changed(bumpDisplayEpoch: true);
                    return;
                }
            }
            catch { }
        }
        if (args.DidVisibilityChange || args.DidPresenterChange)
            Changed(bumpDisplayEpoch: true);
    }

    private void AttachHostWindow(XamlRoot? currentRoot)
    {
        AppWindow? nextWindow = null;
        if (currentRoot is not null)
        {
            try
            {
                var windowId = currentRoot.ContentIslandEnvironment.AppWindowId;
                if (windowId.Value != 0) nextWindow = AppWindow.GetFromWindowId(windowId);
            }
            catch { nextWindow = null; }
        }
        if (ReferenceEquals(hostWindow, nextWindow)) return;
        if (hostWindow is not null)
        {
            try { hostWindow.Changed -= AppWindowChanged; } catch { }
        }
        hostWindow = nextWindow;
        if (hostWindow is not null)
        {
            try
            {
                lastDisplayId = DisplayArea.GetFromWindowId(hostWindow.Id, DisplayAreaFallback.Nearest).DisplayId;
                hostWindow.Changed += AppWindowChanged;
            }
            catch { }
        }
    }

    private void Changed(bool bumpDisplayEpoch)
    {
        if (closed) return;
        if (bumpDisplayEpoch) Interlocked.Increment(ref displayEpoch);
        var current = IsLoaded ? XamlRoot : null;
        if (!ReferenceEquals(root, current))
        {
            if (root is not null) root.Changed -= RootChanged;
            root = current;
            if (root is not null) root.Changed += RootChanged;
        }
        AttachHostWindow(root);
        AttachWindowSubclass(DisplayWindow);
        var previous = mounted;
        mounted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult();
        TargetChanged?.Invoke(this, EventArgs.Empty);
    }

    private void VerifyAccess()
    {
        if (!uiQueue.HasThreadAccess) throw new PlaybackException(PlaybackError.WrongThread);
    }

    public void Dispose()
    {
        VerifyAccess();
        if (closed) return;
        closed = true;
        DetachWindowSubclass();
        try
        {
            BindSwapchain(0);
        }
        catch
        {
            if (swapchain != 0)
            {
                try { Marshal.Release(swapchain); } catch { }
                swapchain = 0;
            }
        }
        finally
        {
            Loaded -= SurfaceLoadedChanged;
            Unloaded -= SurfaceLoadedChanged;
            SizeChanged -= SizeChangedSurface;
            picture.CompositionScaleChanged -= ScaleChangedSurface;
            if (root is not null)
            {
                try { root.Changed -= RootChanged; } catch { }
                root = null;
            }
            if (hostWindow is not null)
            {
                try { hostWindow.Changed -= AppWindowChanged; } catch { }
                hostWindow = null;
            }
            if (displayWatcher is not null)
            {
                try
                {
                    displayWatcher.Updated -= DisplayTopologyChanged;
                    displayWatcher.Added -= DisplayTopologyChanged;
                    displayWatcher.Removed -= DisplayTopologyChanged;
                    displayWatcher.Stop();
                }
                catch { }
                displayWatcher = null;
            }
            try { Content = null; } catch { }
            mounted.TrySetResult();
        }
    }
}
