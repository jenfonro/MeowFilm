using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace MeowFilm.Capabilities;

public sealed partial class MusicSourcePickerDialogView : UserControl
{
    private readonly Action closeRequested;

    public MusicPlaybackController Playback { get; }

    public MusicSourcePickerDialogView(MusicPlaybackController playback, Action closeRequested)
    {
        Playback = playback;
        this.closeRequested = closeRequested;
        InitializeComponent();
    }

    private void SourceChoice_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ButtonBase { Tag: MusicSourceChoice choice } button) return;
        button.Visibility = choice.Key == "auto" ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetItemStatus(button, choice.Current ? "当前" : choice.State);
    }

    private async void SourceChoice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ButtonBase { Tag: MusicSourceChoice choice })
        {
            closeRequested();
            await Playback.SelectSourceChoiceAsync(choice);
        }
    }

    private async void LoadMoreSources_Click(object sender, RoutedEventArgs e) =>
        await Playback.LoadMoreSourcesAsync();

    private void CloseSources_Click(object sender, RoutedEventArgs e) => closeRequested();
}
