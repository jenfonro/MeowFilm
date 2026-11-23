using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

public partial class FilmView : UserControl, IDesktopHeaderSource
{
    private readonly FilmCapability module;
    private IDisposable? visibility;
    public FilmCapability Module => module;

    public FilmView(FilmCapability module)
    {
        this.module = module ?? throw new ArgumentNullException(nameof(module));
        InitializeComponent();
        Loaded += (_, _) =>
        {
            module.PropertyChanged -= OnModulePropertyChanged;
            module.PropertyChanged += OnModulePropertyChanged;
            module.OperationFailed -= OnModuleOperationFailed;
            module.OperationFailed += OnModuleOperationFailed;
            visibility?.Dispose();
            visibility = DesktopVisual.ObserveVisibility(this, _ => UpdatePresentation());
        };
        Unloaded += (_, _) =>
        {
            if (IsLoaded) return;
            module.PropertyChanged -= OnModulePropertyChanged;
            module.OperationFailed -= OnModuleOperationFailed;
            visibility?.Dispose();
            visibility = null;
        };
    }

    private void OnModuleOperationFailed(object? sender, string message) => Notices.Message = message;
    private void UpdatePresentation()
    {
        if (!DesktopVisual.IsVisible(this)) module.SuspendPresentation(
            hiddenToTray: DesktopWindow.GetWindow(this) is ShellWindow { IsHiddenToTray: true });
        else module.ResumePresentation();
    }

    public DesktopHeaderContribution Header =>
        new(module.Heading) { SearchTargetId = "film" };

    public event EventHandler? HeaderChanged;

    private void OnModulePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) ||
            e.PropertyName is nameof(FilmCapability.Heading) or nameof(FilmCapability.IsHome))
            HeaderChanged?.Invoke(this, EventArgs.Empty);
    }

    private async void More_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: FilmHomeSection section })
            await OpenPlaybackAsync(() => module.OpenDataAsync(section));
    }

    private async void Shelf_ItemActivated(object? sender, FilmCardActivatedEventArgs e)
    {
        if (sender is FilmShelf)
            await OpenPlaybackAsync(() => module.OpenPlaybackAsync(e.Item));
    }

    private async void Shelf_RetryRequested(object? sender, EventArgs e)
    {
        if (sender is FilmShelf { DataContext: FilmHomeSection section })
            await module.RetrySectionAsync(section);
    }

    private async void HomeRefresh_Click(object sender, RoutedEventArgs e)
    {

        if (sender is Button { DataContext: FilmHomeSection section })
            await module.RefreshSectionAsync(section);
    }

    private async void DataRefresh_Click(object sender, RoutedEventArgs e)
    {

        await module.RefreshDataAsync();
    }

    private async void DataRetry_Click(object sender, RoutedEventArgs e)
    {

        await module.RetryDataAsync();
    }

    private async void DataPageAction_Click(object sender, RoutedEventArgs e)
    {

        await module.LoadMoreDataAsync();
    }

    private async void DataCard_Activated(object? sender, FilmCardActivatedEventArgs e)
    {
        if (sender is FilmCard)
            await OpenPlaybackAsync(() => module.OpenPlaybackAsync(e.Item));
    }

    private async void Playback_RecommendationRequested(object? sender, FilmCardActivatedEventArgs e) =>
        await OpenPlaybackAsync(() => module.OpenRecommendationAsync(e.Item));

    private async Task OpenPlaybackAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception error) { Notices.Message = module.OperationErrorText(error); }
    }

    private async void Filter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: FilmFilterOption option } button)
        {
            try { await module.SelectDataFilterAsync(option); }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (Exception error) { Notices.Message = module.OperationErrorText(error); }
            finally { button.IsChecked = option.IsSelected; }
        }
    }
}
