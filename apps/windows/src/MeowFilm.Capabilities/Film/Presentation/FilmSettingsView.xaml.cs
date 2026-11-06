using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using MeowFilm.Capabilities.Film.Rules;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

public partial class FilmSettingsView : UserControl, IDesktopHeaderSource, INotifyPropertyChanged
{
    private readonly FilmRuleService rules;
    private readonly FilmSourceService sources;
    private string section = "rules";
    private bool initialized, observingSources;
    private long activation;
    private Func<Task>? popupAction;
    private FrameworkElement? popupAnchor;

    public FilmSettingsView(FilmRuleService rules, FilmSourceService sources)
    {
        this.rules = rules ?? throw new ArgumentNullException(nameof(rules));
        this.sources = sources ?? throw new ArgumentNullException(nameof(sources));
        InitializeComponent();
        DataContext = this;
        foreach (var editor in RuleEditors)
        {
            editor.ActionRequested += Rule_ActionRequested;
            editor.DraftChanged += (_, _) => InvalidateRuleResults();
        }
        initialized = true;
        AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
        {
            if ((e.VerticalChange != 0 || e.HorizontalChange != 0) && e.OriginalSource is DependencyObject source &&
                (ReferenceEquals(source, RulesScroller) || CatsGrid.IsAncestorOf(source) || VideosGrid.IsAncestorOf(source)))
                ClosePopup();
        }));
    }

    public DesktopHeaderContribution Header => new("设置", SearchVisible: false, SearchEnabled: false);
    public event EventHandler? HeaderChanged { add { } remove { } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private bool Active => initialized && IsLoaded && IsVisible;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    private void Notify(string message) => Notices.Message = message;

    private async void View_Loaded(object sender, RoutedEventArgs e)
    {
        if (!observingSources) { sources.Changed += Sources_Changed; observingSources = true; }
        await ActivateCurrentAsync();
    }

    private void View_Unloaded(object sender, RoutedEventArgs e)
    {
        Deactivate();
        if (observingSources) { sources.Changed -= Sources_Changed; observingSources = false; }
    }

    private async void View_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!initialized) return;
        if (!IsVisible) Deactivate();
        else if (IsLoaded) await ActivateCurrentAsync();
    }

    private void Deactivate()
    {
        activation++;
        ruleOperation?.Cancel();
        sourceOperation?.Cancel();
        ClosePopup(restoreFocus: false);
    }

    private async Task ActivateCurrentAsync()
    {
        if (!Active) return;
        if (section == "rules")
        {
            if ((!RulesLoaded || rulesReloadPending) && !RulesBusy && !RulesFailed) await LoadRulesAsync();
        }
        else if (!SourcesBusy && !SourcesFailed)
        {
            if (!SourcesLoaded || section == "cats" && !catsRefreshed)
                await LoadSourcesAsync(refresh: section == "cats");
            else SynchronizeSources();
        }
    }

    private async void Section_Checked(object sender, RoutedEventArgs e)
    {
        if (!initialized || sender is not RadioButton { Tag: string key } anchor || key == section) return;
        Deactivate();
        section = key;
        RulesPage.Visibility = key == "rules" ? Visibility.Visible : Visibility.Collapsed;
        CatsPage.Visibility = key == "cats" ? Visibility.Visible : Visibility.Collapsed;
        VideosPage.Visibility = key == "videos" ? Visibility.Visible : Visibility.Collapsed;
        anchor.Focus(); anchor.BringIntoView();
        await ActivateCurrentAsync();
    }

    private void Section_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not RadioButton current || e.Key is not (Key.Home or Key.End or Key.Left or Key.Right)) return;
        var anchors = SettingsNavigation.Children.OfType<RadioButton>().ToArray();
        int index = Array.IndexOf(anchors, current);
        int next = e.Key switch { Key.Home => 0, Key.End => anchors.Length - 1,
            Key.Left => (index + anchors.Length - 1) % anchors.Length, _ => (index + 1) % anchors.Length };
        anchors[next].IsChecked = true; anchors[next].Focus(); anchors[next].BringIntoView();
        e.Handled = true;
    }

    private void View_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!initialized) return;
        ClosePopup();
        ContentFrame.Margin = e.NewSize.Width < 700 || e.NewSize.Height < 450
            ? new(16, 0, 16, 8) : new(32, 0, 32, 16);
    }

    private void OpenPopup(FrameworkElement anchor, string title, string message, string accept,
        Func<Task> execute, bool edit = false, FilmCatSource? cat = null, string? importType = null)
    {
        if (!Active) return;
        ClosePopup();
        long ticket = activation;
        popupAnchor = anchor;
        popupAction = () => Active && ticket == activation ? execute() : Task.CompletedTask;
        PopupTitle.Text = title;
        PopupMessage.Text = message;
        PopupMessage.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        PopupFields.Visibility = edit ? Visibility.Visible : Visibility.Collapsed;
        CatNameBox.Text = cat?.Name ?? "";
        CatUrlBox.Text = cat?.SourceUrl ?? "";
        CatUrlField.Visibility = importType == "local" || cat is { SourceType: "local" } ? Visibility.Collapsed : Visibility.Visible;
        ImportTargetField.Visibility = importType is null ? Visibility.Collapsed : Visibility.Visible;
        if (importType is not null)
        {
            ImportTarget.ItemsSource = new[] { new ImportChoice(null, "新猫源") }
                .Concat(sources.Cats.Where(value => value.SourceType == importType)
                    .Select(value => new ImportChoice(value.InstallationId, value.Name))).ToArray();
            ImportTarget.SelectedIndex = 0;
        }
        PopupAccept.Content = accept;
        AutomationProperties.SetName(PopupAccept, title + "：" + accept);
        AutomationProperties.SetName(PopupCancel, title + "：取消");
        AutomationProperties.SetName(PopupSurface, title);
        PopupSurface.Width = Math.Min(400, Math.Max(240, ActualWidth - 32));
        PopupScroller.MaxHeight = Math.Max(100, ActualHeight - 64);
        ActionPopup.IsOpen = true;
    }

    private sealed record ImportChoice(string? InstallationId, string Name);
    private void ImportTarget_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ImportTarget.SelectedItem is not ImportChoice { InstallationId: { } id }) return;
        var cat = sources.Cats.FirstOrDefault(value => value.InstallationId == id);
        if (cat is null) return;
        CatNameBox.Text = cat.Name;
        CatUrlBox.Text = cat.SourceUrl ?? "";
    }

    private void ClosePopup(bool restoreFocus = true)
    {
        if (!initialized) return;
        if (!restoreFocus) popupAnchor = null;
        ActionPopup.IsOpen = false;
        popupAction = null;
    }

    private void ActionPopup_Opened(object? sender, EventArgs e)
    {
        if (PopupFields.Visibility == Visibility.Visible) { CatNameBox.Focus(); CatNameBox.SelectAll(); }
        else PopupCancel.Focus();
    }

    private void ActionPopup_Closed(object? sender, EventArgs e)
    {
        popupAction = null;
        var anchor = popupAnchor;
        popupAnchor = null;
        if (Active && anchor is { IsVisible: true, IsEnabled: true }) anchor.Focus();
    }

    private void ActionPopup_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { ClosePopup(); e.Handled = true; }
        else if (e.Key == Key.Enter && PopupFields.Visibility == Visibility.Visible)
        {
            e.Handled = true;
            PopupAccept_Click(PopupAccept, new());
        }
    }

    private void PopupCancel_Click(object sender, RoutedEventArgs e) => ClosePopup();
    private async void PopupAccept_Click(object sender, RoutedEventArgs e)
    {
        var execute = popupAction;
        if (!ActionPopup.IsOpen || execute is null || !Active) return;
        if (PopupFields.Visibility == Visibility.Visible)
        {
            if (string.IsNullOrWhiteSpace(CatNameBox.Text))
            {
                Notify("请输入猫源名称"); CatNameBox.Focus(); return;
            }
            if (CatUrlField.Visibility == Visibility.Visible && string.IsNullOrWhiteSpace(CatUrlBox.Text))
            {
                Notify("请输入网络 URL"); CatUrlBox.Focus(); return;
            }
        }
        ClosePopup();
        await execute();
    }
}
