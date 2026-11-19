using Microsoft.UI.Xaml;
using Windows.Foundation;
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
    public event EventHandler<FilmCardActivatedEventArgs>? Activated;

    private INotifyPropertyChanged? observed;
    public FilmCard()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Observe();
        Loaded += (_, _) => Observe();
        Unloaded += (_, _) => { if (observed is not null) observed.PropertyChanged -= Changed; observed = null; };
        PosterFrame.SizeChanged += (_, _) => TextBadge.MaxWidth = Math.Max(0, PosterFrame.ActualWidth - 16);
        ActualThemeChanged += (_, _) => Refresh();
        RegisterPropertyChangedCallback(DesktopTheme.IsHighContrastProperty, (_, _) => Refresh());
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
        bool badge = DataContext switch
        {
            FilmCardItem item => item.HasTextBadge && !item.HasScore,
            FilmSearchCardItem item => item.HasTextBadge && !item.HasScore,
            _ => false
        };
        TextBadge.Visibility = badge ? Visibility.Visible : Visibility.Collapsed;
        bool contrast = DesktopTheme.GetIsHighContrast(this);
        TextBadge.Background = contrast ? (Brush)DesktopVisual.Resource(this, "MeowFilm.Brush.Accent") : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 249, 115, 22));
        if (TextBadge.Child is TextBlock text)
            text.Foreground = contrast ? (Brush)DesktopVisual.Resource(this, "MeowFilm.Brush.OnAccent") : new SolidColorBrush(Microsoft.UI.Colors.White);
    }

    private void Activate_Click(object sender, RoutedEventArgs e)
    {

        var item = DataContext switch
        {
            FilmCardItem card => card,
            FilmSearchCardItem search => search.ToCard(),
            _ => null
        };
        if (item is not null)
            Activated?.Invoke(this, new(item));
    }
}
