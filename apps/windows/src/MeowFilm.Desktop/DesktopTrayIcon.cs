using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace MeowFilm.Desktop;

internal interface IDesktopTrayIcon : IDisposable
{
    bool TryShow();
}

internal sealed class DesktopTrayIcon : IDesktopTrayIcon
{
    private const uint IconId = 1;
    private const int CallbackMessage = 0x8041;
    private readonly Window _owner;
    private readonly Action _showWindow, _exitApplication, _unavailable;
    private readonly DesktopTrayIconNative _native;
    private readonly Border _anchor = new() { Background = Brushes.Transparent };
    private readonly ContextMenu _menu;
    private readonly MenuItem _showItem, _exitItem;
    private HwndSource? _source;
    private nint _icon;
    private uint _taskbarCreated;
    private bool _registered, _recoveryQueued, _disposed;

    internal DesktopTrayIcon(Window owner, Action showWindow, Action exitApplication, Action unavailable)
        : this(owner, showWindow, exitApplication, unavailable, new DesktopTrayIconNative()) { }

    internal DesktopTrayIcon(Window owner, Action showWindow, Action exitApplication, Action unavailable,
        DesktopTrayIconNative native)
    {
        owner.Dispatcher.VerifyAccess();
        _owner = owner;
        _showWindow = showWindow;
        _exitApplication = exitApplication;
        _unavailable = unavailable;
        _native = native;
        _menu = new ContextMenu
        {
            PlacementTarget = _anchor,
            Placement = PlacementMode.RelativePoint,
            StaysOpen = false,
            UseLayoutRounding = true,
            SnapsToDevicePixels = true
        };
        _menu.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("/MeowFilm.Desktop;component/Theme.xaml", UriKind.Relative) });
        _menu.Resources.MergedDictionaries.Add(owner.Resources);
        _menu.SetResourceReference(FrameworkElement.StyleProperty, "MeowFilm.ContextMenu");
        AutomationProperties.SetName(_menu, "系统托盘菜单");
        _showItem = CreateItem("显示主窗口");
        _exitItem = CreateItem("退出");
        _menu.Items.Add(_showItem);
        _menu.Items.Add(_exitItem);
        _showItem.Click += OnShowClick;
        _exitItem.Click += OnExitClick;
        _menu.Opened += OnMenuOpened;
        _menu.Closed += OnMenuClosed;
        owner.Closed += OnOwnerClosed;
        SystemParameters.StaticPropertyChanged += OnSystemChanged;
        UpdateHighContrast();
    }

    private static MenuItem CreateItem(string label)
    {
        var item = new MenuItem { Header = label };
        item.SetResourceReference(FrameworkElement.StyleProperty, "MeowFilm.MenuItem");
        AutomationProperties.SetName(item, label);
        return item;
    }

    public bool TryShow()
    {
        _owner.Dispatcher.VerifyAccess();
        if (_disposed) return false;
        var ownerHandle = new WindowInteropHelper(_owner).Handle;
        // The shell must already have its HWND; creating it here would change its startup lifecycle.
        if (ownerHandle == 0) return false;
        try
        {
            if (_taskbarCreated == 0) _taskbarCreated = DesktopTrayIconNative.RegisterWindowMessage("TaskbarCreated");
            if (_taskbarCreated == 0) return false;
            if (_source is null)
            {
                // A top-level tool HWND receives Explorer's broadcast. It is only made visible
                // while its WPF menu is open, so a hidden shell never has to be activated.
                _source = new HwndSource(new HwndSourceParameters("Meowfilm tray")
                {
                    WindowStyle = unchecked((int)0x80000000), // WS_POPUP, without WS_VISIBLE
                    ExtendedWindowStyle = 0x80, // WS_EX_TOOLWINDOW, without WS_EX_NOACTIVATE
                    UsesPerPixelOpacity = true,
                    Width = 1,
                    Height = 1
                });
                _source.RootVisual = _anchor;
                _source.AddHook(OnMessage);
            }
            if (_icon == 0) _icon = _native.LoadOwnedIcon(ownerHandle);
            if (_icon == 0) { ReleaseNative(); return false; }
            var data = Data();
            data.Flags = 0x01 | 0x02 | 0x04 | 0x80; // MESSAGE | ICON | TIP | SHOWTIP
            data.CallbackMessage = CallbackMessage;
            data.Icon = _icon;
            data.Tip = "Meowfilm";
            if (_registered)
            {
                if (_native.Notify(1, ref data)) return true; // NIM_MODIFY verifies the live icon.
                var old = Data();
                _native.Notify(2, ref old);
                _registered = false;
            }
            if (!_native.Notify(0, ref data)) { ReleaseNative(); return false; }
            _registered = true;
            data.Version = 4; // NOTIFYICON_VERSION_4 supplies keyboard activation and screen coordinates.
            if (_native.Notify(4, ref data)) return true;
            ReleaseNative();
            return false;
        }
        catch (Exception error) when (error is ExternalException or InvalidOperationException)
        {
            ReleaseNative();
            return false;
        }
    }

    private DesktopTrayIconData Data() => new()
    {
        Size = (uint)Marshal.SizeOf<DesktopTrayIconData>(),
        Window = _source!.Handle,
        Id = IconId
    };

    private nint OnMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (_disposed) return 0;
        if ((uint)message == _taskbarCreated)
        {
            _registered = false;
            _menu.IsOpen = false;
            if (!_recoveryQueued)
            {
                _recoveryQueued = true;
                Queue(() =>
                {
                    _recoveryQueued = false;
                    if (!TryShow()) _unavailable();
                });
            }
            handled = true;
        }
        else if (message == CallbackMessage && ((lParam.ToInt64() >> 16) & 0xffff) == IconId)
        {
            switch ((int)(lParam.ToInt64() & 0xffff))
            {
                case 0x400: // NIN_SELECT: the shell's primary mouse activation
                case 0x401: // NIN_KEYSELECT: Space/Enter from the notification area
                    Queue(_showWindow);
                    break;
                case 0x7b: // WM_CONTEXTMENU, including keyboard invocation
                    var position = new Point(unchecked((short)(wParam.ToInt64() & 0xffff)),
                        unchecked((short)((wParam.ToInt64() >> 16) & 0xffff)));
                    Queue(() => OpenMenu(position));
                    break;
            }
            handled = true;
        }
        else if (message == 0x1f)
            _menu.IsOpen = false; // WM_CANCELMODE
        // ContextMenu owns keyboard/outside-click/app-deactivation dismissal. In particular,
        // WA_INACTIVE can be a focus transfer into its own popup, not a request to close it.
        return 0;
    }

    private void Queue(Action action) => _owner.Dispatcher.BeginInvoke(DispatcherPriority.Normal,
        new Action(() => { if (!_disposed) action(); }));

    private void OpenMenu(Point position)
    {
        if (!_registered || _source is not { IsDisposed: false }) return;
        if (_menu.IsOpen) { _menu.Focus(); return; }
        if (position == new Point(-1, -1) &&
            !DesktopTrayIconNative.TryGetMenuPosition(_source.Handle, IconId, out position))
        {
            _unavailable();
            return;
        }
        // Position the real HWND in physical pixels first. WPF then uses that monitor's
        // DPI for the relative popup; no primary-monitor pixel/DIP conversion is involved.
        if (!_native.ShowMenuHost(_source.Handle, position))
        {
            _unavailable();
            return;
        }
        try
        {
            UpdateHighContrast();
            _menu.IsOpen = true;
        }
        catch (Exception error) when (error is ExternalException or InvalidOperationException)
        {
            _menu.IsOpen = false;
            _native.CloseMenuHost(_source.Handle);
            _unavailable();
        }
    }

    private void OnMenuOpened(object sender, RoutedEventArgs e)
    {
        _menu.Focus();
        _menu.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    private void OnMenuClosed(object sender, RoutedEventArgs e)
    {
        if (!_menu.IsOpen && _source is { IsDisposed: false })
        {
            bool returnFocus = _native.CloseMenuHost(_source.Handle);
            if (returnFocus && _registered && !_disposed)
            {
                var data = Data();
                _native.Notify(3, ref data); // NIM_SETFOCUS also returns Escape dismissal to the tray.
            }
        }
    }

    private void OnShowClick(object sender, RoutedEventArgs e)
    {
        _menu.IsOpen = false;
        Queue(_showWindow);
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        _menu.IsOpen = false;
        Queue(_exitApplication);
    }

    private void OnSystemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(SystemParameters.HighContrast))
            Queue(UpdateHighContrast);
    }

    private void UpdateHighContrast() => DesktopTheme.SetIsHighContrast(_menu, SystemParameters.HighContrast);
    private void OnOwnerClosed(object? sender, EventArgs e) => Dispose();

    private void ReleaseNative()
    {
        _menu.IsOpen = false;
        if (_source is { } source)
        {
            if (!source.IsDisposed)
            {
                if (_registered)
                {
                    var data = Data();
                    _native.Notify(2, ref data); // NIM_DELETE precedes HWND and HICON destruction.
                }
                source.RemoveHook(OnMessage);
                source.RootVisual = null;
                source.Dispose();
            }
            _source = null;
        }
        _registered = false;
        if (_icon != 0)
        {
            _native.DestroyOwnedIcon(_icon);
            _icon = 0;
        }
    }

    public void Dispose()
    {
        _owner.Dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        _owner.Closed -= OnOwnerClosed;
        SystemParameters.StaticPropertyChanged -= OnSystemChanged;
        ReleaseNative();
        _showItem.Click -= OnShowClick;
        _exitItem.Click -= OnExitClick;
        _menu.Opened -= OnMenuOpened;
        _menu.Closed -= OnMenuClosed;
        _menu.PlacementTarget = null;
        _menu.Resources.MergedDictionaries.Clear();
    }
}
