using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace MeowFilm.Desktop;

internal static class MediaShortcutInput
{
    internal static Key Key(KeyEventArgs args) => args.Key == System.Windows.Input.Key.System ? args.SystemKey : args.Key;

    internal static ModifierKeys Modifiers(KeyboardDevice keyboard)
    {
        // WPF's Modifiers property reports only Alt, Control and Shift.
        return keyboard.Modifiers |
            (keyboard.IsKeyDown(System.Windows.Input.Key.LWin) || keyboard.IsKeyDown(System.Windows.Input.Key.RWin)
                ? ModifierKeys.Windows : ModifierKeys.None);
    }

    internal static bool AllowsLocal(Window window, HwndSource source, object? target)
    {
        for (var node = target as DependencyObject; node is not null; node = Parent(node))
        {
            if (Protected(node)) return false;
            if (node is Visual visual && PresentationSource.FromVisual(visual) is HwndSource input && input != source)
                return false;
            if (ReferenceEquals(node, window)) return true;
        }
        return false;
    }

    internal static bool ProtectsForeground(Window window, HwndSource source, MediaShortcutNative native)
    {
        nint foreground = native.ForegroundWindow;
        // A background window's retained WPF focus is not the current input target.
        if (!native.OwnsWindow(source.Handle, foreground)) return false;
        if (foreground != source.Handle || native.HasMenuOrChildFocus(source.Handle) || InputManager.Current.IsInMenuMode)
            return true;
        bool editor = false;
        for (var node = native.FocusedElement as DependencyObject; node is not null; node = Parent(node))
        {
            editor |= Protected(node);
            if (node is Visual visual && PresentationSource.FromVisual(visual) is HwndSource input && input != source)
                return native.OwnsWindow(source.Handle, input.Handle);
            if (ReferenceEquals(node, window)) return editor;
        }
        return false;
    }

    private static bool Protected(DependencyObject node) =>
        node is PlayerDialogHost or TextBoxBase or PasswordBox or ComboBox { IsEditable: true } or MenuBase or MenuItem or Popup;

    private static DependencyObject? Parent(DependencyObject node) => node switch
    {
        Visual or Visual3D => VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node),
        FrameworkContentElement content => content.Parent,
        ContentElement content => ContentOperations.GetParent(content),
        _ => LogicalTreeHelper.GetParent(node)
    };
}
