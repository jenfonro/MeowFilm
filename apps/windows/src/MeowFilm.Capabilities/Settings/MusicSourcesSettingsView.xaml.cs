using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.System;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

public partial class MusicSourcesSettingsView : UserControl
{
    public static readonly DependencyProperty CompactColumnsProperty = DependencyProperty.Register(
        nameof(CompactColumns), typeof(bool), typeof(MusicSourcesSettingsView), new PropertyMetadata(false));
    public bool CompactColumns { get => (bool)GetValue(CompactColumnsProperty); private set => SetValue(CompactColumnsProperty, value); }
    private bool initialized;
    private bool rowsPending;
    private bool networkShowing, editShowing;
    private Task networkPresentation = Task.CompletedTask, editPresentation = Task.CompletedTask;
    private MusicCapability? activeOwner;
    private MusicCapability? rowsOwner;
    private MusicSourceRow? editing;
    private MusicCapability? networkTransferOwner;
    private long networkSession;
    private long networkTransferSession;
    private MusicCapability? Module => DataContext as MusicCapability;
    private bool Active => initialized && IsLoaded && DesktopVisual.IsVisible(this) && IsEnabled && Module is not null;
    private bool Ready => Active && Module is { CanEdit: true };

    public MusicSourcesSettingsView()
    {
        InitializeComponent();
        initialized = true;
        BindRows();
        _ = DesktopVisual.ObserveVisibility(this, _ => UpdateActivation());
        IsEnabledChanged += (_, _) => UpdateActivation();
        SourceList.SelectionChanged += () =>
        {
            if (Module is not { } owner) return;
            foreach (var row in owner.Sources) row.Selected = SourceList.Selected.Contains(row.Key);
        };
        SetColumns();
    }

    private void View_Loaded(object sender, RoutedEventArgs e) => UpdateActivation();
    private void View_Unloaded(object sender, RoutedEventArgs e)
    {
        activeOwner?.RetireSourceManagement();
        activeOwner = null;
        CloseEdit();
        CloseNetwork();
    }

    private void UpdateActivation()
    {
        if (!initialized) return;
        var owner = Active ? Module : null;
        if (ReferenceEquals(owner, activeOwner)) return;
        activeOwner?.RetireSourceManagement();
        CloseEdit();
        CloseNetwork();
        activeOwner = owner;
        if (owner is not null) _ = owner.RefreshSourceManagementAsync();
    }

    private void BindRows()
    {
        if (!initialized || ReferenceEquals(rowsOwner, Module)) return;
        if (rowsOwner is not null) rowsOwner.Sources.CollectionChanged -= Sources_CollectionChanged;
        SourceList.SetRows([], 0, [], "");
        rowsOwner = Module;
        if (rowsOwner is not null) rowsOwner.Sources.CollectionChanged += Sources_CollectionChanged;
        RenderRows();
    }

    private void Sources_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (rowsPending) return;
        rowsPending = true;
        DispatcherQueue.TryEnqueue(() => { rowsPending = false; RenderRows(); });
    }

    private void RenderRows()
    {
        var rows = Module?.Sources.Select(row => new ManagementRow(row.Key, row.Name, "", row.Enabled,
            () => OpenEdit(row), enabled => _ = Module!.SetSourceEnabledAsync(row, enabled), new MenuFlyout(),
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
        new("状态", new(100)), new("平台", new(CompactColumns ? 0 : 332)), new("操作", new(224))
    ]);

    private void View_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs e)
    {
        if (initialized) NetworkAddress.Text = "";
        BindRows();
        UpdateActivation();
    }

    private void View_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!initialized) return;
        bool compact = e.NewSize.Width < 1000;
        if (CompactColumns == compact) return;
        CompactColumns = compact;
        SetColumns();
    }

    private bool Current(Control element, MusicSourceRow row) =>
        Active && element.IsLoaded && DesktopVisual.IsVisible(element) && element.IsEnabled &&
        ReferenceEquals(element.DataContext, row) && Module!.Sources.Contains(row);

    private async void SourceEnabled_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: MusicSourceRow row } box || Module is not { } module) return;
        if (Current(box, row)) await module.SetSourceEnabledAsync(row, box.IsChecked == true);
        box.IsChecked = row.Enabled;
    }
    private void Check_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: MusicSourceRow row } button && Current(button, row))
            _ = Module!.CheckSourcesAsync([row]);
    }
    private void Update_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: MusicSourceRow row } button && Current(button, row) && Ready)
            _ = Module!.UpdateSourceAsync(row);
    }
    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: MusicSourceRow row } button && Current(button, row) && Ready)
            _ = Module!.RemoveSourceAsync(row);
    }
    private void CheckSelected_Click(object sender, RoutedEventArgs e)
    {
        if (Active) _ = Module!.CheckSelectedSourcesAsync();
    }
    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        if (Ready) _ = Module!.RemoveSelectedSourcesAsync();
    }
    private void ImportLocal_Click(object sender, RoutedEventArgs e)
    {
        if (Ready) _ = Module!.ImportSourceAsync();
    }
    private async void ImportNetwork_Click(object sender, RoutedEventArgs e)
    {
        if (!Ready || Module is not { CanEditScripts: true }) return;
        CloseEdit();
        await editPresentation;
        if (!Ready || networkShowing) return;
        networkSession++;
        networkShowing = true;
        try { networkPresentation = DialogPlacement.ShowAsync(this, NetworkPopup); await networkPresentation; }
        finally { networkShowing = false; }
    }
    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (!Ready || !networkShowing || Module is not { CanEditScripts: true } owner) return;
        long session = ++networkSession;
        networkTransferOwner = owner;
        networkTransferSession = session;
        // The owner admits the command asynchronously; a dismissed queued import must
        // also be cancelled when its actual transfer begins.
        PropertyChangedEventHandler cancelRetired = (_, args) =>
        {
            if (args.PropertyName == nameof(MusicCapability.CanCancelSourceTransfer) &&
                (session != networkSession || !networkShowing || !Active || !ReferenceEquals(Module, owner)))
                owner.CancelSourceTransfer();
        };
        owner.PropertyChanged += cancelRetired;
        try
        {
            await owner.ImportSourceFromUrlAsync(NetworkAddress.Text);
            if (session == networkSession && networkShowing && Active &&
                ReferenceEquals(Module, owner) && owner.Status.Length == 0)
                CloseNetwork();
        }
        finally
        {
            owner.PropertyChanged -= cancelRetired;
            if (networkTransferSession == session && ReferenceEquals(networkTransferOwner, owner)) networkTransferOwner = null;
        }
    }
    private void NetworkCancel_Click(object sender, RoutedEventArgs e) => CloseNetwork();
    private void NetworkPopup_Closed(ContentDialog sender, ContentDialogClosedEventArgs e)
    {
        networkSession++;
        networkTransferOwner?.CancelSourceTransfer();
    }
    private void CloseNetwork()
    {
        if (!initialized) return;
        networkSession++;
        networkTransferOwner?.CancelSourceTransfer();
        networkShowing = false;
        NetworkPopup.Hide();
    }

    /// <summary>Edits management facts only: the display name and a network source's address.</summary>
    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: MusicSourceRow row } anchor || !Current(anchor, row) || !Ready) return;
        OpenEdit(row);
    }
    private async void OpenEdit(MusicSourceRow row)
    {
        if (!Ready || !row.IsScript || !Module!.Sources.Contains(row)) return;
        CloseNetwork();
        CloseEdit();
        await Task.WhenAll(networkPresentation, editPresentation);
        if (!Ready || !Module!.Sources.Contains(row)) return;
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
        var address = await Module!.ReadSourceAddressAsync(row);
        if (!ReferenceEquals(editing, row) || !editShowing) return;
        if (address is null) { CloseEdit(); return; }
        EditAddress.Text = address;
        EditAddress.IsEnabled = true;
    }
    private async Task ShowEdit()
    {
        try { await DialogPlacement.ShowAsync(this, EditPopup); }
        finally { editShowing = false; editing = null; }
    }
    private void SourceEnabled_Loaded(object sender, RoutedEventArgs args)
    {
        if (sender is CheckBox { DataContext: MusicSourceRow row } box)
            box.SetBinding(IsEnabledProperty, new Binding { Source = Module,
                Path = new PropertyPath(row.IsBuiltIn ? nameof(MusicCapability.CanEditOfficialSources) : nameof(MusicCapability.CanEditScripts)), Mode = BindingMode.OneWay });
    }
    private void CompactPlatforms_Loaded(object sender, RoutedEventArgs args) =>
        ((FrameworkElement)sender).SetBinding(VisibilityProperty, new Binding { Source = this,
            Path = new PropertyPath(nameof(CompactColumns)), Converter = (IValueConverter)DesktopVisual.Resource(this, "MeowFilm.BoolToVisibility"), Mode = BindingMode.OneWay });
    private void EditName_TextChanged(object sender, TextChangedEventArgs e) =>
        EditSave.IsEnabled = EditName.Text.Trim().Length > 0;
    private void EditSave_Click(object sender, RoutedEventArgs e)
    {
        var row = editing;
        string name = EditName.Text;
        string? address = row?.Kind == MusicSourceKind.Network && EditAddress.IsEnabled ? EditAddress.Text : null;
        CloseEdit();
        if (row is not null && Ready && Module!.Sources.Contains(row)) _ = Module.EditSourceAsync(row, name, address);
    }
    private void EditRollback_Click(object sender, RoutedEventArgs e)
    {
        var row = editing;
        CloseEdit();
        if (row is not null && Ready) _ = Module!.RollbackSourceAsync(row);
    }
    private void EditCancel_Click(object sender, RoutedEventArgs e) => CloseEdit();
    private void CloseEdit()
    {
        editing = null;
        if (initialized) { editShowing = false; EditPopup.Hide(); }
    }
}
