using System.ComponentModel;
using MeowFilm.Desktop;
using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Capabilities;

public partial class FilmView : UserControl, IDesktopHeaderSource
{
    private readonly FilmCapability module;
    private readonly CatalogBrowseView browseView;
    private IDisposable? visibility;
    public FilmCapability Module => module;
    internal FilmPlaybackView PlaybackViewControl => PlaybackView;

    public FilmView(FilmCapability module)
    {
        this.module = module ?? throw new ArgumentNullException(nameof(module));
        browseView = new CatalogBrowseView(module);
        InitializeComponent();
        BrowseHost.Content = browseView;
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

    private async void Playback_RecommendationRequested(object? sender, FilmCardActivatedEventArgs e)
    {
        try { await module.OpenRecommendationAsync(e.Item); }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception error) { Notices.Message = module.OperationErrorText(error); }
    }
}
