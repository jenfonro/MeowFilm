using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MeowFilm.Desktop;
using Windows.UI.ViewManagement;

namespace MeowFilm.Capabilities;

public sealed partial class MusicSourcePickerDialogView : UserControl
{
    private static readonly ConditionalWeakTable<Button, SourceChoiceChrome> ChoiceChromes = new();
    [ThreadStatic] private static SolidColorBrush? transparentBrush;
    private static SolidColorBrush TransparentBrush => transparentBrush ??= DesktopVisual.Brush("#00000000");
    private readonly Action closeRequested;
    private IDisposable? paletteObserver;

    public MusicPlaybackController Playback { get; }

    public MusicSourcePickerDialogView(MusicPlaybackController playback, Action closeRequested)
    {
        Playback = playback;
        this.closeRequested = closeRequested;
        InitializeComponent();
        Loaded += (_, _) =>
        {
            paletteObserver?.Dispose();
            paletteObserver = DesktopTheme.Observe(SourcePanel, RefreshSourcePalette);
        };
        Unloaded += (_, _) =>
        {
            if (IsLoaded) return;
            paletteObserver?.Dispose();
            paletteObserver = null;
        };
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
        button.Visibility = choice.Key == "auto" ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetItemStatus(button, choice.Current ? "当前" : choice.State);
        ChoiceChromes.GetValue(button, static b => new SourceChoiceChrome(b)).Attach();
    }

    private sealed class SourceChoiceChrome
    {
        private readonly Button button;
        private long enabledToken;
        private IDisposable? theme;
        private bool attached;

        internal SourceChoiceChrome(Button button)
        {
            this.button = button;
            button.Unloaded += OnUnloaded;
        }

        internal void Attach()
        {
            if (!attached)
            {
                attached = true;
                theme = DesktopTheme.Observe(button, () => RefreshSourceChoice(button));
                enabledToken = button.RegisterPropertyChangedCallback(IsEnabledProperty, (_, _) => RefreshSourceChoice(button));
            }
            else RefreshSourceChoice(button);
        }

        private void OnUnloaded(object sender, RoutedEventArgs args)
        {
            if (button.IsLoaded) return;
            if (!attached) return;
            attached = false;
            button.UnregisterPropertyChangedCallback(IsEnabledProperty, enabledToken);
            enabledToken = 0;
            theme?.Dispose();
            theme = null;
        }
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
            : current ? (Brush)DesktopVisual.Resource(button, "MeowFilm.Brush.ItemSelected") : TransparentBrush;
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
