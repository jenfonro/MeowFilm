using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MeowFilm.Desktop;
using Windows.UI.ViewManagement;

namespace MeowFilm.Capabilities;

public partial class MusicPlayerView
{
    private void RefreshFmPalette()
    {
        bool contrast = DesktopTheme.GetIsHighContrast(FmPanel);
        if (contrast) FmPanel.Background = SystemAppearance.GetElementBrush(UIElementType.Window);
        else MusicVisuals.Bind(FmPanel, Border.BackgroundProperty, nameof(MusicPlaybackState.PlayerBackground), module.PlaybackState);
        FmPanel.BorderBrush = contrast ? SystemAppearance.GetElementBrush(UIElementType.WindowText) : MusicVisuals.Brush("#60FFFFFF");
        var caption = contrast ? SystemAppearance.GetElementBrush(UIElementType.WindowText) : MusicVisuals.Brush("#C5C3C8");
        ((TextBlock)FmPanel.FindName("FmTitle")).Foreground = caption;
        ((TextBlock)FmPanel.FindName("FmSceneCaption")).Foreground = caption;
        ((Border)FmPanel.FindName("FmSceneSurface")).Background = contrast
            ? SystemAppearance.GetElementBrush(UIElementType.Window) : MusicVisuals.Brush("#12FFFFFF");
    }
    private static void ObserveFmButton(Button button, bool selected, bool scene, bool close = false)
    {
        VisualStateGroup? commonStates = null;
        void Refresh()
        {
            bool focused = button.FocusState != FocusState.Unfocused;
            string? state = commonStates?.CurrentState?.Name;
            if (DesktopTheme.GetIsHighContrast(button))
            {
                bool highlight = button.IsEnabled && (selected || close && (focused || state is "PointerOver" or "Pressed"));
                button.Foreground = SystemAppearance.GetElementBrush(!button.IsEnabled ? UIElementType.GrayText : highlight ? UIElementType.HighlightText : UIElementType.WindowText);
                button.Background = SystemAppearance.GetElementBrush(highlight ? UIElementType.Highlight : UIElementType.Window);
                button.BorderBrush = SystemAppearance.GetElementBrush(!button.IsEnabled ? UIElementType.GrayText : focused ? UIElementType.Highlight : UIElementType.WindowText);
            }
            else
            {
                button.Foreground = !button.IsEnabled ? (Brush)DesktopVisual.Resource(button, "MeowFilm.Brush.Disabled")
                    : MusicVisuals.Brush(scene && !selected ? "#C5C3C8" : "#EBEAED");
                string background = close ? "#00000000" : selected ? "#32FFFFFF" : scene ? "#0CFFFFFF" : "#12FFFFFF";
                if (button.IsEnabled && state == "Pressed") background = "#55FFFFFF";
                else if (button.IsEnabled && state == "PointerOver") background = "#40FFFFFF";
                button.Background = MusicVisuals.Brush(background);
                button.BorderBrush = MusicVisuals.Brush(close ? "#00000000" : focused ? "#E0FFFFFF" : selected ? "#C0FFFFFF" : "#24FFFFFF");
            }
        }
        void StateChanged(object sender, VisualStateChangedEventArgs args) => Refresh();
        button.Loaded += (_, _) =>
        {
            button.ApplyTemplate();
            commonStates = VisualStateManager.GetVisualStateGroups((FrameworkElement)VisualTreeHelper.GetChild(button, 0))
                .Single(group => group.Name == "CommonStates");
            commonStates.CurrentStateChanged += StateChanged; Refresh();
        };
        button.Unloaded += (_, _) =>
        {
            if (commonStates is not null) commonStates.CurrentStateChanged -= StateChanged;
            commonStates = null;
        };
        button.GotFocus += (_, _) => Refresh(); button.LostFocus += (_, _) => Refresh();
        button.RegisterPropertyChangedCallback(IsEnabledProperty, (_, _) => Refresh());
        DesktopTheme.Observe(button, Refresh); Refresh();
    }
}
