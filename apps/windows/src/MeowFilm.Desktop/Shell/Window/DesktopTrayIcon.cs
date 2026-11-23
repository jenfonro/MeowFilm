using System.Runtime.InteropServices;
using Windows.Foundation;

namespace MeowFilm.Desktop;

/// <summary>System notification-area integration on the shell's own HWND; on the WinUI window.</summary>
internal sealed class DesktopTrayIcon : IDisposable
{
    private const uint IconId = 1;
    private const int CallbackMessage = 0x8041;
    private readonly DesktopWindow owner;
    private readonly Action showWindow, exitApplication, unavailable;
    private nint icon;
    private uint taskbarCreated;
    private bool registered, recoveryQueued, disposed, menuOpen;

    internal DesktopTrayIcon(DesktopWindow owner, Action showWindow, Action exitApplication, Action unavailable)
    {
        this.owner = owner; this.showWindow = showWindow; this.exitApplication = exitApplication;
        this.unavailable = unavailable;
        owner.NativeMessage += Message; owner.Closed += Closed;
    }

    public bool TryShow()
    {
        owner.Dispatcher.VerifyAccess();
        if (disposed || owner.Handle == 0) return false;
        try
        {
            if (taskbarCreated == 0) taskbarCreated = DesktopTrayIconNative.RegisterWindowMessage("TaskbarCreated");
            if (taskbarCreated == 0) return false;
            if (icon == 0) icon = DesktopTrayIconNative.LoadOwnedIcon(owner.Handle);
            if (icon == 0) return false;
            var data = Data();
            data.Flags = 0x01 | 0x02 | 0x04 | 0x80;
            data.CallbackMessage = CallbackMessage; data.Icon = icon; data.Tip = "Meowfilm";
            if (registered)
            {
                if (DesktopTrayIconNative.Notify(1, ref data)) return true;
                var old = Data(); DesktopTrayIconNative.Notify(2, ref old); registered = false;
            }
            if (!DesktopTrayIconNative.Notify(0, ref data)) { Release(); return false; }
            registered = true; data.Version = 4;
            if (DesktopTrayIconNative.Notify(4, ref data)) return true;
            Release(); return false;
        }
        catch (Exception error) when (error is ExternalException or InvalidOperationException) { Release(); return false; }
    }

    private DesktopTrayIconData Data() => new() { Size = (uint)Marshal.SizeOf<DesktopTrayIconData>(), Window = owner.Handle, Id = IconId };

    private nint Message(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (disposed) return 0;
        if ((uint)message == taskbarCreated)
        {
            registered = false;
            if (!recoveryQueued)
            {
                recoveryQueued = true;
                Queue(() => { recoveryQueued = false; if (!TryShow()) unavailable(); });
            }
            handled = true;
        }
        else if (message == CallbackMessage && ((lParam.ToInt64() >> 16) & 0xffff) == IconId)
        {
            switch ((int)(lParam.ToInt64() & 0xffff))
            {
                case 0x400: case 0x401: Queue(showWindow); break;
                case 0x7b:
                    var point = new Point(unchecked((short)(wParam.ToInt64() & 0xffff)), unchecked((short)((wParam.ToInt64() >> 16) & 0xffff)));
                    Queue(() => OpenMenu(point)); break;
            }
            handled = true;
        }
        return 0;
    }

    private void Queue(Action action) => owner.DispatcherQueue.TryEnqueue(() => { if (!disposed) action(); });

    private void OpenMenu(Point point)
    {
        if (!registered || disposed || menuOpen) return;
        if (point == new Point(-1, -1) && !DesktopTrayIconNative.TryGetMenuPosition(owner.Handle, IconId, out point)) { unavailable(); return; }
        nint menu = CreatePopupMenu();
        if (menu == 0) { unavailable(); return; }
        nint menuHost = 0;
        menuOpen = true;
        try
        {
            AppendMenu(menu, 0, 1, "显示主窗口"); AppendMenu(menu, 0, 2, "退出");
            // A system tray menu is not application content. Its native menu owns keyboard,
            // high contrast, monitor placement and dismissal even while the shell is hidden.
            menuHost = DesktopTrayIconNative.CreateMenuHost(point);
            if (menuHost == 0 || !DesktopTrayIconNative.ActivateMenuHost(menuHost)) { unavailable(); return; }
            uint command = DesktopTrayIconNative.TrackMenu(menu, point, menuHost);
            PostMessage(menuHost, 0, 0, 0);
            if (command == 1) Queue(showWindow);
            else if (command == 2) Queue(exitApplication);
            else if (DesktopTrayIconNative.IsMenuHostForeground(menuHost) && registered && !disposed)
            { var data = Data(); DesktopTrayIconNative.Notify(3, ref data); }
        }
        finally
        {
            menuOpen = false;
            DestroyMenu(menu);
            if (menuHost != 0) DesktopTrayIconNative.DestroyMenuHost(menuHost);
        }
    }

    private void Closed(object sender, Microsoft.UI.Xaml.WindowEventArgs args) => Dispose();

    private void Release()
    {
        if (registered) { var data = Data(); DesktopTrayIconNative.Notify(2, ref data); }
        registered = false;
        if (icon != 0) { DesktopTrayIconNative.DestroyOwnedIcon(icon); icon = 0; }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; owner.NativeMessage -= Message; owner.Closed -= Closed; Release();
    }

    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string label);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(nint hwnd, uint message, nuint wParam, nint lParam);
}
