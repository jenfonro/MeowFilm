using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.ComponentModel;

namespace MeowFilm.Capabilities;

public sealed class FilmCardActivatedEventArgs(FilmCardItem item) : EventArgs
{
    public FilmCardItem Item { get; } = item ?? throw new ArgumentNullException(nameof(item));
}

public partial class FilmCard : UserControl
{
    public event EventHandler<FilmCardActivatedEventArgs>? Activated;

    private INotifyPropertyChanged? observed;
    public IFilmCardDisplay? Item => DataContext as IFilmCardDisplay;
    public Visibility TextBadgeVisibility => Item is { HasTextBadge: true, HasScore: false } ? Visibility.Visible : Visibility.Collapsed;

    public FilmCard()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            Observe();
            Bindings.Update();
        };
        Loaded += (_, _) =>
        {
            Observe();
            Bindings.Update();
        };
        Unloaded += (_, _) =>
        {
            if (IsLoaded) return;
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

    private void Changed(object? sender, PropertyChangedEventArgs e) => Bindings.Update();

    private void Activate_Click(object sender, RoutedEventArgs e)
    {
        if (Item?.ToCard() is { } item)
            Activated?.Invoke(this, new(item));
    }
}
