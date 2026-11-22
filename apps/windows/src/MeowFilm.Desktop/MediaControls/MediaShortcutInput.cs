using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;
using Key = Windows.System.VirtualKey;
using ModifierKeys = Windows.System.VirtualKeyModifiers;

namespace MeowFilm.Desktop;

internal static class MediaShortcutInput
{
    internal static Key Key(KeyRoutedEventArgs args) =>
        args.OriginalKey;

    internal static ModifierKeys Modifiers() => Modifiers(Down(VirtualKey.Control), Down(VirtualKey.Menu),
        Down(VirtualKey.Shift), Down(VirtualKey.LeftWindows), Down(VirtualKey.RightWindows));

    internal static ModifierKeys Modifiers(bool control, bool alt, bool shift, bool leftWindows, bool rightWindows)
    {
        ModifierKeys modifiers = ModifierKeys.None;
        if (control) modifiers |= ModifierKeys.Control;
        if (alt) modifiers |= ModifierKeys.Menu;
        if (shift) modifiers |= ModifierKeys.Shift;
        if (leftWindows || rightWindows) modifiers |= ModifierKeys.Windows;
        return modifiers;
    }

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

    private static bool Down(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;

    internal static bool AllowsLocal(FrameworkElement root, object? target)
    {
        if (HasPopup(root)) return false;
        for (var node = target as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (Protected(node)) return false;
            if (ReferenceEquals(node, root)) return true;
        }
        return false;
    }

    internal static bool ProtectsForeground(FrameworkElement root, nint hwnd, MediaShortcutNative native)
    {
        nint foreground = native.ForegroundWindow;
        // A background root's retained XAML focus is not the current input target.
        if (!native.OwnsWindow(hwnd, foreground)) return false;
        if (foreground != hwnd || native.HasMenuOrChildFocus(hwnd, root) || HasPopup(root)) return true;
        bool editor = false;
        for (var node = native.FocusedElement(root); node is not null; node = VisualTreeHelper.GetParent(node))
        {
            editor |= Protected(node);
            if (ReferenceEquals(node, root)) return editor;
        }
        return false;
    }

    private static bool HasPopup(FrameworkElement root) => root.XamlRoot is { } xamlRoot &&
        VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot).Any(popup => popup.IsOpen && popup.Child is not ToolTip);

    private static bool Protected(DependencyObject node) =>
        node is DesktopModalHost or TextBox or RichEditBox or PasswordBox or AutoSuggestBox or
            ComboBox { IsEditable: true } or MenuFlyoutPresenter or MenuFlyoutItemBase or MenuBar or
            MenuBarItem or FlyoutPresenter or Popup or ContentDialog or WebView2;
}
