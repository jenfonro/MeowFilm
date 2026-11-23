using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Capabilities;

public sealed class NovelImportPreviewItem : INotifyPropertyChanged
{
    private bool selected;

    public int Index { get; }
    public string Title { get; }
    public string Detail { get; }
    public string Status { get; }
    public string CheckAutomationName => "选择 " + Title;

    public bool Selected
    {
        get => selected;
        set
        {
            if (selected == value) return;
            selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public NovelImportPreviewItem(int index, string title, string detail, string status, bool selected)
    {
        Index = index;
        Title = title;
        Detail = detail;
        Status = status;
        this.selected = selected;
    }
}

public sealed partial class NovelImportPreviewDialogView : UserControl
{
    private readonly ObservableCollection<NovelImportPreviewItem> items = [];

    internal IReadOnlySet<int> SelectedIndices => items.Where(i => i.Selected).Select(i => i.Index).ToHashSet();

    internal NovelImportPreviewDialogView(
        IReadOnlyList<(string Title, string Detail, string Status)> entries,
        IEnumerable<int>? initiallySelected = null,
        IReadOnlyList<string>? errors = null)
    {
        InitializeComponent();
        var initial = (initiallySelected ?? Enumerable.Range(0, entries.Count)).ToHashSet();
        for (int i = 0; i < entries.Count; i++)
            items.Add(new(i, entries[i].Title, entries[i].Detail, entries[i].Status, initial.Contains(i)));
        PreviewList.ItemsSource = items;
        if (errors is { Count: > 0 })
        {
            ErrorsText.Text = string.Join("\n", errors);
            ErrorsText.Visibility = Visibility.Visible;
        }
        UpdateCount();
    }

    internal void ShowError(string message) => Notices.Show(this, message);

    private void UpdateCount()
    {
        int count = items.Count(i => i.Selected);
        SelectAllCheck.Content = $"全选（{count}/{items.Count}）";
        SelectAllCheck.IsChecked = items.Count > 0 && count == items.Count;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        bool check = SelectAllCheck.IsChecked == true;
        foreach (var item in items) item.Selected = check;
        UpdateCount();
    }

    private void ItemCheck_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: NovelImportPreviewItem item } check)
        {
            item.Selected = check.IsChecked == true;
            UpdateCount();
        }
    }
}
