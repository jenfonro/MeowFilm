using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace MeowFilm.Capabilities;

public enum FilmSettingsRuleAction { Add, Save, Delete }

public sealed class FilmSettingsRuleActionEventArgs(
    FilmSettingsRuleAction action, FilmSettingsRuleRow? row, Button anchor) : EventArgs
{
    public FilmSettingsRuleAction Action { get; } = action;
    public FilmSettingsRuleRow? Row { get; } = row;
    public Button Anchor { get; } = anchor;
}

public sealed class FilmSettingsRuleRow(string title, int number, string pattern, string replace = "", string flags = "")
    : INotifyPropertyChanged
{
    private string pattern = pattern, replace = replace, preview = "";
    public string Pattern { get => pattern; set { pattern = value; Changed(); } }
    public string Replace { get => replace; set { replace = value; Changed(); } }
    public string Flags { get; } = flags;
    public string Preview { get => preview; set { preview = value; Changed(); } }
    public string PatternLabel => $"{title}第 {number} 条正则";
    public string ReplaceLabel => $"{title}第 {number} 条替换";
    public string SaveLabel => $"保存{title}第 {number} 条";
    public string DeleteLabel => $"删除{title}第 {number} 条";
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public partial class FilmSettingsRuleEditor : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(FilmSettingsRuleEditor), new PropertyMetadata(""));
    public static readonly DependencyProperty HasReplacementProperty = DependencyProperty.RegisterAttached(
        nameof(HasReplacement), typeof(bool), typeof(FilmSettingsRuleEditor),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public bool HasReplacement { get => (bool)GetValue(HasReplacementProperty); set => SetValue(HasReplacementProperty, value); }
    public static bool GetHasReplacement(DependencyObject element) => (bool)element.GetValue(HasReplacementProperty);
    public static void SetHasReplacement(DependencyObject element, bool value) => element.SetValue(HasReplacementProperty, value);
    public ObservableCollection<FilmSettingsRuleRow> Rows { get; } = [];
    public string NewPatternText => NewPattern.Text;
    public string NewReplaceText => NewReplace.Text;
    public event EventHandler<FilmSettingsRuleActionEventArgs>? ActionRequested;
    public event EventHandler? DraftChanged;

    public FilmSettingsRuleEditor()
    {
        InitializeComponent();
        Rows.CollectionChanged += (_, e) =>
        {
            if (e.OldItems is not null)
                foreach (FilmSettingsRuleRow row in e.OldItems) row.PropertyChanged -= Row_Changed;
            if (e.NewItems is not null)
                foreach (FilmSettingsRuleRow row in e.NewItems) row.PropertyChanged += Row_Changed;
            DraftChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    public void ClearNewRule() { NewPattern.Clear(); NewReplace.Clear(); }
    private void Row_Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FilmSettingsRuleRow.Pattern) or nameof(FilmSettingsRuleRow.Replace))
            DraftChanged?.Invoke(this, EventArgs.Empty);
    }
    private void Add_Click(object sender, RoutedEventArgs e) => Raise(FilmSettingsRuleAction.Add, sender);
    private void Save_Click(object sender, RoutedEventArgs e) => Raise(FilmSettingsRuleAction.Save, sender);
    private void Delete_Click(object sender, RoutedEventArgs e) => Raise(FilmSettingsRuleAction.Delete, sender);
    private void Raise(FilmSettingsRuleAction action, object sender)
    {
        if (IsEnabled && IsVisible && sender is Button { IsEnabled: true, IsLoaded: true, IsVisible: true } anchor)
            ActionRequested?.Invoke(this, new(action, anchor.DataContext as FilmSettingsRuleRow, anchor));
    }
}
