using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

public partial class MusicSourcesSettingsView : UserControl
{
    private readonly HashSet<FrameworkElement> compactPlatformLists = [];
    private IDisposable? visibility;
    private bool compactColumns;
    private bool rowsPending;
    private bool networkShowing, editShowing;
    private Task networkPresentation = Task.CompletedTask, editPresentation = Task.CompletedTask;
    private MusicSettingsController? activeOwner;
    private MusicSettingsController? rowsOwner;
    private MusicSourceRow? editing;
    private MusicSettingsController? networkTransferOwner;
    private long networkSession;
    private long networkTransferSession;
    public MusicSettingsController? Settings { get; private set; }
    private bool Visible => IsLoaded && DesktopVisual.IsVisible(this) && Settings is not null;
    private bool Active => Visible && IsEnabled;
    private bool Ready => Active && Settings is { CanEdit: true };

    public MusicSourcesSettingsView()
    {
        InitializeComponent();
        ModalStash.Children.Clear();
        IsEnabledChanged += (_, _) => UpdateActivation();
        SourceList.SelectionChanged += () => Settings?.SetSelectedSources(SourceList.Selected);
        SetColumns();
    }

    internal void Bind(MusicSettingsController settings)
    {
        bool changed = !ReferenceEquals(Settings, settings);
        Settings = settings;
        NetworkAddress.Text = "";
        Bindings.Update();
        if (changed)
        {
            UnbindRows();
            SourceList.SetRows([], 0, [], "");
        }
        UpdateActivation();
    }

    private void View_Loaded(object sender, RoutedEventArgs e)
    {
        visibility?.Dispose();
        visibility = DesktopVisual.ObserveVisibility(this, _ => UpdateActivation());
        UpdateActivation();
    }
    private void View_Unloaded(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) return;
        visibility?.Dispose();
        visibility = null;
        UnbindRows();
        activeOwner?.RetireSourceManagement();
        activeOwner = null;
        CloseEdit();
        CloseNetwork();
    }

    private void UpdateActivation()
    {
        SyncRowSubscription();
        var owner = Active ? Settings : null;
        if (ReferenceEquals(owner, activeOwner)) return;
        activeOwner?.RetireSourceManagement();
        CloseEdit();
        CloseNetwork();
        activeOwner = owner;
        if (owner is not null) _ = owner.RefreshSourceManagementAsync();
    }

    private void SyncRowSubscription()
    {
        if (Visible) BindRows();
        else UnbindRows();
    }

    private void BindRows()
    {
        if (!Visible || ReferenceEquals(rowsOwner, Settings)) return;
        UnbindRows();
        rowsOwner = Settings;
        if (rowsOwner is not null) rowsOwner.Sources.CollectionChanged += Sources_CollectionChanged;
        RenderRows();
    }

    private void UnbindRows()
    {
        if (rowsOwner is not null) rowsOwner.Sources.CollectionChanged -= Sources_CollectionChanged;
        rowsOwner = null;
        rowsPending = false;
    }

    private void Sources_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (rowsPending || !Visible) return;
        rowsPending = DispatcherQueue.TryEnqueue(() =>
        {
            rowsPending = false;
            if (Visible) RenderRows();
        });
    }

    private void RenderRows()
    {
        var rows = Settings?.Sources.Select(row => new ManagementRow(row.Key, row.Name, "", row.Enabled,
            Cells: () => SourceCells(row), CellsIdentity: row)).ToArray() ?? [];
        SourceList.SetRows(rows, rows.Length, rows.Select(row => row.Id), "");
    }

    private IReadOnlyList<FrameworkElement> SourceCells(MusicSourceRow row) =>
        new[] { "Source.Enabled", "Source.Name", "Source.Type", "Source.Health", "Source.Platforms", "Source.Actions" }
            .Select(key =>
            {
                var cell = (FrameworkElement)((DataTemplate)DesktopVisual.Resource(this, key)).LoadContent();
                cell.DataContext = row;
                return cell;
            }).ToArray();

    private void SetColumns() => SourceList.SetColumns([
        new("启用", new(48)), new("名称", new(1, GridUnitType.Star)), new("类型", new(48)),
        new("状态", new(100)), new("平台", new(compactColumns ? 0 : 332)), new("操作", new(224))
    ]);

    private void View_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool compact = e.NewSize.Width < 1000;
        if (compactColumns == compact) return;
        compactColumns = compact;
        SetColumns();
        var visibility = compactColumns ? Visibility.Visible : Visibility.Collapsed;
        foreach (var items in compactPlatformLists) items.Visibility = visibility;
    }

    private void CompactPlatforms_Loaded(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement element) return;
        compactPlatformLists.Add(element);
        element.Unloaded -= CompactPlatforms_Unloaded;
        element.Unloaded += CompactPlatforms_Unloaded;
        element.Visibility = compactColumns ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CompactPlatforms_Unloaded(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement element && !element.IsLoaded)
            compactPlatformLists.Remove(element);
    }

    private bool Current(Control element, MusicSourceRow row) =>
        Active && element.IsLoaded && DesktopVisual.IsVisible(element) && element.IsEnabled &&
        ReferenceEquals(element.DataContext, row) && Settings!.Sources.Contains(row);

    private async void SourceEnabled_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: MusicSourceRow row } box || Settings is not { } owner) return;
        try
        {
            if (Current(box, row)) await owner.SetSourceEnabledAsync(row, box.IsChecked == true);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (IsLoaded) _ = owner.Notify(MusicRuntimeContext.ErrorText(error));
        }
        if (box.IsLoaded && ReferenceEquals(box.DataContext, row))
            box.IsChecked = owner.Sources.FirstOrDefault(item => item.Key == row.Key)?.Enabled ?? row.Enabled;
    }
    private void Check_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: MusicSourceRow row } button && Current(button, row))
            _ = Settings!.CheckSourcesAsync([row]);
    }
    private void Update_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: MusicSourceRow row } button && Current(button, row) && Ready)
            _ = Settings!.UpdateSourceAsync(row);
    }
    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: MusicSourceRow row } button && Current(button, row) && Ready)
            _ = Settings!.RemoveSourceAsync(row);
    }
    private void CheckSelected_Click(object sender, RoutedEventArgs e)
    {
        if (Active) _ = Settings!.CheckSelectedSourcesAsync();
    }
    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        if (Ready) _ = Settings!.RemoveSelectedSourcesAsync();
    }
    private void ImportLocal_Click(object sender, RoutedEventArgs e)
    {
        if (Ready) _ = Settings!.ImportSourceAsync();
    }
    private async void ImportNetwork_Click(object sender, RoutedEventArgs e)
    {
        if (!Ready || Settings is not { CanEditScripts: true }) return;
        CloseEdit();
        try { await editPresentation; }
        catch (OperationCanceledException) { }
        if (!Ready || networkShowing) return;
        networkSession++;
        networkShowing = true;
        try
        {
            networkPresentation = DesktopModalHost.ShowSurfaceAsync(
                this,
                NetworkPopupSurface,
                onOpened: () => { NetworkAddress.Focus(FocusState.Programmatic); NetworkAddress.SelectAll(); });
            await networkPresentation;
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (IsLoaded && Settings is { } owner) _ = owner.Notify(MusicRuntimeContext.ErrorText(error));
        }
        finally
        {
            networkShowing = false;
            networkSession++;
            networkTransferOwner?.CancelSourceTransfer();
        }
    }
    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (!Ready || !networkShowing || Settings is not { CanEditScripts: true } owner) return;
        long session = ++networkSession;
        networkTransferOwner = owner;
        networkTransferSession = session;
        // The owner admits the command asynchronously; a dismissed queued import must
        // also be cancelled when its actual transfer begins.
        PropertyChangedEventHandler cancelRetired = (_, args) =>
        {
            if (args.PropertyName == nameof(MusicSettingsController.CanCancelSourceTransfer) &&
                (session != networkSession || !networkShowing || !Active || !ReferenceEquals(Settings, owner)))
                owner.CancelSourceTransfer();
        };
        owner.PropertyChanged += cancelRetired;
        try
        {
            await owner.ImportSourceFromUrlAsync(NetworkAddress.Text);
            if (session == networkSession && networkShowing && Active &&
                ReferenceEquals(Settings, owner) && owner.Status.Length == 0)
                CloseNetwork();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (IsLoaded) _ = owner.Notify(MusicRuntimeContext.ErrorText(error));
        }
        finally
        {
            owner.PropertyChanged -= cancelRetired;
            if (networkTransferSession == session && ReferenceEquals(networkTransferOwner, owner)) networkTransferOwner = null;
        }
    }
    private void NetworkCancel_Click(object sender, RoutedEventArgs e) => CloseNetwork();
    private void CloseNetwork()
    {
        networkSession++;
        networkTransferOwner?.CancelSourceTransfer();
        networkShowing = false;
        DesktopModalHost.CloseFor(this, NetworkPopupSurface);
    }

    /// <summary>Edits management facts only: the display name and a network source's address.</summary>
    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: MusicSourceRow row } anchor || !Current(anchor, row) || !Ready) return;
        OpenEdit(row);
    }
    private async void OpenEdit(MusicSourceRow row)
    {
        if (!Ready || !row.IsScript || !Settings!.Sources.Contains(row)) return;
        CloseNetwork();
        CloseEdit();
        try
        {
            await Task.WhenAll(networkPresentation, editPresentation);
            if (!Ready || !Settings!.Sources.Contains(row)) return;
            editing = row;
            EditName.Text = row.Name;
            EditAddress.Text = "";
            bool network = row.Kind == MusicSourceKind.Network;
            EditAddressPanel.Visibility = network ? Visibility.Visible : Visibility.Collapsed;
            EditAddress.IsEnabled = !network;
            EditRollback.IsEnabled = row.RollbackAvailable;
            editShowing = true;
            editPresentation = ShowEdit();
            if (!network) return;
            var address = await Settings!.ReadSourceAddressAsync(row);
            if (!ReferenceEquals(editing, row) || !editShowing) return;
            if (address is null) { CloseEdit(); return; }
            EditAddress.Text = address;
            EditAddress.IsEnabled = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            CloseEdit();
            if (IsLoaded && Settings is { } owner) _ = owner.Notify(MusicRuntimeContext.ErrorText(error));
        }
    }
    private async Task ShowEdit()
    {
        try
        {
            await DesktopModalHost.ShowSurfaceAsync(
                this,
                EditPopupSurface,
                onOpened: () => { EditName.Focus(FocusState.Programmatic); EditName.SelectAll(); });
        }
        catch (OperationCanceledException) { }
        finally { editShowing = false; editing = null; }
    }
    private void EditName_TextChanged(object sender, TextChangedEventArgs e) =>
        EditSave.IsEnabled = EditName.Text.Trim().Length > 0;
    private void EditSave_Click(object sender, RoutedEventArgs e)
    {
        var row = editing;
        string name = EditName.Text;
        string? address = row?.Kind == MusicSourceKind.Network && EditAddress.IsEnabled ? EditAddress.Text : null;
        CloseEdit();
        if (row is not null && Ready && Settings!.Sources.Contains(row)) _ = Settings.EditSourceAsync(row, name, address);
    }
    private void EditRollback_Click(object sender, RoutedEventArgs e)
    {
        var row = editing;
        CloseEdit();
        if (row is not null && Ready) _ = Settings!.RollbackSourceAsync(row);
    }
    private void EditCancel_Click(object sender, RoutedEventArgs e) => CloseEdit();
    private void CloseEdit()
    {
        editing = null;
        editShowing = false;
        DesktopModalHost.CloseFor(this, EditPopupSurface);
    }
}
