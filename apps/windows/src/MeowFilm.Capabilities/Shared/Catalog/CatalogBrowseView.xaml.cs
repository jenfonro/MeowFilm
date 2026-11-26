using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace MeowFilm.Capabilities;

public sealed partial class CatalogBrowseView : UserControl
{
    private readonly ICatalogBrowseController controller;
    private CatalogDataSection? renderedDataSection;

    public ICatalogBrowseController Controller => controller;
    internal ScrollViewer HomeScrollHost => HomeScroll;
    internal ScrollViewer DataScrollHost => DataScroll;

    public CatalogBrowseView(ICatalogBrowseController controller)
    {
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
        InitializeComponent();
        renderedDataSection = controller.BrowseActiveData;
        Loaded += (_, _) =>
        {
            controller.PropertyChanged -= OnControllerPropertyChanged;
            controller.PropertyChanged += OnControllerPropertyChanged;
            SyncDataScrollReset();
        };
        Unloaded += (_, _) =>
        {
            if (IsLoaded) return;
            controller.PropertyChanged -= OnControllerPropertyChanged;
        };
    }

    internal void ResetHomeScroll() => HomeScroll.ChangeView(null, 0, null, true);
    internal void ResetDataScroll() => DataScroll.ChangeView(null, 0, null, true);

    private void OnControllerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(ICatalogBrowseController.BrowseActiveData))
            SyncDataScrollReset();
    }

    private void SyncDataScrollReset()
    {
        var active = controller.BrowseActiveData;
        if (ReferenceEquals(renderedDataSection, active)) return;
        renderedDataSection = active;
        DataScroll.ChangeView(null, 0, null, true);
    }

    private void Shelf_Admitted(object? sender, EventArgs e)
    {
        if (sender is CatalogShelf { Section: { } section })
            controller.OnSectionAdmitted(section);
    }

    private async void More_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CatalogHomeSection section })
            await RunGuardedAsync(() => controller.OpenSectionAsync(section));
    }

    private async void Shelf_ItemActivated(object? sender, CatalogCardActivatedEventArgs e)
    {
        if (sender is CatalogShelf)
            await RunGuardedAsync(() => controller.ActivateCardAsync(e.Item));
    }

    private async void Shelf_RetryRequested(object? sender, EventArgs e)
    {
        if (sender is CatalogShelf { Section: { } section })
            await RunGuardedAsync(() => controller.RetrySectionAsync(section));
    }

    private async void HomeRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: CatalogHomeSection section })
            await RunGuardedAsync(() => controller.RefreshSectionAsync(section));
    }

    private async void HomeAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CatalogHomeSection section })
            await RunGuardedAsync(() => controller.InvokeSectionActionAsync(section, this));
    }

    private async void HomeRetry_Click(object sender, RoutedEventArgs e) =>
        await RunGuardedAsync(controller.RetryHomeAsync);

    private async void HomeImport_Click(object sender, RoutedEventArgs e) =>
        await RunGuardedAsync(() => controller.ImportHomeAsync(this));

    private async void DataRefresh_Click(object sender, RoutedEventArgs e) =>
        await RunGuardedAsync(controller.RefreshDataAsync);

    private async void DataAction_Click(object sender, RoutedEventArgs e) =>
        await RunGuardedAsync(() => controller.InvokeDataActionAsync(this));

    private async void DataRetry_Click(object sender, RoutedEventArgs e) =>
        await RunGuardedAsync(controller.RetryDataAsync);

    private async void DataPageAction_Click(object sender, RoutedEventArgs e) =>
        await RunGuardedAsync(controller.LoadMoreDataAsync);

    private async void DataCard_Activated(object? sender, CatalogCardActivatedEventArgs e)
    {
        if (sender is FilmCard)
            await RunGuardedAsync(() => controller.ActivateCardAsync(e.Item));
    }

    private async void Filter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: CatalogFilterOption option } button)
        {
            try
            {
                await controller.SelectDataFilterAsync(option);
                DataScroll.ChangeView(null, 0, null, true);
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (Exception error) { controller.ReportBrowseError(controller.OperationErrorText(error)); }
            finally { button.IsChecked = option.IsSelected; }
        }
    }

    private async Task RunGuardedAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception error) { controller.ReportBrowseError(controller.OperationErrorText(error)); }
    }
}
