using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MeowFilm.Desktop;
using static MeowFilm.Windows.Tests.QualityFixture;

namespace MeowFilm.Windows.Tests;

public static class DesktopTrayIconTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("requires an existing shell HWND and owns one registration through repeated show and dispose", Lifetime);
        yield return Case("add version and live-icon failures roll back resources and permit a later attempt", Failures);
        yield return Case("Explorer recreation restores the icon or reports unavailability on the UI dispatcher", ExplorerRestart);
        yield return Case("native and menu actions use the UI dispatcher and queued actions retire with their owner", Callbacks);
        yield return Case("unavailable menu activation restores access and keeps signed screen coordinates", MenuUnavailable);
        // Real OS foreground verification is explicit and must never run on the user's interactive desktop.
        if (Environment.GetEnvironmentVariable("MEOWFILM_TRAY_SESSION0_VERIFY") == "1")
            yield return Case("isolated Session0 opens and focuses the real menu then retires its anchor", MenuLifecycle);
        yield return Case("detached menu preserves shared styles live palette high contrast and accessible actions", MenuTheme);
        yield return Case("native icon acquisition copies the current window icon without taking its ownership", WindowIcon);
        yield return Case("default WPF icon is valid for tray registration inside SourceInitialized without Window.Icon", DefaultWindowIcon);
    }

    private static TestCase Case(string name, Func<Task> body) =>
        new("desktop tray: " + name, () => MusicQualityConsumerTests.Sta(body));

    private static Window Owner() => new()
    {
        Width = 320, Height = 180, Left = -32000, Top = -32000,
        ShowActivated = false, ShowInTaskbar = false
    };

    private static async Task Lifetime()
    {
        var owner = Owner();
        var native = new ProbeNative();
        using var tray = new DesktopTrayIcon(owner, () => { }, () => { }, () => { }, native);
        try
        {
            Check(!tray.TryShow() && new WindowInteropHelper(owner).Handle == 0 &&
                native.Loads == 0 && native.Calls.Count == 0, "tray never creates the shell's HWND");
            var ownerHandle = new WindowInteropHelper(owner).EnsureHandle();
            Check(tray.TryShow() && tray.TryShow(), "initial registration and subsequent ensure both succeed");
            var added = native.Calls.Single(call => call.Operation == 0).Data;
            Check(added.Window != 0 && added.Window != ownerHandle && IsWindow(added.Window),
                "the tray owns its separate live callback and menu HWND");
            Check(native.Live && native.Loads == 1 && native.Calls.Count(call => call.Operation == 0) == 1,
                "ensuring an existing icon does not duplicate registration or icon acquisition");
            Check(!owner.IsVisible && !IsWindowVisible(added.Window),
                "registration never makes the shell or its menu anchor visible");
            var menu = Menu(tray);
            tray.Dispose(); tray.Dispose();
            Check(!native.Live && !IsWindow(added.Window) && native.Destroys == 1 &&
                native.Calls.Count(call => call.Operation == 2) == 1,
                "dispose removes the icon before releasing its HWND and owned HICON exactly once");
            Check(!tray.TryShow() && !menu.IsOpen && menu.PlacementTarget is null,
                "disposed adapters cannot re-register or retain a menu placement owner");
            owner.Close();
            await Drain();
            Check(native.Destroys == 1, "owner closure after explicit dispose does not repeat cleanup");
        }
        finally { owner.Close(); }
    }

    private static async Task Failures()
    {
        foreach (uint failedOperation in new uint[] { 0, 4 })
        {
            var owner = Owner();
            new WindowInteropHelper(owner).EnsureHandle();
            var native = new ProbeNative { FailedOperation = failedOperation };
            using var tray = new DesktopTrayIcon(owner, () => { }, () => { }, () => { }, native);
            try
            {
                Check(!tray.TryShow(), "a rejected shell operation cannot promise a tray entry");
                var failedWindow = native.Calls.Single(call => call.Operation == 0).Data.Window;
                Check(!native.Live && native.Destroys == 1 && !IsWindow(failedWindow),
                    "partial registration and icon acquisition are rolled back");
                Check(native.Calls.Any(call => call.Operation == 2) == (failedOperation == 4),
                    "an icon accepted before a version failure is removed");
                native.FailedOperation = null;
                Check(tray.TryShow() && native.Loads == 2 && native.Live,
                    "a failed attempt leaves the same adapter available for recovery");
                native.RejectModifyAndAdd = true;
                Check(!tray.TryShow() && !native.Live && native.Destroys == 2,
                    "a lost icon whose replacement also fails cannot reuse stale success");
            }
            finally { owner.Close(); }
        }
        var missingOwner = Owner();
        new WindowInteropHelper(missingOwner).EnsureHandle();
        var missing = new ProbeNative { IconAvailable = false };
        using var missingTray = new DesktopTrayIcon(missingOwner, () => { }, () => { }, () => { }, missing);
        try
        {
            Check(!missingTray.TryShow() && missing.Calls.Count == 0 && missing.Destroys == 0,
                "missing product icon does not register an empty icon or destroy an unowned handle");
        }
        finally { missingOwner.Close(); }
        await Drain();
    }

    private static async Task ExplorerRestart()
    {
        var owner = Owner();
        new WindowInteropHelper(owner).EnsureHandle();
        var native = new ProbeNative();
        int unavailable = 0;
        using var tray = new DesktopTrayIcon(owner, () => { }, () => { }, () =>
        {
            Check(owner.Dispatcher.CheckAccess(), "unavailability is delivered on the shell's dispatcher");
            unavailable++;
            owner.Show();
        }, native);
        try
        {
            Check(tray.TryShow());
            var added = native.Calls.Single(call => call.Operation == 0).Data;
            var recreated = (int)DesktopTrayIconNative.RegisterWindowMessage("TaskbarCreated");
            native.Live = false;
            DesktopTrayIconNative.SendMessage(added.Window, recreated, 0, 0);
            DesktopTrayIconNative.SendMessage(added.Window, recreated, 0, 0);
            await Drain();
            Check(native.Live && unavailable == 0 && native.Loads == 1 &&
                native.Calls.Count(call => call.Operation == 0) == 2,
                "Explorer broadcast restores one registration while retaining the owned icon");
            native.Live = false;
            native.FailedOperation = 0;
            DesktopTrayIconNative.SendMessage(added.Window, recreated, 0, 0);
            await Drain();
            Check(unavailable == 1 && owner.IsVisible && !native.Live && !IsWindow(added.Window) &&
                native.Destroys == 1, "failed recreation releases resources and permits shell recovery");
        }
        finally { owner.Close(); }
    }

    private static async Task Callbacks()
    {
        var owner = Owner();
        new WindowInteropHelper(owner).EnsureHandle();
        var native = new ProbeNative();
        int shown = 0, exited = 0;
        void Show() { Check(owner.Dispatcher.CheckAccess()); shown++; }
        void Exit() { Check(owner.Dispatcher.CheckAccess()); exited++; }
        using var tray = new DesktopTrayIcon(owner, Show, Exit, () => { }, native);
        try
        {
            Check(tray.TryShow());
            var data = native.Calls.Single(call => call.Operation == 0).Data;
            void Send(int notification, uint id = 1) => DesktopTrayIconNative.SendMessage(
                data.Window, (int)data.CallbackMessage, 0, (nint)((id << 16) | (uint)notification));
            await Task.Run(() => { Send(0x400); Send(0x401); Send(0x400, 2); });
            await Drain();
            Check(shown == 2 && exited == 0 && !owner.IsVisible,
                "primary and keyboard activation reach the UI owner without window or exit policy in the adapter");
            var menu = Menu(tray);
            var show = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "显示主窗口"));
            var exit = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "退出"));
            show.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            exit.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Drain();
            Check(shown == 3 && exited == 1, "each explicit menu action reaches its own callback once");
            Send(0x400);
            exit.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            owner.Close();
            await Drain();
            Check(shown == 3 && exited == 1 && native.Destroys == 1 && !native.Live &&
                !IsWindow(data.Window), "owner close retires queued actions and disposes native resources");
            show.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            exit.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Drain();
            Check(shown == 3 && exited == 1, "retired menu items no longer invoke the application");
        }
        finally { owner.Close(); }
    }

    private static async Task MenuTheme()
    {
        var owner = Owner();
        using var tray = new DesktopTrayIcon(owner, () => { }, () => { }, () => { }, new ProbeNative());
        var menu = Menu(tray);
        try
        {
            AppearanceColors.ApplyFloatingPalette(AppearanceColors.DefaultColor);
            DesktopTheme.SetIsHighContrast(menu, false);
            Layout(menu);
            Check(menu.Style == menu.FindResource("MeowFilm.ContextMenu") && !menu.StaysOpen &&
                AutomationProperties.GetName(menu) == "系统托盘菜单",
                "the detached menu uses the shared context menu and normal dismissal semantics");
            var items = menu.Items.OfType<MenuItem>().ToArray();
            Check(items.Select(item => item.Header).SequenceEqual(new[] { "显示主窗口", "退出" }));
            Check(items.All(item => item.Style == menu.FindResource("MeowFilm.MenuItem") &&
                AutomationProperties.GetName(item) == (string)item.Header),
                "the real actionable menu items keep shared templates and accessible labels");
            var surface = Visuals(menu).OfType<Border>().Single(border =>
                ReferenceEquals(border.Style, menu.FindResource("MeowFilm.FloatingSurface")));
            var before = ((SolidColorBrush)surface.Background).Color;
            SavePreview(menu, "tray-menu-default");
            AppearanceColors.ApplyFloatingPalette("#AA2200");
            await Drain();
            Check(((SolidColorBrush)surface.Background).Color != before &&
                ReferenceEquals(surface.Background, AppearanceColors.FloatingSurface),
                "an already templated detached menu follows subsequent application palette changes");
            SavePreview(menu, "tray-menu-themed");
            DesktopTheme.SetIsHighContrast(menu, true);
            Check(items.All(item => item.Foreground == SystemColors.WindowTextBrush) &&
                surface.Background == SystemColors.WindowBrush && surface.Effect is null,
                "system high contrast reaches both menu items and its floating surface");
            SavePreview(menu, "tray-menu-high-contrast");
            items[1].IsEnabled = false;
            Check(items[1].Foreground == SystemColors.GrayTextBrush, "disabled contrast uses the system color");
            DesktopTheme.SetIsHighContrast(menu, false);
            items[1].IsEnabled = true;
            owner.Resources["MeowFilm.Brush.Text"] = Brushes.White;
            await Drain();
            Check(items.All(item => item.Foreground == Brushes.White),
                "owner theme tokens also reach a detached menu without a visible shell");
            owner.Resources["MeowFilm.Brush.Text"] = Brushes.Black;
            await Drain();
            Check(items.All(item => item.Foreground == Brushes.Black), "theme token replacement remains live");
        }
        finally
        {
            AppearanceColors.ApplyFloatingPalette(AppearanceColors.DefaultColor);
            owner.Close();
        }
    }

    private static async Task MenuUnavailable()
    {
        var owner = Owner();
        new WindowInteropHelper(owner).EnsureHandle();
        var native = new ProbeNative();
        int unavailable = 0;
        using var tray = new DesktopTrayIcon(owner, () => { }, () => { }, () =>
        {
            Check(owner.Dispatcher.CheckAccess());
            unavailable++;
            owner.Show();
        }, native);
        try
        {
            Check(tray.TryShow() && !owner.IsVisible);
            var data = native.Calls.Single(call => call.Operation == 0).Data;
            short x = -1920, y = 720;
            var position = (nint)((uint)unchecked((ushort)x) | ((uint)unchecked((ushort)y) << 16));
            DesktopTrayIconNative.SendMessage(data.Window, (int)data.CallbackMessage, position,
                (nint)((data.Id << 16) | 0x7b));
            await Drain();
            Check(unavailable == 1 && owner.IsVisible && !Menu(tray).IsOpen && native.Live,
                "a rejected foreground activation reports failure without trapping the hidden application");
            Check(GetWindowRect(data.Window, out var rect) && rect.Left == x && rect.Top == y &&
                !IsWindowVisible(data.Window),
                "version-four callback coordinates stay signed physical screen coordinates");
        }
        finally { owner.Close(); }
    }

    private static async Task MenuLifecycle()
    {
        using var process = Process.GetCurrentProcess();
        Check(process.SessionId == 0, "successful foreground/menu evidence requires the isolated Windows Session0");
        var owner = Owner();
        new WindowInteropHelper(owner).EnsureHandle();
        var native = new ProbeNative { UseNativeActivation = true, RestoreTrayFocus = true };
        var unavailable = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var tray = new DesktopTrayIcon(owner, () => { }, () => { }, () => unavailable.TrySetResult(), native);
        try
        {
            Check(tray.TryShow());
            var data = native.Calls.Single(call => call.Operation == 0).Data;
            var menu = Menu(tray);
            for (int cycle = 0; cycle < 2; cycle++)
            {
                var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnOpened(object sender, RoutedEventArgs e) => opened.TrySetResult();
                void OnClosed(object sender, RoutedEventArgs e) => closed.TrySetResult();
                menu.Opened += OnOpened;
                menu.Closed += OnClosed;
                try
                {
                    var position = unchecked((nint)(unchecked((uint)(ushort)-32000) |
                        (unchecked((uint)(ushort)-32000) << 16)));
                    DesktopTrayIconNative.SendMessage(data.Window, (int)data.CallbackMessage, position,
                        (nint)((data.Id << 16) | 0x7b));
                    var outcome = await Task.WhenAny(opened.Task, unavailable.Task).WaitAsync(TimeSpan.FromSeconds(3));
                    Check(outcome == opened.Task,
                        "Session0 denied real foreground/menu activation; successful menu evidence is unavailable");
                    await Drain();
                    Check(menu.IsOpen && !owner.IsVisible && IsWindowVisible(data.Window),
                        "a hidden shell can open its real WPF menu on the visible tool anchor");
                    Check(menu.ActualWidth > 0 && menu.ActualHeight > 0 &&
                        menu.Items.OfType<MenuItem>().All(item => item.ActualWidth > 0 && item.ActualHeight > 0),
                        "the opened native WPF menu and both actions have nonempty layout");
                    Check(menu.IsKeyboardFocusWithin && Keyboard.FocusedElement is not null,
                        "the actual opened menu receives keyboard focus");
                    var popup = PresentationSource.FromVisual(menu) as HwndSource
                        ?? throw new InvalidOperationException("the opened WPF menu has no real popup HWND");
                    DesktopTrayIconNative.SendMessage(data.Window, 0x06, 0, popup.Handle); // WA_INACTIVE -> own popup
                    await Drain();
                    Check(menu.IsOpen, "the anchor becoming inactive for its own popup does not dismiss the menu");
                    DesktopTrayIconNative.SendMessage(data.Window, 0x1f, 0, 0); // WM_CANCELMODE
                    await closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    await Drain();
                    Check(!menu.IsOpen && !IsWindowVisible(data.Window),
                        "dismissal hides the anchor while retaining its notification callback HWND");
                    Check(native.Calls.Count(call => call.Operation == 3) == 1,
                        "only dismissal that still owns foreground focus returns it to the notification area");
                    native.RestoreTrayFocus = false; // The next dismissal represents an outside click.
                }
                finally { menu.Opened -= OnOpened; menu.Closed -= OnClosed; }
            }
        }
        finally { owner.Close(); }
    }

    private static Task WindowIcon()
    {
        var owner = Owner();
        owner.Icon = BitmapFrame.Create(BitmapSource.Create(16, 16, 96, 96,
            PixelFormats.Bgra32, null, new byte[16 * 16 * 4], 16 * 4));
        var native = new DesktopTrayIconNative();
        nint owned = 0, retained = 0;
        try
        {
            var handle = new WindowInteropHelper(owner).EnsureHandle();
            var borrowed = DesktopTrayIconNative.SendMessage(handle, 0x7f, 0, 0); // WM_GETICON / ICON_SMALL
            Check(borrowed != 0, "the fixture's actual WPF window exposes its icon");
            owned = native.LoadOwnedIcon(handle);
            Check(owned != 0 && owned != borrowed, "the adapter receives a separately owned icon");
            native.DestroyOwnedIcon(owned);
            owned = 0;
            retained = CopyIcon(borrowed);
            Check(retained != 0, "releasing the tray's icon leaves the window-owned original usable");
        }
        finally
        {
            if (owned != 0) native.DestroyOwnedIcon(owned);
            if (retained != 0) native.DestroyOwnedIcon(retained);
            owner.Close();
        }
        return Task.CompletedTask;
    }

    private static Task DefaultWindowIcon()
    {
        var owner = Owner();
        owner.WindowStyle = WindowStyle.None;
        owner.ShowInTaskbar = true;
        var native = new ProbeNative { UseNativeIcon = true };
        using var tray = new DesktopTrayIcon(owner, () => { }, () => { }, () => { }, native);
        bool initialized = false;
        nint borrowed = 0, retained = 0;
        void OnInitialized(object? sender, EventArgs e)
        {
            initialized = true;
            var handle = new WindowInteropHelper(owner).Handle;
            Check(owner.Icon is null && handle != 0 && !owner.IsVisible,
                "default-icon acquisition runs during SourceInitialized with no explicit Window.Icon");
            var source = new (string Name, nint Icon)[]
            {
                ("WM_GETICON/ICON_SMALL2", DesktopTrayIconNative.SendMessage(handle, 0x7f, 2, 0)),
                ("WM_GETICON/ICON_SMALL", DesktopTrayIconNative.SendMessage(handle, 0x7f, 0, 0)),
                ("WM_GETICON/ICON_BIG", DesktopTrayIconNative.SendMessage(handle, 0x7f, 1, 0)),
                ("GCLP_HICONSM", DesktopTrayIconNative.GetClassLongPtr(handle, -34)),
                ("GCLP_HICON", DesktopTrayIconNative.GetClassLongPtr(handle, -14))
            }.FirstOrDefault(candidate => candidate.Icon != 0);
            borrowed = source.Icon;
            Check(borrowed != 0, "the default WPF window must expose a native icon before tray registration");
            Check(tray.TryShow(), "the real native default icon is available at the shell's registration stage");
            var data = native.Calls.Single(call => call.Operation == 0).Data;
            Check(data.Icon != 0 && data.Icon != borrowed,
                "tray registration receives an owned copy of the default native window icon");
            var bitmap = Imaging.CreateBitmapSourceFromHIcon(data.Icon, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            Check(bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0, "the registered HICON has valid image data");
            Console.WriteLine($"DEFAULT_ICON stage=SourceInitialized source={source.Name} " +
                $"process={Path.GetFileName(Environment.ProcessPath)} size={bitmap.PixelWidth}x{bitmap.PixelHeight}");
        }
        owner.SourceInitialized += OnInitialized;
        try
        {
            new WindowInteropHelper(owner).EnsureHandle();
            Check(initialized && native.Live && native.Loads == 1, "the callback actually registered the tray icon");
            tray.Dispose();
            retained = CopyIcon(borrowed);
            Check(retained != 0 && native.Destroys == 1,
                "disposing the tray leaves the window's default native icon usable");
        }
        finally
        {
            owner.SourceInitialized -= OnInitialized;
            if (retained != 0) native.DestroyOwnedIcon(retained);
            owner.Close();
        }
        return Task.CompletedTask;
    }

    private static ContextMenu Menu(DesktopTrayIcon tray) => (ContextMenu)typeof(DesktopTrayIcon)
        .GetField("_menu", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(tray)!;

    private static void SavePreview(ContextMenu menu, string name)
    {
        string root = Environment.GetEnvironmentVariable("MEOWFILM_SETTINGS_PREVIEW_ROOT")
            ?? TestWorkspace.DataPath("previews");
        TestWorkspace.ValidatePath(TestWorkspace.DataRoot, root);
        Directory.CreateDirectory(root);
        Layout(menu);
        int width = (int)Math.Ceiling(menu.DesiredSize.Width), height = (int)Math.Ceiling(menu.DesiredSize.Height);
        foreach (int dpi in new[] { 96, 144, 192 })
        {
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * dpi / 96d),
                (int)Math.Ceiling(height * dpi / 96d), dpi, dpi, PixelFormats.Pbgra32);
            bitmap.Render(menu);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string path = Path.Combine(root, $"{name}-{dpi}.png");
            using var file = File.Create(path);
            encoder.Save(file);
            Console.WriteLine("PREVIEW " + path);
        }
    }

    private static void Layout(FrameworkElement element)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        element.Arrange(new Rect(element.DesiredSize));
        element.UpdateLayout();
    }

    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static async Task Drain()
    {
        for (int i = 0; i < 3; i++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private sealed class ProbeNative : DesktopTrayIconNative
    {
        public List<(uint Operation, DesktopTrayIconData Data)> Calls { get; } = [];
        public int Loads, Destroys;
        public bool Live, RejectModifyAndAdd;
        public bool IconAvailable = true;
        public uint? FailedOperation;
        public bool UseNativeActivation, RestoreTrayFocus, UseNativeIcon;

        internal override nint LoadOwnedIcon(nint owner)
        {
            Loads++;
            return UseNativeIcon ? base.LoadOwnedIcon(owner) : IconAvailable ? 42 : 0;
        }
        internal override void DestroyOwnedIcon(nint icon)
        {
            Check((UseNativeIcon || icon == 42) && !Live,
                "only this adapter's acquired icon is released, after unregistering");
            if (UseNativeIcon) base.DestroyOwnedIcon(icon);
            Destroys++;
        }
        internal override bool Notify(uint operation, ref DesktopTrayIconData data)
        {
            Check(IsWindow(data.Window), "native registration and deletion always receive a live owned HWND");
            Calls.Add((operation, data));
            if (FailedOperation == operation || (RejectModifyAndAdd && operation is 0 or 1)) return false;
            if (operation == 0) Live = true;
            if (operation == 2) Live = false;
            return operation != 1 || Live;
        }
        internal override bool ActivateMenuHost(nint hwnd) => UseNativeActivation && base.ActivateMenuHost(hwnd);
        internal override bool IsMenuHostForeground(nint hwnd) => RestoreTrayFocus;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hwnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint CopyIcon(nint icon);
}
