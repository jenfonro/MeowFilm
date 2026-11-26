using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Capabilities;

public sealed partial class HistoryPageView : UserControl
{
    public HistoryPage Page { get; }

    public HistoryPageView(HistoryPage page)
    {
        Page = page ?? throw new ArgumentNullException(nameof(page));
        InitializeComponent();
        FilmBrowseHost.Content = new CatalogBrowseView(page.FilmController);
        NovelBrowseHost.Content = new CatalogBrowseView(page.NovelController);
    }

    internal void PostNotice(string message) => MeowFilm.Desktop.NoticeOutlet.Post(this, message);

    private async void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tabId })
            await Page.SelectTabAsync(tabId);
    }
}
