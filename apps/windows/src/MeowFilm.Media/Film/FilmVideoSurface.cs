using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MeowFilm.Media;

/// <summary>The picture and controls are siblings in the application's composition tree.</summary>
public class FilmVideoSurface : Grid, IDisposable
{
    private readonly SwapChainPanel picture = new();
    private UIElement? controls;
    private XamlRoot? root;
    private nint swapchain;
    private bool closed;
    private TaskCompletionSource mounted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FilmVideoSurface()
    {
        Background = new SolidColorBrush(Colors.Black);
        AutomationProperties.SetName(this, "视频画面");
        Children.Add(picture);
        Loaded += SurfaceLoadedChanged;
        Unloaded += SurfaceLoadedChanged;
        SizeChanged += SizeChangedSurface;
        picture.CompositionScaleChanged += ScaleChangedSurface;
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
    internal virtual nint DisplayWindow => IsLoaded && XamlRoot is { } current
        ? Win32Interop.GetWindowFromWindowId(current.ContentIslandEnvironment.AppWindowId) : 0;

    internal virtual FilmKernelNative.Surface Describe() => new()
    {
        Size = (uint)Marshal.SizeOf<FilmKernelNative.Surface>(), Abi = FilmKernelNative.Abi,
        DisplayWindow = DisplayWindow,
        Width = Math.Max(1, (int)Math.Ceiling(ActualWidth * picture.CompositionScaleX)),
        Height = Math.Max(1, (int)Math.Ceiling(ActualHeight * picture.CompositionScaleY)),
        ScaleX = picture.CompositionScaleX, ScaleY = picture.CompositionScaleY,
        Visible = IsLoaded && XamlRoot?.IsHostVisible == true && ActualWidth > 0 && ActualHeight > 0 ? 1 : 0
    };

    internal virtual async Task WaitForTargetAsync(CancellationToken token)
    {
        VerifyAccess();
        while (!closed && (Describe() is not { DisplayWindow: not 0, Visible: 1 }))
            await mounted.Task.WaitAsync(token);
        if (closed) throw new PlaybackException(PlaybackError.Disposed);
    }

    // The caller transfers the AddRef returned by mfk_attach, including on failure.
    internal virtual void BindSwapchain(nint value)
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

    private void SurfaceLoadedChanged(object sender, RoutedEventArgs args) => Changed();
    private void SizeChangedSurface(object sender, SizeChangedEventArgs args) => Changed();
    private void ScaleChangedSurface(SwapChainPanel sender, object args) => Changed();
    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => Changed();
    private void Changed()
    {
        if (closed) return;
        var current = IsLoaded ? XamlRoot : null;
        if (!ReferenceEquals(root, current))
        {
            if (root is not null) root.Changed -= RootChanged;
            root = current;
            if (root is not null) root.Changed += RootChanged;
        }
        var previous = mounted;
        mounted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult();
        TargetChanged?.Invoke(this, EventArgs.Empty);
    }

    private void VerifyAccess()
    {
        if (!DispatcherQueue.HasThreadAccess) throw new PlaybackException(PlaybackError.WrongThread);
    }

    public void Dispose()
    {
        VerifyAccess();
        if (closed) return;
        BindSwapchain(0);
        closed = true;
        Loaded -= SurfaceLoadedChanged;
        Unloaded -= SurfaceLoadedChanged;
        SizeChanged -= SizeChangedSurface;
        picture.CompositionScaleChanged -= ScaleChangedSurface;
        if (root is not null) root.Changed -= RootChanged;
        root = null;
        Content = null;
        mounted.TrySetResult();
    }
}
