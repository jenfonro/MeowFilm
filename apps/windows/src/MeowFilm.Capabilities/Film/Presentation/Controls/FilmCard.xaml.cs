using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.ComponentModel;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

public sealed class FilmCardActivatedEventArgs(FilmCardItem item) : EventArgs
{
    public FilmCardItem Item { get; } = item ?? throw new ArgumentNullException(nameof(item));
}

public partial class FilmCard : UserControl
{
    [ThreadStatic] private static SolidColorBrush? textBadgeBrush, whiteBrush, siteBackgroundBrush, siteBorderBrush, scoreBadgeBrush;
    public event EventHandler<FilmCardActivatedEventArgs>? Activated;

    private INotifyPropertyChanged? observed;
    private IDisposable? theme;
    public IFilmCardDisplay? Item => DataContext as IFilmCardDisplay;
    public Visibility TextBadgeVisibility => Item is { HasTextBadge: true, HasScore: false } ? Visibility.Visible : Visibility.Collapsed;
    public FilmCard()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            Observe();
            Bindings?.Update();
        };
        Loaded += (_, _) =>
        {
            theme?.Dispose();
            theme = DesktopTheme.Observe(this, ApplyTheme);
            Observe();
            Bindings?.Update();
        };
        Unloaded += (_, _) =>
        {
            if (IsLoaded) return;
            theme?.Dispose();
            theme = null;
            if (observed is not null) observed.PropertyChanged -= Changed;
            observed = null;
        };
        PosterFrame.SizeChanged += (_, args) => TextBadge.MaxWidth = args.NewSize.Width * .7;
        SizeChanged += (_, args) => SiteLabel.MaxWidth = args.NewSize.Width;
    }
    private void Observe()
    {
        if (observed is not null) observed.PropertyChanged -= Changed;
        observed = IsLoaded ? DataContext as INotifyPropertyChanged : null;
        if (observed is not null) observed.PropertyChanged += Changed;
    }
    private void Changed(object? sender, PropertyChangedEventArgs e) => Bindings?.Update();
    private void ApplyTheme()
    {
        bool contrast = DesktopTheme.GetIsHighContrast(this);
        TextBadge.Background = contrast ? (Brush)DesktopVisual.Resource(this, "MeowFilm.Brush.Accent") : textBadgeBrush ??= new SolidColorBrush(Windows.UI.Color.FromArgb(255, 249, 115, 22));
        if (TextBadge.Child is TextBlock text)
            text.Foreground = contrast ? (Brush)DesktopVisual.Resource(this, "MeowFilm.Brush.OnAccent") : whiteBrush ??= new SolidColorBrush(Microsoft.UI.Colors.White);
        SiteLabel.Background = contrast ? SystemAppearance.GetElementBrush(Windows.UI.ViewManagement.UIElementType.Window) : siteBackgroundBrush ??= new SolidColorBrush(Windows.UI.Color.FromArgb(255, 248, 250, 252));
        SiteLabel.BorderBrush = contrast ? SystemAppearance.GetElementBrush(Windows.UI.ViewManagement.UIElementType.WindowText) : siteBorderBrush ??= new SolidColorBrush(Windows.UI.Color.FromArgb(255, 226, 232, 240));
        if (IsLoaded) Subtitle.Foreground = (Brush)DesktopVisual.Resource(this, contrast ? "MeowFilm.Brush.Text" : "MeowFilm.Brush.Muted");
        ScoreBadge.Background = contrast ? (Brush)DesktopVisual.Resource(this, "MeowFilm.Brush.Accent") : scoreBadgeBrush ??= new SolidColorBrush(Windows.UI.Color.FromArgb(255, 236, 72, 153));
        ((TextBlock)ScoreBadge.Child).Foreground = contrast ? (Brush)DesktopVisual.Resource(this, "MeowFilm.Brush.OnAccent") : whiteBrush ??= new SolidColorBrush(Microsoft.UI.Colors.White);
    }

    private void Activate_Click(object sender, RoutedEventArgs e)
    {
        if (Item?.ToCard() is { } item)
            Activated?.Invoke(this, new(item));
    }
}
