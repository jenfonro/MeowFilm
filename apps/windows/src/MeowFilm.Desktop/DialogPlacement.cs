using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Desktop;

/// <summary>Dialogs use the owner's existing XamlRoot, so window movement is ordinary composition.</summary>
public static class DialogPlacement
{
    public static async Task<ContentDialogResult> ShowAsync(FrameworkElement owner, ContentDialog dialog)
    {
        var root = owner.XamlRoot ?? throw new InvalidOperationException("DIALOG_OWNER_NOT_ATTACHED");
        var focus = DesktopVisual.Focused(owner) as UIElement;
        dialog.XamlRoot = root;
        var visibility = DesktopVisual.ObserveVisibility(owner, visible => { if (!visible) dialog.Hide(); });
        try { return await dialog.ShowAsync(); }
        finally
        {
            visibility.Dispose();
            if (focus is not null && DesktopVisual.IsVisible(focus)) focus.Focus(FocusState.Programmatic);
        }
    }
}
