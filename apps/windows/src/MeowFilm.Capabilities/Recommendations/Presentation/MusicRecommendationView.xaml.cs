using System.Windows;
using System.Windows.Controls;

namespace MeowFilm.Capabilities;

public partial class MusicRecommendationView : UserControl
{
    public MusicRecommendationView() => InitializeComponent();
    private async void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MusicCapability module && sender is CheckBox box)
            await module.ChangeRecommendationContinueAsync(box.IsChecked == true);
    }
    private async void Retry_Click(object sender, RoutedEventArgs e)
    { if (DataContext is MusicCapability module) await module.RetryRecommendationAsync(); }
    private async void Return_Click(object sender, RoutedEventArgs e)
    { if (DataContext is MusicCapability module) await module.ReturnRecommendationAsync(); }
    private async void Cancel_Click(object sender, RoutedEventArgs e)
    { if (DataContext is MusicCapability module) await module.CancelRecommendationAsync(); }
    private async void Reconcile_Click(object sender, RoutedEventArgs e)
    { if (DataContext is MusicCapability module) await module.ReconcileRecommendationAsync(); }
}
