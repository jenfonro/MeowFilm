using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;

namespace MeowFilm.Capabilities;

public partial class MusicSourcesSettingsView : UserControl
{
    private bool initialized;
    private MusicCapability? activeOwner;
    private ContextMenu? menu;
    private MusicCapability? Module => DataContext as MusicCapability;
    private bool Active => initialized && IsLoaded && IsVisible && IsEnabled && Module is not null;
    private bool Ready => Active && Module is { CanEdit: true };

    public MusicSourcesSettingsView()
    {
        InitializeComponent();
        initialized = true;
        BindRows();
        AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
        {
            if (e.VerticalChange != 0) CloseMenu();
        }));
    }

    private void View_Loaded(object sender, RoutedEventArgs e) => UpdateActivation();
    private void View_Unloaded(object sender, RoutedEventArgs e)
    {
        activeOwner = null;
        CloseMenu();
    }
    private void View_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateActivation();

    private void UpdateActivation()
    {
        if (!initialized) return;
        var owner = Active ? Module : null;
        if (ReferenceEquals(owner, activeOwner)) return;
        CloseMenu();
        activeOwner = owner;
        if (owner is not null) _ = owner.RefreshSourceManagementAsync();
    }

    private void BindRows()
    {
        if (!initialized) return;
        SourceList.ItemsSource = Module is not { } module ? null : new CompositeCollection
        {
            new CollectionContainer { Collection = module.OfficialSourceRows },
            new CollectionContainer { Collection = module.Sources }
        };
    }

    private void View_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        BindRows();
        UpdateActivation();
    }

    private void View_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!initialized) return;
        CloseMenu();
        bool shortLayout = e.NewSize.Height < 450;
        ManagementContent.Margin = shortLayout ? new(12, 8, 12, 8) : new(28, 24, 28, 20);
        SourceTitleRow.Margin = new(0, 0, 0, shortLayout ? 8 : 18);
    }

    private bool Current(FrameworkElement element, object row) =>
        Ready && element.IsLoaded && element.IsVisible && element.IsEnabled &&
        ReferenceEquals(element.DataContext, row) &&
        ItemsControl.ContainerFromElement(SourceList, element) is { } container &&
        ReferenceEquals(SourceList.ItemContainerGenerator.ItemFromContainer(container), row);

    private async void OfficialSource_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: MusicOfficialSourceRow row } box || Module is not { } module)
            return;
        if (Current(box, row)) await module.SetOfficialSourceEnabledAsync(row, box.IsChecked == true);
        box.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateTarget();
    }

    private async void SourceEnabled_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: MusicSourceRow row } box || Module is not { } module)
            return;
        if (Current(box, row)) await module.SourceActionAsync(row, box.IsChecked == true ? "enable" : "disable");
        box.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateTarget();
    }

    private void ImportLocal_Click(object sender, RoutedEventArgs e)
    {
        if (Ready) _ = Module!.ImportSourceAsync();
    }

    private void Download_Click(object sender, RoutedEventArgs e)
    {
        if (Ready) _ = Module!.ImportSourceFromUrlAsync(NetworkAddress.Text);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (Ready) _ = Module!.RefreshSourceManagementAsync();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (Active && Module!.CanCancelOnline) _ = Module.CancelOnlineAsync();
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: MusicSourceRow source } anchor ||
            !Current(anchor, source) || Module is not { } owner) return;
        CloseMenu();
        var opened = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        anchor.ContextMenu = opened;
        menu = opened;
        opened.Closed += (_, _) =>
        {
            if (ReferenceEquals(menu, opened)) menu = null;
            if (ReferenceEquals(anchor.ContextMenu, opened)) anchor.ContextMenu = null;
        };
        void Add(string title, Func<Task> action, bool enabled = true)
        {
            var item = new MenuItem { Header = title, IsEnabled = enabled };
            item.Click += async (_, _) =>
            {
                if (opened.IsOpen && ReferenceEquals(menu, opened) && ReferenceEquals(owner, Module) &&
                    Current(anchor, source) && owner.Sources.Any(row => ReferenceEquals(row, source)))
                {
                    var operation = action();
                    CloseMenu();
                    await operation;
                }
            };
            opened.Items.Add(item);
        }
        if (source is { Enabled: true, State: "idle" })
            Add("加载音源", () => owner.SourceActionAsync(source, "enable"));
        Add("更新脚本", () => owner.UpdateSourceAsync(source));
        Add("回退版本", () => owner.SourceActionAsync(source, "rollback"), source.RollbackAvailable);
        Add("删除音源", () => owner.SourceActionAsync(source, "remove"));
        opened.IsOpen = true;
    }

    private void CloseMenu()
    {
        var previous = menu;
        menu = null;
        if (previous is not null) previous.IsOpen = false;
    }
}
