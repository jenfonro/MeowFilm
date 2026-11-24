using System.Collections.ObjectModel;
using System.ComponentModel;
using MeowFilm.Desktop;
using MeowFilm.Desktop.Artwork;
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

    private INotifyPropertyChanged? observed;
    private object? boundContext;
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
        Cover.RegisterPropertyChangedCallback(ArtworkImage.SourceProperty, (_, _) => RefreshPalette());
        Cover.RegisterPropertyChangedCallback(ArtworkImage.RawPaletteBrushProperty, (_, _) => RefreshPalette());
        Cover.RegisterPropertyChangedCallback(ArtworkImage.TopPaletteBrushProperty, (_, _) => RefreshTitleShade());
        DecorationLayer.RegisterPropertyChangedCallback(ArtworkImage.TopPaletteBrushProperty, (_, _) => RefreshTitleShade());
        DecorationLayer.RegisterPropertyChangedCallback(ArtworkImage.HasTopPaletteProperty, (_, _) => RefreshTitleShade());
    }
    private void Observe(bool refresh)
    {
        var current = DataContext;
        if (observed is not null) observed.PropertyChanged -= Changed;
        observed = IsLoaded ? current as INotifyPropertyChanged : null;
        if (observed is not null) observed.PropertyChanged += Changed;
        if (!ReferenceEquals(boundContext, current))
        {
            boundContext = current;
            Bindings.Update();
            RefreshPreviewRows();
        }
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
        CoverShade.Visibility = !preview && card is { CanPlay: true } ? Visibility.Visible : Visibility.Collapsed;
        PlayButton.Visibility = card is { CanPlay: true } ? Visibility.Visible : Visibility.Collapsed;
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
        Foreground = highContrast ? SystemAppearance.GetElementBrush(Windows.UI.ViewManagement.UIElementType.WindowText) : DesktopVisual.WhiteBrush;
        Surface.Background = highContrast ? SystemAppearance.GetElementBrush(Windows.UI.ViewManagement.UIElementType.Window) : entryPalette;
        var bottom = (Surface.Background as SolidColorBrush)?.Color ?? Microsoft.UI.Colors.Gray;
        UpdateVerticalGradient(BottomShade, Windows.UI.Color.FromArgb(0, bottom.R, bottom.G, bottom.B), .62, Windows.UI.Color.FromArgb(112, bottom.R, bottom.G, bottom.B), .82, bottom, 1);
        if (highContrast) ExpansionShade.Background = Surface.Background;
        else UpdateVerticalGradient(ExpansionShade, Windows.UI.Color.FromArgb(0, bottom.R, bottom.G, bottom.B), 0, Windows.UI.Color.FromArgb(191, bottom.R, bottom.G, bottom.B), .32, bottom, .6);
        if (contrastChanged || TitleShade.Background is null) RefreshTitleShade();
    }
    private static void UpdateVerticalGradient(Border target, Windows.UI.Color c0, double o0, Windows.UI.Color c1, double o1, Windows.UI.Color c2, double o2)
    {
        if (target.Background is LinearGradientBrush { GradientStops.Count: 3 } b)
        {
            b.GradientStops[0].Color = c0; b.GradientStops[0].Offset = o0;
            b.GradientStops[1].Color = c1; b.GradientStops[1].Offset = o1;
            b.GradientStops[2].Color = c2; b.GradientStops[2].Offset = o2;
            return;
        }
        target.Background = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(0, 1), GradientStops = [new GradientStop { Color = c0, Offset = o0 }, new GradientStop { Color = c1, Offset = o1 }, new GradientStop { Color = c2, Offset = o2 }] };
    }
    private void RefreshTitleShade()
    {
        if (highContrast) { TitleShade.Background = Surface.Background; return; }
        MusicEntryTitlePaletteConverter.Apply(TitleShade, DecorationLayer.HasTopPalette, DecorationLayer.TopPaletteBrush, Cover.TopPaletteBrush);
    }
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
