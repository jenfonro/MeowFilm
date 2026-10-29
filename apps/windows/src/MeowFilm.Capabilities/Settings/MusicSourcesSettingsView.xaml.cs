using System.Windows;
using System.Windows.Controls;

namespace MeowFilm.Capabilities;

public partial class MusicSourcesSettingsView : UserControl
{
    private MusicCapability Module => (MusicCapability)DataContext;

    public MusicSourcesSettingsView() => InitializeComponent();

    private async void View_Loaded(object sender, RoutedEventArgs e) =>
        await Module.RefreshOnlineAsync();
    private async void Import_Click(object sender, RoutedEventArgs e) =>
        await Module.ImportSourceAsync();
    private async void Official_Click(object sender, RoutedEventArgs e) =>
        await Module.UseOfficialSourcesAsync();
    private async void Update_Click(object sender, RoutedEventArgs e) =>
        await Module.UpdateSourceAsync();
    private async void Platform_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string platform })
            await Module.UseSourcePlatformAsync(platform);
    }
    private async void SourceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: MusicSourceRow source } &&
            !ReferenceEquals(Module.SelectedSource, source))
        {
            Module.SelectedSource = source;
            await Module.LoadSourceAsync(source);
        }
    }
    private async void SourceEnabled_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: MusicSourceRow source } box) return;
        Module.SelectedSource = source;
        await Module.LoadSourceAsync(source);
        await Module.SourceActionAsync(box.IsChecked == true ? "enable" : "disable");
        box.GetBindingExpression(CheckBox.IsCheckedProperty)?.UpdateTarget();
    }
    private async void Action_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string action })
            await Module.SourceActionAsync(action);
    }
}
