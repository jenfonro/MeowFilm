using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MeowFilm.Capabilities;

public partial class EntryCard : UserControl
{
    public event RoutedEventHandler? OpenRequested;
    public event RoutedEventHandler? PlayRequested;
    private INotifyPropertyChanged? observed;
    private Microsoft.UI.Xaml.Media.Imaging.BitmapSource? sampledSource;
    private bool sampledPreview, highContrast;
    private Brush entryPalette = MusicVisuals.Brush("#808080");
    public EntryCard()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Observe();
        Loaded += (_, _) => Observe(); Unloaded += (_, _) => { if (observed is not null) observed.PropertyChanged -= Changed; observed = null; };
        Surface.RegisterPropertyChangedCallback(CardMotion.ProgressProperty, (_, _) => UpdateMotion());
        ContentBlock.SizeChanged += (_, _) => UpdateMotion();
        RegisterPropertyChangedCallback(MeowFilm.Desktop.DesktopTheme.IsHighContrastProperty, (_, _) => Refresh());
        ActualThemeChanged += (_, _) => Refresh();
        Cover.RegisterPropertyChangedCallback(MeowFilm.Desktop.Artwork.ArtworkImage.SourceProperty, (_, _) => RefreshPalette());
        DecorationLayer.RegisterPropertyChangedCallback(MeowFilm.Desktop.Artwork.ArtworkImage.PaletteBrushProperty, (_, _) => RefreshPalette());
    }
    private void Observe()
    {
        if (observed is not null) observed.PropertyChanged -= Changed;
        observed = DataContext as INotifyPropertyChanged;
        if (observed is not null) observed.PropertyChanged += Changed;
        RefreshPreviewRows();
        Refresh();
    }
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(MusicCatalogCard.PreviewTitles))
            RefreshPreviewRows();
        Refresh();
    }
    private void RefreshPreviewRows()
    {
        var titles = (DataContext as MusicCatalogCard)?.PreviewTitles ?? [];
        while (PreviewRows.Children.Count > titles.Count)
            PreviewRows.Children.RemoveAt(PreviewRows.Children.Count - 1);
        for (int index = 0; index < titles.Count; index++)
        {
            if (index == PreviewRows.Children.Count)
            {
                var label = new TextBlock { FontSize = 13, Height = 25, TextTrimming = TextTrimming.CharacterEllipsis };
                label.SetBinding(TextBlock.ForegroundProperty, new Microsoft.UI.Xaml.Data.Binding
                    { Source = this, Path = new PropertyPath(nameof(Foreground)) });
                PreviewRows.Children.Add(label);
            }
            var row = (TextBlock)PreviewRows.Children[index];
            row.Text = titles[index];
            row.Margin = new Thickness(0, 0, index == 0 && titles.Count == 3 ? 0 : 40, 0);
        }
    }
    private void Refresh()
    {
        var card = DataContext as MusicCatalogCard;
        TitleShade.Visibility = card is { HasLabel: true } or { HasPlayCount: true } or { HasCoverLabel: true }
            ? Visibility.Visible : Visibility.Collapsed;
        bool preview = card is { IsPreviewEntry: true, HasPreview: true };
        PreviewBlock.Visibility = ExpansionShade.Visibility = preview ? Visibility.Visible : Visibility.Collapsed;
        CoverShade.Visibility = !preview && DataContext is MusicCatalogCard { CanPlay: true } ? Visibility.Visible : Visibility.Collapsed;
        PlayButton.Visibility = DataContext is MusicCatalogCard { CanPlay: true } ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetRowSpan(PlayButton, preview ? 2 : 1);
        RefreshPalette(); UpdateMotion();
    }
    private void RefreshPalette()
    {
        bool preview = DataContext is MusicCatalogCard { IsPreviewEntry: true };
        if (!ReferenceEquals(sampledSource, Cover.Source) || sampledPreview != preview)
        { sampledSource = Cover.Source; sampledPreview = preview; entryPalette = MusicEntryPalette.Sample(sampledSource, preview); }
        highContrast = MeowFilm.Desktop.DesktopTheme.GetIsHighContrast(this);
        var colors = new Windows.UI.ViewManagement.UISettings();
        Foreground = highContrast ? new SolidColorBrush(colors.GetColorValue(Windows.UI.ViewManagement.UIColorType.Foreground)) : MusicVisuals.Brush("#FFFFFF");
        Surface.Background = highContrast ? new SolidColorBrush(colors.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background)) : entryPalette;
        var bottom = (Surface.Background as SolidColorBrush)?.Color ?? Microsoft.UI.Colors.Gray;
        BottomShade.Background = new LinearGradientBrush
        {
            StartPoint = new(0, 0), EndPoint = new(0, 1), GradientStops =
            [new GradientStop { Color = Windows.UI.Color.FromArgb(0, bottom.R, bottom.G, bottom.B), Offset = .62 },
             new GradientStop { Color = Windows.UI.Color.FromArgb(112, bottom.R, bottom.G, bottom.B), Offset = .82 },
             new GradientStop { Color = bottom, Offset = 1 }]
        };
        ExpansionShade.Background = new LinearGradientBrush
        {
            StartPoint = new(0, 0), EndPoint = new(0, 1), GradientStops =
            [new GradientStop { Color = Microsoft.UI.Colors.Transparent },
             new GradientStop { Color = (entryPalette as SolidColorBrush)?.Color ?? Microsoft.UI.Colors.Gray, Offset = .6 }]
        };
        if (highContrast)
        {
            ExpansionShade.Background = TitleShade.Background = Surface.Background;
            ExpansionShade.Visibility = Visibility.Visible;
        }
        else
        {
            TitleShade.Background = MusicEntryTitlePaletteConverter.Create(DecorationLayer.HasTopPalette, DecorationLayer.TopPaletteBrush, Cover.TopPaletteBrush);
        }
    }
    private void UpdateMotion()
    {
        double progress = CardMotion.GetProgress(Surface);
        ExpansionShade.Opacity = highContrast ? 1 : progress;
        ContentTranslation.Y = -Math.Max(0, ContentBlock.ActualHeight - CaptionSlot.ActualHeight) * progress;
        PlayButton.IsHitTestVisible = progress > 0;
    }
    private void Open_Click(object sender, RoutedEventArgs e) => OpenRequested?.Invoke(this, e);
    private void Play_Click(object sender, RoutedEventArgs e) => PlayRequested?.Invoke(this, e);
}
