using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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
        PanelBorder.Background = DesktopTheme.GetIsHighContrast(PanelBorder)
            ? SystemAppearance.GetElementBrush(UIElementType.Window)
            : playback.PlayerBackground;
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

    private static IEnumerable<ButtonBase> EnumerateButtons(ItemsControl items)
    {
        for (int index = 0; index < items.Items.Count; index++)
        {
            if (items.ContainerFromIndex(index) is DependencyObject container &&
                DesktopVisual.Descendant<ButtonBase>(container) is { } button)
                yield return button;
        }
    }

    private void CloseFm_Click(object sender, RoutedEventArgs e) => onCloseRequested();

    private async void Choice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ButtonBase { IsEnabled: true, Tag: MusicFmChoiceItem item })
            await onChoiceSelected(item);
    }
}
