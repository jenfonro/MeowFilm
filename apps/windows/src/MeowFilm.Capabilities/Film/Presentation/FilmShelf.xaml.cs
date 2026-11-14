using System.Windows;
using System.Windows.Controls;

namespace MeowFilm.Capabilities;

public partial class FilmShelf : UserControl
{
    internal ScrollViewer? ScrollHost => Cards.ScrollHost;
    internal bool IsSettled => Cards.IsSettled;

    public event EventHandler<FilmCardActivatedEventArgs>? ItemActivated;
    public event EventHandler? RetryRequested;

    public FilmShelf() => InitializeComponent();

    private void Card_Activated(object? sender, FilmCardActivatedEventArgs e) =>
        ItemActivated?.Invoke(this, e);

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        RetryRequested?.Invoke(this, EventArgs.Empty);
    }
}
