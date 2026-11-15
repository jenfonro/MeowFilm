using System.Windows;
using System.Windows.Controls;

namespace MeowFilm.Capabilities;

public sealed class FilmCardActivatedEventArgs(FilmCardItem item) : EventArgs
{
    public FilmCardItem Item { get; } = item ?? throw new ArgumentNullException(nameof(item));
}

public partial class FilmCard : UserControl
{
    public event EventHandler<FilmCardActivatedEventArgs>? Activated;

    public FilmCard() => InitializeComponent();

    private void Activate_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
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
