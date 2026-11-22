using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MeowFilm.Desktop;
using Windows.UI.ViewManagement;

namespace MeowFilm.Capabilities;

// The selected playback choice retains its identity independently of pointer and focus states.
public sealed class MusicPlayerMenuChoice : ToggleMenuFlyoutItem
{
    public static readonly DependencyProperty IconKeyProperty = DependencyProperty.Register(nameof(IconKey), typeof(string), typeof(MusicPlayerMenuChoice), new PropertyMetadata(""));
    public string IconKey { get => (string)GetValue(IconKeyProperty); set => SetValue(IconKeyProperty, value); }
    private VisualStateGroup? commonStates;
    public MusicPlayerMenuChoice()
    {
        RegisterPropertyChangedCallback(IsCheckedProperty, (_, _) => Refresh());
        RegisterPropertyChangedCallback(IsEnabledProperty, (_, _) => Refresh());
        GotFocus += (_, _) => Refresh(); LostFocus += (_, _) => Refresh();
        DesktopTheme.Observe(this, Refresh);
    }
    protected override void OnApplyTemplate()
    {
        if (commonStates is not null) commonStates.CurrentStateChanged -= StateChanged;
        base.OnApplyTemplate();
        commonStates = VisualStateManager.GetVisualStateGroups((FrameworkElement)GetTemplateChild("LayoutRoot"))
            .Single(group => group.Name == "CommonStates");
        commonStates.CurrentStateChanged += StateChanged; Refresh();
    }
    private void StateChanged(object sender, VisualStateChangedEventArgs args) => Refresh();
    private void Refresh()
    {
        bool active = IsChecked || FocusState != FocusState.Unfocused || commonStates?.CurrentState?.Name is "PointerOver" or "Pressed";
        if (DesktopTheme.GetIsHighContrast(this))
        {
            Foreground = SystemAppearance.GetElementBrush(!IsEnabled ? UIElementType.GrayText : active ? UIElementType.HighlightText : UIElementType.WindowText);
            Background = SystemAppearance.GetElementBrush(IsEnabled && active ? UIElementType.Highlight : UIElementType.Window);
        }
        else
        {
            Foreground = (Brush)DesktopVisual.Resource(this, IsEnabled ? "MeowFilm.Brush.Text" : "MeowFilm.Brush.Disabled");
            Background = IsEnabled && active ? (Brush)DesktopVisual.Resource(this, IsChecked ? "MeowFilm.Brush.ItemSelected" : "MeowFilm.Brush.ItemHover") : MusicVisuals.Brush("#00000000");
        }
    }
}
