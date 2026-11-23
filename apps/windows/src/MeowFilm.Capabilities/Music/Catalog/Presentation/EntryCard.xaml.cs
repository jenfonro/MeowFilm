using System.Collections.ObjectModel;
using MeowFilm.Desktop;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MeowFilm.Capabilities;

public sealed class EntryPreviewRow(string text, Thickness margin)
{
    public string Text { get; } = text;
    public Thickness Margin { get; } = margin;
}

public partial class EntryCard : UserControl
{
    public event RoutedEventHandler? OpenRequested;
    public event RoutedEventHandler? PlayRequested;
    public ObservableCollection<EntryPreviewRow> PreviewItems { get; } = [];
    [ThreadStatic] private static SolidColorBrush? whiteForegroundBrush;
    private static SolidColorBrush WhiteForegroundBrush => whiteForegroundBrush ??= DesktopVisual.Brush("#FFFFFF");

    private INotifyPropertyChanged? observed;
    private IDisposable? theme;
    private readonly MusicCatalogSlot slot;
    private Windows.UI.Color? sampledColor;
    private bool sampledHasColor, sampledPreview, highContrast;
    private Brush entryPalette = MusicEntryPalette.FallbackBrush;
    public MusicCatalogCard? Card => DataContext as MusicCatalogCard;
    public EntryCard()
    {
        InitializeComponent();
        slot = new MusicCatalogSlot(this);
        DataContextChanged += (_, _) => Observe(refresh: true);
        Loaded += (_, _) =>
        {
            Observe(refresh: false);
            theme?.Dispose();
            theme = DesktopTheme.Observe(this, Refresh);
        };
        Unloaded += (_, _) => { if (IsLoaded) return; theme?.Dispose(); theme = null; if (observed is not null) observed.PropertyChanged -= Changed; observed = null; };
        Surface.RegisterPropertyChangedCallback(CardMotion.ProgressProperty, (_, _) => UpdateMotion());
        ArtworkFrame.SizeChanged += (_, args) => DecorationLayer.Height = args.NewSize.Height;
        CaptionHost.SizeChanged += (_, args) => ContentBlock.Width = args.NewSize.Width;
        ContentBlock.SizeChanged += (_, _) => UpdateMotion();
        Cover.RegisterPropertyChangedCallback(MeowFilm.Desktop.Artwork.ArtworkImage.SourceProperty, (_, _) => RefreshPalette());
        Cover.RegisterPropertyChangedCallback(MeowFilm.Desktop.Artwork.ArtworkImage.RawPaletteBrushProperty, (_, _) => RefreshPalette());
        Cover.RegisterPropertyChangedCallback(MeowFilm.Desktop.Artwork.ArtworkImage.TopPaletteBrushProperty, (_, _) => RefreshTitleShade());
        DecorationLayer.RegisterPropertyChangedCallback(MeowFilm.Desktop.Artwork.ArtworkImage.TopPaletteBrushProperty, (_, _) => RefreshTitleShade());
        DecorationLayer.RegisterPropertyChangedCallback(MeowFilm.Desktop.Artwork.ArtworkImage.HasTopPaletteProperty, (_, _) => RefreshTitleShade());
    }
    private void Observe(bool refresh)
    {
        if (observed is not null) observed.PropertyChanged -= Changed;
        observed = IsLoaded ? DataContext as INotifyPropertyChanged : null;
        if (observed is not null) observed.PropertyChanged += Changed;
        Bindings.Update();
        RefreshPreviewRows();
        if (refresh) Refresh();
    }
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(MusicCatalogCard.PreviewTitles))
            RefreshPreviewRows();
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName is nameof(MusicCatalogCard.HasPreview) or
            nameof(MusicCatalogCard.HasLabel) or nameof(MusicCatalogCard.HasPlayCount) or nameof(MusicCatalogCard.HasCoverLabel) or
            nameof(MusicCatalogCard.CanPlay) or nameof(MusicCatalogCard.IsPreviewEntry)) Refresh();
    }
    private void RefreshPreviewRows()
    {
        var titles = (DataContext as MusicCatalogCard)?.PreviewTitles ?? [];
        while (PreviewItems.Count > titles.Count)
            PreviewItems.RemoveAt(PreviewItems.Count - 1);
        for (int index = 0; index < titles.Count; index++)
        {
            var margin = new Thickness(0, 0, index == 0 && titles.Count == 3 ? 0 : 40, 0);
            if (index == PreviewItems.Count) PreviewItems.Add(new(titles[index], margin));
            else if (PreviewItems[index].Text != titles[index] || PreviewItems[index].Margin != margin)
                PreviewItems[index] = new(titles[index], margin);
        }
    }
    private void Refresh()
    {
        var card = DataContext as MusicCatalogCard;
        TitleShade.Visibility = card is { HasLabel: true } or { HasPlayCount: true } or { HasCoverLabel: true }
            ? Visibility.Visible : Visibility.Collapsed;
        bool preview = card is { IsPreviewEntry: true, HasPreview: true };
        PreviewBlock.Visibility = preview ? Visibility.Visible : Visibility.Collapsed;
        CoverShade.Visibility = !preview && DataContext is MusicCatalogCard { CanPlay: true } ? Visibility.Visible : Visibility.Collapsed;
        PlayButton.Visibility = DataContext is MusicCatalogCard { CanPlay: true } ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetRowSpan(PlayButton, preview ? 2 : 1);
        RefreshPalette();
        ExpansionShade.Visibility = preview || highContrast ? Visibility.Visible : Visibility.Collapsed;
        UpdateMotion();
    }
    private void RefreshPalette()
    {
        bool preview = DataContext is MusicCatalogCard { IsPreviewEntry: true };
        bool nextContrast = DesktopTheme.GetIsHighContrast(this);
        bool contrastChanged = highContrast != nextContrast;
        Windows.UI.Color? rawColor = Cover.Source is not null && Cover.RawPaletteBrush is SolidColorBrush rawBrush ? rawBrush.Color : null;
        bool paletteChanged = sampledHasColor != rawColor.HasValue || sampledColor != rawColor || sampledPreview != preview;
        bool changed = paletteChanged || contrastChanged || BottomShade.Background is null;
        if (paletteChanged)
        {
            sampledHasColor = rawColor.HasValue;
            sampledColor = rawColor;
            sampledPreview = preview;
            entryPalette = MusicEntryPalette.Sample(rawColor, preview);
        }
        highContrast = nextContrast;
        if (!changed) return;
        Foreground = highContrast ? SystemAppearance.GetElementBrush(Windows.UI.ViewManagement.UIElementType.WindowText) : WhiteForegroundBrush;
        Surface.Background = highContrast ? SystemAppearance.GetElementBrush(Windows.UI.ViewManagement.UIElementType.Window) : entryPalette;
        var bottom = (Surface.Background as SolidColorBrush)?.Color ?? Microsoft.UI.Colors.Gray;
        BottomShade.Background = new LinearGradientBrush
        {
            StartPoint = new(0, 0), EndPoint = new(0, 1), GradientStops =
            [new GradientStop { Color = Windows.UI.Color.FromArgb(0, bottom.R, bottom.G, bottom.B), Offset = .62 },
             new GradientStop { Color = Windows.UI.Color.FromArgb(112, bottom.R, bottom.G, bottom.B), Offset = .82 },
             new GradientStop { Color = bottom, Offset = 1 }]
        };
        ExpansionShade.Background = highContrast
            ? Surface.Background
            : new LinearGradientBrush
            {
                StartPoint = new(0, 0), EndPoint = new(0, 1), GradientStops =
                [new GradientStop { Color = Windows.UI.Color.FromArgb(0, bottom.R, bottom.G, bottom.B) },
                 new GradientStop { Color = Windows.UI.Color.FromArgb(191, bottom.R, bottom.G, bottom.B), Offset = .32 },
                 new GradientStop { Color = bottom, Offset = .6 }]
            };
        if (contrastChanged || TitleShade.Background is null) RefreshTitleShade();
    }
    private void RefreshTitleShade() => TitleShade.Background = highContrast ? Surface.Background :
        MusicEntryTitlePaletteConverter.Create(DecorationLayer.HasTopPalette, DecorationLayer.TopPaletteBrush, Cover.TopPaletteBrush);
    private void UpdateMotion()
    {
        double progress = CardMotion.GetProgress(Surface);
        CoverShade.Opacity = PreviewBlock.Opacity = PlayButton.Opacity = progress;
        ExpansionShade.Opacity = highContrast ? 1 : progress;
        ContentTranslation.Y = -Math.Max(0, ContentBlock.ActualHeight - CaptionSlot.ActualHeight) * progress;
        PlayButton.IsHitTestVisible = progress > 0;
    }
    private void Open_Click(object sender, RoutedEventArgs e) => OpenRequested?.Invoke(this, e);
    private void Play_Click(object sender, RoutedEventArgs e) => PlayRequested?.Invoke(this, e);
    internal void AttachContext(MusicCatalogContext? context) => slot.SetContext(context);
}
