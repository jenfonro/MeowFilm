using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Capabilities;

public enum FilmSettingsRuleAction { Add, Save, Delete }

public sealed class FilmSettingsRuleActionEventArgs(
    FilmSettingsRuleAction action, FilmSettingsRuleRow? row, Button anchor) : EventArgs
{
    public FilmSettingsRuleAction Action { get; } = action;
    public FilmSettingsRuleRow? Row { get; } = row;
    public Button Anchor { get; } = anchor;
}

public sealed class FilmSettingsRuleRow(string title, int number, string pattern, string replace = "", string flags = "",
    bool hasReplacement = false)
    : INotifyPropertyChanged
{
    private string pattern = pattern, replace = replace, flags = flags, preview = "";
    private int number = number;
    public string Pattern { get => pattern; set { if (pattern == value) return; pattern = value; Changed(); } }
    public string Replace { get => replace; set { if (replace == value) return; replace = value; Changed(); } }
    public string Flags => flags;
    public bool HasReplacement { get; } = hasReplacement;
    public int Number => number;
    public string Preview { get => preview; set { if (preview == value) return; preview = value; Changed(); } }
    public string PatternLabel => $"{title}第 {Number} 条正则";
    public string ReplaceLabel => $"{title}第 {Number} 条替换";
    public string SaveLabel => $"保存{title}第 {Number} 条";
    public string DeleteLabel => $"删除{title}第 {Number} 条";
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));

    internal void Update(int nextNumber, string nextPattern, string nextReplace, string nextFlags)
    {
        if (number != nextNumber)
        {
            number = nextNumber;
            Changed(nameof(Number));
            Changed(nameof(PatternLabel));
            Changed(nameof(ReplaceLabel));
            Changed(nameof(SaveLabel));
            Changed(nameof(DeleteLabel));
        }
        if (pattern != nextPattern) { pattern = nextPattern; Changed(nameof(Pattern)); }
        if (replace != nextReplace) { replace = nextReplace; Changed(nameof(Replace)); }
        if (flags != nextFlags) { flags = nextFlags; Changed(nameof(Flags)); }
    }
}

public partial class FilmSettingsRuleEditor : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(FilmSettingsRuleEditor), new PropertyMetadata(""));
    public static readonly DependencyProperty HasReplacementProperty = DependencyProperty.Register(
        nameof(HasReplacement), typeof(bool), typeof(FilmSettingsRuleEditor),
        new PropertyMetadata(false));
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public bool HasReplacement { get => (bool)GetValue(HasReplacementProperty); set => SetValue(HasReplacementProperty, value); }
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

    public void ClearNewRule() { NewPattern.Text = ""; NewReplace.Text = ""; }
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
        if (IsEnabled && MeowFilm.Desktop.DesktopVisual.IsVisible(this) && sender is Button { IsEnabled: true, IsLoaded: true, Visibility: Visibility.Visible } anchor)
            ActionRequested?.Invoke(this, new(action, anchor.DataContext as FilmSettingsRuleRow, anchor));
    }
}
