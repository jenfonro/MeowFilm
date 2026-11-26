using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace MeowFilm.Capabilities;

public sealed class CatalogActionNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => $"{parameter} {value}";
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

internal sealed record CatalogFilterOptionDefinition(string Label, string Value);

internal sealed record CatalogFilterRowDefinition(
    string Key,
    string Label,
    string DefaultValue,
    IReadOnlyList<CatalogFilterOptionDefinition> Options);

public sealed class CatalogFilterOption : INotifyPropertyChanged
{
    private bool isSelected;

    internal CatalogFilterOption(
        string filterKey,
        string filterLabel,
        CatalogFilterOptionDefinition definition,
        bool isSelected)
    {
        FilterKey = filterKey;
        Label = definition.Label;
        Value = definition.Value;
        AutomationName = $"{filterLabel}：{Label}";
        this.isSelected = isSelected;
    }

    internal string FilterKey { get; }
    public string Label { get; }
    public string Value { get; }
    public string AutomationName { get; }
    public bool IsSelected => isSelected;

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Select(bool selected)
    {
        if (isSelected == selected) return;
        isSelected = selected;
        PropertyChanged?.Invoke(this, new(nameof(IsSelected)));
    }
}

public sealed class CatalogFilterRow
{
    internal CatalogFilterRow(CatalogFilterRowDefinition definition)
    {
        Key = definition.Key;
        Label = definition.Label;
        Options = definition.Options.Select(option => new CatalogFilterOption(
            definition.Key,
            definition.Label,
            option,
            option.Value == definition.DefaultValue)).ToArray();
        if (Options.Count(option => option.IsSelected) != 1)
            throw new ArgumentException("Invalid catalog filter default.", nameof(definition));
    }

    public string Key { get; }
    public string Label { get; }
    public IReadOnlyList<CatalogFilterOption> Options { get; }
    internal string SelectedValue => Options.Single(option => option.IsSelected).Value;

    internal bool Contains(CatalogFilterOption option) =>
        option is not null && option.FilterKey == Key && Options.Contains(option);

    internal bool Select(CatalogFilterOption option)
    {
        ArgumentNullException.ThrowIfNull(option);
        if (!Contains(option)) throw new ArgumentException("Unknown catalog filter option.", nameof(option));
        if (option.IsSelected) return false;
        foreach (var candidate in Options)
            candidate.Select(ReferenceEquals(candidate, option));
        return true;
    }
}

public interface ICatalogCardDisplay
{
    string Id { get; }
    string Title { get; }
    string Poster { get; }
    string Score { get; }
    bool HasScore { get; }
    string TextBadge { get; }
    bool HasTextBadge { get; }
    string Subtitle { get; }
    bool HasSubtitle { get; }
    bool ShowPlayMark { get; }
    NovelBook? NovelBook { get; }
    NovelSourceService? NovelSources { get; }
    bool HasSameDisplay(ICatalogCardDisplay other);
}

internal static class CatalogCardCollection
{
    internal static void Present(ObservableCollection<ICatalogCardDisplay> current, IReadOnlyList<ICatalogCardDisplay> desired)
    {
        var unique = new List<ICatalogCardDisplay>(desired.Count);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in desired)
            if (identities.Add(item.Id)) unique.Add(item);
        for (int index = current.Count - 1; index >= 0; index--)
            if (!identities.Contains(current[index].Id)) current.RemoveAt(index);
        for (int index = 0; index < unique.Count; index++)
        {
            var next = unique[index];
            if (index >= current.Count || current[index].Id != next.Id)
            {
                int previous = -1;
                for (int candidate = index + 1; candidate < current.Count; candidate++)
                    if (current[candidate].Id == next.Id) { previous = candidate; break; }
                if (previous >= 0) current.Move(previous, index);
                else current.Insert(index, next);
            }
            if (!current[index].HasSameDisplay(next)) current[index] = next;
        }
        while (current.Count > unique.Count) current.RemoveAt(current.Count - 1);
    }
}

internal enum CatalogSectionState
{
    Pending,
    Loading,
    Ready,
    Failed,
    Cancelled
}

public class CatalogHomeSection : INotifyPropertyChanged
{
    private readonly string defaultFailedText;
    private readonly string emptyText;
    private CatalogSectionState state = CatalogSectionState.Pending;
    private string? customFailedText;

    internal CatalogHomeSection(
        string key,
        string title,
        string defaultFailedText = "栏目加载失败",
        string emptyText = "暂无数据")
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Title = title ?? throw new ArgumentNullException(nameof(title));
        this.defaultFailedText = defaultFailedText;
        this.emptyText = emptyText;
    }

    internal CatalogSectionState State => state;
    internal bool Admitted { get; private set; }
    public double HorizontalOffset { get; set; }

    public string Key { get; }
    public string Title { get; }
    public ObservableCollection<ICatalogCardDisplay> Items { get; } = [];
    public bool HasItems => Items.Count > 0;
    public bool ShowLoading => !HasItems && state is CatalogSectionState.Pending or CatalogSectionState.Loading;
    public bool ShowStatus => !HasItems && !ShowLoading;
    public bool CanRetry => state is CatalogSectionState.Failed or CatalogSectionState.Cancelled;
    public bool CanRefresh => state != CatalogSectionState.Loading;
    public virtual bool ShowRefresh => true;
    public virtual string ActionText => "";
    public virtual bool ShowAction => false;
    public virtual bool CanAction => false;
    public string StatusText => state switch
    {
        CatalogSectionState.Failed => customFailedText ?? defaultFailedText,
        CatalogSectionState.Cancelled => "加载已取消",
        CatalogSectionState.Ready when Items.Count == 0 => emptyText,
        _ => ""
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    internal bool MarkAdmitted()
    {
        if (Admitted) return false;
        Admitted = true;
        return true;
    }

    internal void Begin()
    {
        customFailedText = null;
        state = CatalogSectionState.Loading;
        PublishState();
    }

    protected void CompleteItems(IReadOnlyList<ICatalogCardDisplay> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        customFailedText = null;
        CatalogCardCollection.Present(Items, items);
        state = CatalogSectionState.Ready;
        PublishState();
    }

    internal void Fail(string? failedText = null)
    {
        customFailedText = failedText;
        state = CatalogSectionState.Failed;
        PublishState();
    }

    internal void Cancel()
    {
        state = CatalogSectionState.Cancelled;
        PublishState();
    }

    protected void PublishState()
    {
        Changed(nameof(HasItems));
        Changed(nameof(ShowStatus));
        Changed(nameof(ShowLoading));
        Changed(nameof(CanRetry));
        Changed(nameof(CanRefresh));
        Changed(nameof(ShowAction));
        Changed(nameof(CanAction));
        Changed(nameof(StatusText));
    }

    protected void Changed([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new(property));
}

public class CatalogDataSection : INotifyPropertyChanged
{
    internal static CatalogDataSection Empty { get; } = new("", "", [], "暂无数据", "暂无数据");

    private readonly string defaultFailedText;
    private readonly string emptyText;
    private CatalogSectionState state = CatalogSectionState.Pending;
    private bool hasMore = true;
    private string? customFailedText;

    internal CatalogDataSection(
        string key,
        string title,
        IReadOnlyList<CatalogFilterRow> filterRows,
        string defaultFailedText = "数据加载失败",
        string emptyText = "暂无数据")
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Title = title ?? throw new ArgumentNullException(nameof(title));
        FilterRows = filterRows ?? throw new ArgumentNullException(nameof(filterRows));
        this.defaultFailedText = defaultFailedText;
        this.emptyText = emptyText;
    }

    internal CatalogSectionState State => state;
    internal bool HasMore => hasMore;
    internal bool ReloadPending { get; private set; }

    public string Key { get; }
    public string Title { get; }
    public IReadOnlyList<CatalogFilterRow> FilterRows { get; }
    public Visibility FiltersVisibility => FilterRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public ObservableCollection<ICatalogCardDisplay> Items { get; } = [];
    public bool HasItems => Items.Count > 0;
    public bool ShowLoading => !HasItems && state is CatalogSectionState.Pending or CatalogSectionState.Loading;
    public bool ShowStatus => !HasItems && !ShowLoading;
    public bool CanRetry => !HasItems && state is CatalogSectionState.Failed or CatalogSectionState.Cancelled;
    public bool CanRefresh => state != CatalogSectionState.Loading;
    public virtual bool ShowRefresh => true;
    public virtual string ActionText => "";
    public virtual bool ShowAction => false;
    public virtual bool CanAction => false;
    public bool ShowPageAction => HasItems &&
        (hasMore || state is CatalogSectionState.Loading or CatalogSectionState.Failed or CatalogSectionState.Cancelled);
    public bool CanPage => HasItems && (hasMore || ReloadPending) &&
        state is CatalogSectionState.Ready or CatalogSectionState.Failed or CatalogSectionState.Cancelled;
    public string PageActionText => state switch
    {
        CatalogSectionState.Loading => "加载中…",
        CatalogSectionState.Failed or CatalogSectionState.Cancelled => "重试",
        _ => "加载更多"
    };
    public string StatusText => state switch
    {
        CatalogSectionState.Failed => customFailedText ?? defaultFailedText,
        CatalogSectionState.Cancelled => "加载已取消",
        CatalogSectionState.Ready when Items.Count == 0 => emptyText,
        _ => ""
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    internal bool Contains(CatalogFilterOption option) =>
        FilterRows.Any(row => row.Contains(option));

    protected bool SelectFilterOption(CatalogFilterOption option)
    {
        ArgumentNullException.ThrowIfNull(option);
        var row = FilterRows.SingleOrDefault(candidate => candidate.Contains(option)) ??
            throw new ArgumentException("Unknown catalog filter option.", nameof(option));
        if (!row.Select(option)) return false;
        Items.Clear();
        customFailedText = null;
        ReloadPending = false;
        hasMore = true;
        state = CatalogSectionState.Pending;
        PublishState();
        return true;
    }

    protected void SeedUniqueItems(IEnumerable<ICatalogCardDisplay> items, bool markReady, bool more)
    {
        ArgumentNullException.ThrowIfNull(items);
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
            if (known.Add(item.Id))
                Items.Add(item);
        hasMore = more;
        if (markReady) state = CatalogSectionState.Ready;
        PublishState();
    }

    internal void Begin(bool replace = false)
    {
        customFailedText = null;
        ReloadPending = replace;
        state = CatalogSectionState.Loading;
        PublishState();
    }

    protected int AppendUniqueItems(IReadOnlyList<ICatalogCardDisplay> items, Func<int, bool> moreFromAdded)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(moreFromAdded);
        var known = Items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        int added = 0;
        foreach (var item in items)
            if (known.Add(item.Id))
            {
                Items.Add(item);
                added++;
            }
        customFailedText = null;
        hasMore = moreFromAdded(added);
        ReloadPending = false;
        state = CatalogSectionState.Ready;
        PublishState();
        return added;
    }

    protected void ReplaceAllItems(IReadOnlyList<ICatalogCardDisplay> items, bool more)
    {
        ArgumentNullException.ThrowIfNull(items);
        customFailedText = null;
        CatalogCardCollection.Present(Items, items);
        hasMore = more;
        ReloadPending = false;
        state = CatalogSectionState.Ready;
        PublishState();
    }

    internal void Fail(string? failedText = null)
    {
        customFailedText = failedText;
        state = CatalogSectionState.Failed;
        PublishState();
    }

    internal void Cancel()
    {
        state = CatalogSectionState.Cancelled;
        PublishState();
    }

    protected void PublishState()
    {
        Changed(nameof(HasItems));
        Changed(nameof(ShowStatus));
        Changed(nameof(ShowLoading));
        Changed(nameof(CanRetry));
        Changed(nameof(CanRefresh));
        Changed(nameof(ShowAction));
        Changed(nameof(CanAction));
        Changed(nameof(ShowPageAction));
        Changed(nameof(CanPage));
        Changed(nameof(PageActionText));
        Changed(nameof(StatusText));
    }

    protected void Changed([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new(property));
}

public interface ICatalogBrowseController : INotifyPropertyChanged
{
    bool IsHome { get; }
    bool IsData { get; }
    IEnumerable BrowseSections { get; }
    bool HasSections { get; }
    bool ShowHomeLoading { get; }
    bool ShowHomeStatus { get; }
    string HomeStatusText { get; }
    bool CanRetryHome { get; }
    bool CanImportHome { get; }
    CatalogDataSection BrowseActiveData { get; }
    string DataRefreshAutomationName { get; }
    string DataActionAutomationName => "";
    string DataListAutomationName { get; }
    string DataRetryAutomationName { get; }
    string DataMoreAutomationName { get; }

    void OnSectionAdmitted(CatalogHomeSection section);
    Task OpenSectionAsync(CatalogHomeSection section);
    Task RefreshSectionAsync(CatalogHomeSection section);
    Task InvokeSectionActionAsync(CatalogHomeSection section, FrameworkElement owner) => Task.CompletedTask;
    Task RetrySectionAsync(CatalogHomeSection section);
    Task RetryHomeAsync();
    Task ImportHomeAsync(FrameworkElement owner);
    Task ActivateCardAsync(ICatalogCardDisplay card);
    Task RefreshDataAsync();
    Task InvokeDataActionAsync(FrameworkElement owner) => Task.CompletedTask;
    Task RetryDataAsync();
    Task LoadMoreDataAsync();
    Task SelectDataFilterAsync(CatalogFilterOption option);
    string OperationErrorText(Exception error);
    void ReportBrowseError(string message);
}
