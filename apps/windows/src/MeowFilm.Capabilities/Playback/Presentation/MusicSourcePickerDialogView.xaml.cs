using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MeowFilm.Desktop;
using Windows.UI.ViewManagement;

namespace MeowFilm.Capabilities;

public sealed partial class MusicSourcePickerDialogView : UserControl
{
    private readonly Action closeRequested;

    public MusicPlaybackController Playback { get; }

    public MusicSourcePickerDialogView(MusicPlaybackController playback, Action closeRequested)
    {
        Playback = playback;
        this.closeRequested = closeRequested;
        InitializeComponent();
        DesktopTheme.Observe(SourcePanel, RefreshSourcePalette);
    }

    private void RefreshSourcePalette()
    {
        bool contrast = DesktopTheme.GetIsHighContrast(SourcePanel);
        SourcePanel.Background = contrast
            ? SystemAppearance.GetElementBrush(UIElementType.Window)
            : (Brush)DesktopVisual.Resource(SourcePanel, "MeowFilm.Brush.FloatingSurface");
    }

    private void SourceChoice_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MusicSourceChoice choice } button) return;
        button.Loaded -= SourceChoice_Loaded;
        button.Visibility = choice.Key == "auto" ? Visibility.Collapsed : Visibility.Visible;
        var theme = DesktopTheme.Observe(button, () => RefreshSourceChoice(button));
        long enabledToken = button.RegisterPropertyChangedCallback(IsEnabledProperty, (_, _) => RefreshSourceChoice(button));
        void OnUnloaded(object s, RoutedEventArgs args)
        {
            button.Unloaded -= OnUnloaded;
            button.UnregisterPropertyChangedCallback(IsEnabledProperty, enabledToken);
            theme.Dispose();
        }
        button.Unloaded += OnUnloaded;
        RefreshSourceChoice(button);
        AutomationProperties.SetItemStatus(button, choice.Current ? "当前" : choice.State);
    }

    private static void RefreshSourceChoice(Button button)
    {
        bool contrast = DesktopTheme.GetIsHighContrast(button);
        bool current = button.Tag is MusicSourceChoice { Current: true };
        button.Foreground = contrast
            ? SystemAppearance.GetElementBrush(!button.IsEnabled ? UIElementType.GrayText : current ? UIElementType.HighlightText : UIElementType.WindowText)
            : (Brush)DesktopVisual.Resource(button, button.IsEnabled ? "MeowFilm.Brush.Text" : "MeowFilm.Brush.Disabled");
        button.Background = contrast
            ? SystemAppearance.GetElementBrush(button.IsEnabled && current ? UIElementType.Highlight : UIElementType.Window)
            : current ? (Brush)DesktopVisual.Resource(button, "MeowFilm.Brush.ItemSelected") : DesktopVisual.Brush("#00000000");
    }

    private async void SourceChoice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: MusicSourceChoice choice })
        {
            closeRequested();
            await Playback.SelectSourceChoiceAsync(choice);
        }
    }

    private async void LoadMoreSources_Click(object sender, RoutedEventArgs e) =>
        await Playback.LoadMoreSourcesAsync();

    private void CloseSources_Click(object sender, RoutedEventArgs e) => closeRequested();
}
