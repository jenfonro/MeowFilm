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
    private bool sampledPreview;
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
        Refresh();
    }
    private void Changed(object? sender, PropertyChangedEventArgs e) => Refresh();
    private void Refresh()
    {
        bool preview = DataContext is MusicCatalogCard { IsPreviewEntry: true, HasPreview: true };
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
        bool contrast = MeowFilm.Desktop.DesktopTheme.GetIsHighContrast(this);
        var colors = new Windows.UI.ViewManagement.UISettings();
        Foreground = contrast ? new SolidColorBrush(colors.GetColorValue(Windows.UI.ViewManagement.UIColorType.Foreground)) : MusicVisuals.Brush("#FFFFFF");
        Surface.Background = contrast ? new SolidColorBrush(colors.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background)) : entryPalette;
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
        if (contrast)
        {
            ExpansionShade.Background = TitleShade.Background = Surface.Background;
            ExpansionShade.Opacity = 1; ExpansionShade.Visibility = Visibility.Visible;
        }
        else
        {
            ExpansionShade.SetBinding(OpacityProperty, new Microsoft.UI.Xaml.Data.Binding { Source = Surface, Path = new PropertyPath("(MeowFilm.Capabilities.CardMotion.Progress)") });
            TitleShade.Background = MusicEntryTitlePaletteConverter.Create(DecorationLayer.HasTopPalette, DecorationLayer.TopPaletteBrush, Cover.TopPaletteBrush);
        }
    }
    private void UpdateMotion()
    {
        double progress = CardMotion.GetProgress(Surface);
        ContentTranslation.Y = -Math.Max(0, ContentBlock.ActualHeight - CaptionSlot.ActualHeight) * progress;
        PlayButton.IsHitTestVisible = progress > 0;
    }
    private void Open_Click(object sender, RoutedEventArgs e) => OpenRequested?.Invoke(this, e);
    private void Play_Click(object sender, RoutedEventArgs e) => PlayRequested?.Invoke(this, e);
}
