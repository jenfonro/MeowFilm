using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;

namespace MeowFilm.Capabilities;

public partial class MusicSourcesSettingsView : UserControl
{
    private sealed class NotConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    }
    public static readonly IValueConverter Not = new NotConverter();
    private bool initialized;
    private MusicCapability? activeOwner;
    private MusicSourceRow? editing;
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
            if (e.OriginalSource is not ScrollViewer { Name: "ItemsScroll" } body) return;
            SourceHeader.Width = Math.Max(0, Math.Max(body.ExtentWidth, body.ViewportWidth) -
                SourceHeader.Margin.Left - SourceHeader.Margin.Right);
            HeaderScroll.Width = body.ViewportWidth;
            HeaderScroll.ScrollToHorizontalOffset(body.HorizontalOffset);
            if (e.VerticalChange != 0) CloseEdit();
        }));
    }

    private void View_Loaded(object sender, RoutedEventArgs e) => UpdateActivation();
    private void View_Unloaded(object sender, RoutedEventArgs e)
    {
        activeOwner = null;
        CloseEdit();
    }
    private void View_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateActivation();

    private void UpdateActivation()
    {
        if (!initialized) return;
        var owner = Active ? Module : null;
        if (ReferenceEquals(owner, activeOwner)) return;
        CloseEdit();
        activeOwner = owner;
        if (owner is not null) _ = owner.RefreshSourceManagementAsync();
    }

    private void BindRows()
    {
        if (initialized) SourceList.ItemsSource = Module?.Sources;
    }

    private void View_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        BindRows();
        UpdateActivation();
    }

    private void View_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!initialized) return;
        CloseEdit();
        bool shortLayout = e.NewSize.Height < 450;
        ManagementContent.Margin = shortLayout ? new(12, 8, 12, 8) : new(28, 24, 28, 20);
        SourceTitleRow.Margin = new(0, 0, 0, shortLayout ? 8 : 18);
    }

    private bool Current(FrameworkElement element, MusicSourceRow row) =>
        Active && element.IsLoaded && element.IsVisible && element.IsEnabled &&
        ReferenceEquals(element.DataContext, row) && Module!.Sources.Contains(row);

    private async void SourceEnabled_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: MusicSourceRow row } box || Module is not { } module) return;
        if (Current(box, row)) await module.SetSourceEnabledAsync(row, box.IsChecked == true);
        box.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateTarget();
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
    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (Module is { } module) module.AllSourcesSelected = !module.AllSourcesSelected;
        (sender as CheckBox)?.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateTarget();
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
        if (Active) Module!.CancelSourceTransfer();
    }

    /// <summary>Edits management facts only: the display name and a network source's address.</summary>
    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: MusicSourceRow row } anchor || !Current(anchor, row) || !Ready) return;
        CloseEdit();
        editing = row;
        EditName.Text = row.Name;
        EditAddress.Text = "";
        bool network = row.Kind == MusicSourceKind.Network;
        EditAddressPanel.Visibility = network ? Visibility.Visible : Visibility.Collapsed;
        EditAddress.IsEnabled = !network;
        EditRollback.IsEnabled = row.RollbackAvailable;
        EditPopup.PlacementTarget = anchor;
        EditPopup.IsOpen = true;
        EditName.Focus();
        if (!network) return;
        var address = await Module!.ReadSourceAddressAsync(row);
        if (!ReferenceEquals(editing, row) || !EditPopup.IsOpen) return;
        if (address is null) { CloseEdit(); return; }
        EditAddress.Text = address;
        EditAddress.IsEnabled = true;
    }
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
        if (initialized) EditPopup.IsOpen = false;
    }
}
