using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using ModifierKeys = Windows.System.VirtualKeyModifiers;

namespace MeowFilm.Desktop;

internal static class MediaShortcutInput
{
    internal static uint NativeModifiers(ModifierKeys modifiers)
    {
        // VirtualKeyModifiers uses Control=1/Alt=2; RegisterHotKey uses MOD_ALT=1/MOD_CONTROL=2.
        uint native = 0;
        if (modifiers.HasFlag(ModifierKeys.Menu)) native |= 0x0001;
        if (modifiers.HasFlag(ModifierKeys.Control)) native |= 0x0002;
        if (modifiers.HasFlag(ModifierKeys.Shift)) native |= 0x0004;
        if (modifiers.HasFlag(ModifierKeys.Windows)) native |= 0x0008;
        return native;
    }

    internal static bool AllowsLocal(FrameworkElement root, object? target)
    {
        if (HasPopup(root) || HasModal(root)) return false;
        for (var node = target as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (Protected(node)) return false;
            if (ReferenceEquals(node, root)) return true;
        }
        return false;
    }

    internal static bool ProtectsForeground(FrameworkElement root, nint hwnd)
    {
        nint foreground = MediaShortcutNative.ForegroundWindow;
        // A background root's retained XAML focus is not the current input target.
        if (!MediaShortcutNative.OwnsWindow(hwnd, foreground)) return false;
        if (foreground != hwnd || MediaShortcutNative.HasMenuOrChildFocus(hwnd, root) ||
            HasPopup(root) || HasModal(root)) return true;
        bool editor = false;
        for (var node = DesktopVisual.Focused(root); node is not null; node = VisualTreeHelper.GetParent(node))
        {
            editor |= Protected(node);
            if (ReferenceEquals(node, root)) return editor;
        }
        return false;
    }

    private static bool HasModal(FrameworkElement root) =>
        DesktopWindow.GetWindow(root) is ShellWindow { IsAnyModalOpen: true } ||
        DesktopModalHost.Find(root)?.IsOpen == true;

    private static bool HasPopup(FrameworkElement root) => root.XamlRoot is { } xamlRoot &&
        VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot).Any(popup => popup.IsOpen && popup.Child is not ToolTip);

    private static bool Protected(DependencyObject node) =>
        node is DesktopModalHost or TextBox or RichEditBox or PasswordBox or AutoSuggestBox or
            ComboBox { IsEditable: true } or MenuFlyoutPresenter or MenuFlyoutItemBase or MenuBar or
            MenuBarItem or FlyoutPresenter or Popup or WebView2;
}
