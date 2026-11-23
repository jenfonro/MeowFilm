using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

public sealed class MusicFmChoiceItem(MusicCatalogChoice choice, bool isScene, bool isSelected)
{
    public MusicCatalogChoice Choice { get; } = choice;
    public bool IsScene { get; } = isScene;
    public bool IsSelected { get; } = isSelected;
    public string Name => Choice.Name;
    public string IconKey => Choice.Key;
    public string ItemStatus => IsSelected ? "已选中" : "";
}

public partial class MusicFmPanelView : UserControl
{
    private static readonly ConditionalWeakTable<Button, ChoiceButtonChrome> ButtonChromes = new();
    [ThreadStatic] private static SolidColorBrush? panelBorderBrush, captionBrush, sceneSurfaceBrush;
    private static SolidColorBrush PanelBorderBrush => panelBorderBrush ??= DesktopVisual.Brush("#60FFFFFF");
    private static SolidColorBrush CaptionBrush => captionBrush ??= DesktopVisual.Brush("#C5C3C8");
    private static SolidColorBrush SceneSurfaceBrush => sceneSurfaceBrush ??= DesktopVisual.Brush("#12FFFFFF");
    private readonly MusicPlaybackController playback;
    private readonly Action onCloseRequested;
    private readonly Func<MusicFmChoiceItem, Task> onChoiceSelected;
    private IDisposable? paletteObserver;

    public MusicQueueController Queue { get; }
    public ObservableCollection<MusicFmChoiceItem> ModeChoices { get; } = [];
    public ObservableCollection<MusicFmChoiceItem> SceneChoices { get; } = [];

    internal MusicFmPanelView(
        MusicQueueController queue,
        MusicPlaybackController playback,
        Action onCloseRequested,
        Func<MusicFmChoiceItem, Task> onChoiceSelected)
    {
        Queue = queue;
        this.playback = playback;
        this.onCloseRequested = onCloseRequested;
        this.onChoiceSelected = onChoiceSelected;
        InitializeComponent();
        AttachChoiceChrome(CloseFmButton, selected: false, scene: false, close: true);
        Loaded += (_, _) =>
        {
            paletteObserver?.Dispose();
            paletteObserver = DesktopTheme.Observe(this, RefreshPalette);
            RevealSelection();
        };
        Unloaded += (_, _) =>
        {
            if (IsLoaded) return;
            paletteObserver?.Dispose();
            paletteObserver = null;
        };
    }

    internal void Populate(IReadOnlyList<MusicCatalogChoice> modes, IReadOnlyList<MusicCatalogChoice> scenes)
    {
        ModeChoices.Clear();
        SceneChoices.Clear();
        bool plain = Queue.PlaybackFmRuleId.Length == 0 && Queue.PlaybackFmTabTagId.Length == 0;
        foreach (var choice in modes)
            ModeChoices.Add(new(choice, isScene: false, isSelected: plain && Queue.PlaybackFmMode == choice.Key));
        foreach (var choice in scenes)
            SceneChoices.Add(new(choice, isScene: true, isSelected: plain && Queue.PlaybackFmMode == "scene" && Queue.PlaybackFmScene == choice.Key));
        RefreshPalette();
    }

    internal void Clear()
    {
        ModeChoices.Clear();
        SceneChoices.Clear();
    }

    internal void RefreshPalette()
    {
        bool contrast = DesktopTheme.GetIsHighContrast(PanelBorder);
        PanelBorder.Background = contrast
            ? SystemAppearance.GetElementBrush(UIElementType.Window)
            : playback.PlayerBackground;
        PanelBorder.BorderBrush = contrast
            ? SystemAppearance.GetElementBrush(UIElementType.WindowText)
            : PanelBorderBrush;
        var caption = contrast
            ? SystemAppearance.GetElementBrush(UIElementType.WindowText)
            : CaptionBrush;
        FmTitle.Foreground = caption;
        FmSceneCaption.Foreground = caption;
        FmSceneSurface.Background = contrast
            ? SystemAppearance.GetElementBrush(UIElementType.Window)
            : SceneSurfaceBrush;
    }

    internal void RevealSelection()
    {
        if (!IsLoaded) return;
        var buttons = EnumerateButtons(FmModeChoices).Concat(EnumerateButtons(FmSceneChoices)).ToArray();
        var selected = buttons.FirstOrDefault(button => AutomationProperties.GetItemStatus(button) == "已选中")
            ?? buttons.FirstOrDefault();
        if (selected is null) return;
        selected.Focus(FocusState.Programmatic);
        var point = selected.TransformToVisual(FmScroll).TransformPoint(new global::Windows.Foundation.Point());
        FmScroll.ChangeView(null, Math.Max(0, FmScroll.VerticalOffset + point.Y -
            (FmScroll.ViewportHeight - selected.ActualHeight) / 2), null, true);
    }

    private static IEnumerable<Button> EnumerateButtons(ItemsControl items)
    {
        for (int index = 0; index < items.Items.Count; index++)
        {
            if (items.ContainerFromIndex(index) is DependencyObject container &&
                DesktopVisual.Descendant<Button>(container) is { } button)
                yield return button;
        }
    }

    private void CloseFm_Click(object sender, RoutedEventArgs e) => onCloseRequested();

    private async void Choice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { IsEnabled: true, Tag: MusicFmChoiceItem item })
            await onChoiceSelected(item);
    }

    private void ChoiceButton_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MusicFmChoiceItem item } button) return;
        MusicPlaybackIcon? icon = null;
        TextBlock? label = null;
        if (button.Content is StackPanel panel)
        {
            icon = panel.Children.OfType<MusicPlaybackIcon>().FirstOrDefault();
            label = panel.Children.OfType<TextBlock>().FirstOrDefault();
        }
        else if (button.Content is TextBlock text)
        {
            label = text;
        }
        AttachChoiceChrome(button, item.IsSelected, item.IsScene, close: false, icon: icon, label: label);
    }

    private static void AttachChoiceChrome(Button button, bool selected, bool scene, bool close,
        MusicPlaybackIcon? icon = null, TextBlock? label = null)
    {
        var chrome = ButtonChromes.GetValue(button, static b => new ChoiceButtonChrome(b));
        chrome.Configure(selected, scene, close, icon, label);
    }

    private sealed class ChoiceButtonChrome
    {
        [ThreadStatic] private static SolidColorBrush? fgNormal, fgMuted, bgTransparent, bgSelected, bgScene, bgMode, bgPressed, bgPointer, borderFocused, borderSelected, borderDefault;
        private static SolidColorBrush FgNormal => fgNormal ??= DesktopVisual.Brush("#EBEAED");
        private static SolidColorBrush FgMuted => fgMuted ??= DesktopVisual.Brush("#C5C3C8");
        private static SolidColorBrush BgTransparent => bgTransparent ??= DesktopVisual.Brush("#00000000");
        private static SolidColorBrush BgSelected => bgSelected ??= DesktopVisual.Brush("#32FFFFFF");
        private static SolidColorBrush BgScene => bgScene ??= DesktopVisual.Brush("#0CFFFFFF");
        private static SolidColorBrush BgMode => bgMode ??= DesktopVisual.Brush("#12FFFFFF");
        private static SolidColorBrush BgPressed => bgPressed ??= DesktopVisual.Brush("#55FFFFFF");
        private static SolidColorBrush BgPointer => bgPointer ??= DesktopVisual.Brush("#40FFFFFF");
        private static SolidColorBrush BorderFocused => borderFocused ??= DesktopVisual.Brush("#E0FFFFFF");
        private static SolidColorBrush BorderSelected => borderSelected ??= DesktopVisual.Brush("#C0FFFFFF");
        private static SolidColorBrush BorderDefault => borderDefault ??= DesktopVisual.Brush("#24FFFFFF");

        private readonly Button button;
        private bool selected, scene, close, attached;
        private MusicPlaybackIcon? icon;
        private TextBlock? label;
        private VisualStateGroup? commonStates;
        private long enabledToken;
        private IDisposable? theme;

        internal ChoiceButtonChrome(Button button)
        {
            this.button = button;
            button.Loaded += OnLoaded;
            button.Unloaded += OnUnloaded;
        }

        internal void Configure(bool selected, bool scene, bool close, MusicPlaybackIcon? icon, TextBlock? label)
        {
            this.selected = selected;
            this.scene = scene;
            this.close = close;
            this.icon = icon;
            this.label = label;
            if (button.IsLoaded) Attach();
        }

        private void OnLoaded(object sender, RoutedEventArgs args) => Attach();

        private void OnUnloaded(object sender, RoutedEventArgs args)
        {
            if (button.IsLoaded) return;
            Detach();
        }

        private void Attach()
        {
            button.ApplyTemplate();
            if (VisualTreeHelper.GetChildrenCount(button) > 0 &&
                VisualTreeHelper.GetChild(button, 0) is FrameworkElement root)
            {
                if (commonStates is not null) commonStates.CurrentStateChanged -= StateChanged;
                commonStates = VisualStateManager.GetVisualStateGroups(root)
                    .FirstOrDefault(group => group.Name == "CommonStates");
                if (commonStates is not null) commonStates.CurrentStateChanged += StateChanged;
            }
            if (!attached)
            {
                attached = true;
                button.GotFocus += OnFocusChanged;
                button.LostFocus += OnFocusChanged;
                enabledToken = button.RegisterPropertyChangedCallback(IsEnabledProperty, (_, _) => Refresh());
                theme = DesktopTheme.Observe(button, Refresh);
            }
            else Refresh();
        }

        private void Detach()
        {
            if (commonStates is not null) commonStates.CurrentStateChanged -= StateChanged;
            commonStates = null;
            if (!attached) return;
            attached = false;
            button.GotFocus -= OnFocusChanged;
            button.LostFocus -= OnFocusChanged;
            button.UnregisterPropertyChangedCallback(IsEnabledProperty, enabledToken);
            enabledToken = 0;
            theme?.Dispose();
            theme = null;
        }

        private void OnFocusChanged(object sender, RoutedEventArgs args) => Refresh();
        private void StateChanged(object sender, VisualStateChangedEventArgs args) => Refresh();

        private void Refresh()
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
                    : scene && !selected ? FgMuted : FgNormal;
                button.Background = button.IsEnabled && state == "Pressed" ? BgPressed
                    : button.IsEnabled && state == "PointerOver" ? BgPointer
                    : close ? BgTransparent : selected ? BgSelected : scene ? BgScene : BgMode;
                button.BorderBrush = close ? BgTransparent : focused ? BorderFocused : selected ? BorderSelected : BorderDefault;
            }
            if (icon is not null) icon.Foreground = button.Foreground;
            if (label is not null) label.Foreground = button.Foreground;
        }
    }
}
