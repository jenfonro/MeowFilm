using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace MeowFilm.Desktop;

/// <summary>Dialogs use the owner's existing XamlRoot, so window movement is ordinary composition.</summary>
public static class DialogPlacement
{
    public static async Task<ContentDialogResult> ShowAsync(FrameworkElement owner, ContentDialog dialog, Func<bool>? canRestoreFocus = null)
    {
        var root = owner.XamlRoot ?? throw new InvalidOperationException("DIALOG_OWNER_NOT_ATTACHED");
        var focus = DesktopVisual.Focused(owner) as UIElement;
        void GettingFocus(UIElement sender, GettingFocusEventArgs args)
        { if (ReferenceEquals(args.NewFocusedElement, focus)) args.TryCancel(); }
        void Closing(ContentDialog sender, ContentDialogClosingEventArgs args)
        {
            if (focus is null) return;
            focus.GettingFocus -= GettingFocus;
            if (!DesktopVisual.IsVisible(owner) || canRestoreFocus?.Invoke() == false)
                focus.GettingFocus += GettingFocus;
        }
        dialog.XamlRoot = root;
        dialog.Closing += Closing;
        var visibility = DesktopVisual.ObserveVisibility(owner, visible => { if (!visible) dialog.Hide(); });
        try { return await dialog.ShowAsync(); }
        finally
        {
            visibility.Dispose();
            dialog.Closing -= Closing;
            if (focus is not null) focus.GettingFocus -= GettingFocus;
        }
    }
}
