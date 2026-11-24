using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Desktop;

public sealed partial class ManagementRowView : Grid
{
    private readonly List<FrameworkElement> customElements = [];
    private ManagementList? owner;
    private ManagementRowState? state;
    private ManagementRow? renderedRow;
    private object? cellsIdentity;
    private bool? detailFailed;
    private bool customCells, observing;

    public ManagementRowView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Bind();
        Loaded += (_, _) => Bind();
        Unloaded += (_, _) => { if (IsLoaded) return; StopObserving(); owner?.DetachColumns(this); owner = null; };
    }

    private void StopObserving()
    {
        if (!observing || state is null) return;
        state.RowChanged -= UpdateRow; state.SelectionChanged -= UpdateSelection; observing = false;
    }

    private void Bind()
    {
        var next = DataContext as ManagementRowState;
        if (!ReferenceEquals(state, next))
        {
            StopObserving();
            if (!ReferenceEquals(owner, next?.Owner)) { owner?.DetachColumns(this); owner = null; }
            state = next;
            renderedRow = null;
            if (next is null)
            {
                ClearCustomCells();
                SetDefaultVisible(false);
                cellsIdentity = null; detailFailed = null; customCells = false;
                return;
            }
        }
        if (state is null) return;
        if (IsLoaded && !observing) { state.RowChanged += UpdateRow; state.SelectionChanged += UpdateSelection; observing = true; }
        if (IsLoaded && owner is null) { owner = state.Owner; owner.AttachColumns(this); }
        state.Owner.ApplyColumns(this);
        UpdateRow(); UpdateSelection();
    }

    private void ClearCustomCells()
    {
        for (int i = 0; i < customElements.Count; i++) Children.Remove(customElements[i]);
        customElements.Clear();
    }

    private void SetDefaultVisible(bool visible)
    {
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        DefaultText.Visibility = EnabledSwitch.Visibility = EditButton.Visibility = MoreButton.Visibility = visibility;
    }

    private void UpdateSelection()
    {
        if (state is not null) RowCheck.IsChecked = state.Selected;
    }

    private void UpdateRow()
    {
        if (state is null) return;
        var row = state.Row;
        var before = renderedRow; renderedRow = row;
        if (before is null || before.Title != row.Title) AutomationProperties.SetName(RowCheck, "选择 " + row.Title);
        bool custom = row.Cells is not null;
        object identity = row.CellsIdentity ?? row;
        if (custom != customCells || custom && !ReferenceEquals(cellsIdentity, identity))
        {
            ClearCustomCells();
            detailFailed = null;
            customCells = custom; cellsIdentity = identity;
        }
        if (custom)
        {
            SetDefaultVisible(false);
            if (customElements.Count > 0) return;
            var cells = row.Cells!();
            if (cells.Count != ColumnDefinitions.Count - 1) throw new InvalidOperationException("Management row cells must match its columns.");
            for (int index = 0; index < cells.Count; index++)
            {
                SetColumn(cells[index], index + 1);
                customElements.Add(cells[index]);
                Children.Add(cells[index]);
            }
            return;
        }
        SetDefaultVisible(true);
        if (before is null || before.Title != row.Title)
        {
            TitleText.Text = row.Title;
            AutomationProperties.SetName(EnabledSwitch, "启用 " + row.Title);
            AutomationProperties.SetName(EditButton, "编辑 " + row.Title);
            AutomationProperties.SetName(MoreButton, row.Title + "更多操作");
        }
        if (before is null || before.Detail != row.Detail)
        {
            DetailText.Text = row.Detail;
            DetailText.Visibility = row.Detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        if (detailFailed != row.Failed)
        {
            detailFailed = row.Failed;
            DetailText.Style = DesktopVisual.TryAppStyle(row.Failed ? "MeowFilm.ErrorText" : "MeowFilm.MutedText");
        }
        if (before is null || before.Enabled != row.Enabled) EnabledSwitch.IsChecked = row.Enabled;
        if (before is null || !ReferenceEquals(before.Menu, row.Menu)) MoreButton.ContextFlyout = row.Menu;
    }

    private void RowCheck_Click(object sender, RoutedEventArgs e) => state?.Select(RowCheck.IsChecked == true);
    private void EnabledSwitch_Click(object sender, RoutedEventArgs e) => state?.Row.Toggle?.Invoke(EnabledSwitch.IsChecked == true);
    private void EditButton_Click(object sender, RoutedEventArgs e) => state?.Row.Edit?.Invoke();
    private void MoreButton_Click(object sender, RoutedEventArgs e) => ManagementList.OpenMenu(MoreButton);
}
