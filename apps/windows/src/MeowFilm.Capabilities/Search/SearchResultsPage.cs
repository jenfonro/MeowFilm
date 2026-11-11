using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

/// <summary>App-level composition of existing music results and film-owned search. Owns no domain requests.</summary>
public sealed class SearchResultsPage : IDesktopSearchPage, INotifyPropertyChanged, IDisposable
{
    private readonly FilmCapability film;
    private readonly NovelCapability? novel;
    private readonly SearchResultsView view;
    private string query = "", tab = "overview", error = "";
    private bool raw, active;
    private string? musicLocation;
    private FilmSearchSession? session;
    private double aggregateScroll, rawScroll, novelScroll;
    private long submission;
    private bool disposed, changingMusic;
    private CancellationTokenSource? novelPresentation;
    private readonly SemaphoreSlim musicGate = new(1, 1);
    public SearchResultsPage(MusicCapability music, FilmCapability film, NovelCapability? novel = null)
    {
        Music = music;
        this.film = film;
        this.novel = novel;
        Tabs = Array.AsReadOnly(new[] { new SearchResultTab("overview", "综合"), new("songs", "单曲"),
            new("playlists", "歌单"), new("film", "影视"), new("novel", "小说") });
        view = new(this);
        film.Search.Changed += FilmChanged;
        music.PropertyChanged += MusicChanged;
        if (novel is not null) novel.Search.Changed += NovelChanged;
    }
    private void NovelChanged(object? sender, EventArgs e) { if (!disposed && active && tab == "novel") view.PublishNovel(); }
    private void FilmChanged(object? sender, EventArgs e)
    {
        if (disposed) return;
        if (film.Search.Current is null) session = null;
        if (ReferenceEquals(session, film.Search.Current)) PublishFilm();
    }
    private void MusicChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!disposed && active && !changingMusic && tab is not ("film" or "novel") &&
            e.PropertyName == nameof(MusicCapability.SearchTab))
        { tab = Music.SearchTab; musicLocation = Music.CaptureNavigationLocation(); RefreshTabs(); }
    }
    public void Dispose()
    {
        if (disposed) return;
        Deactivate(); disposed = true;
        film.Search.Changed -= FilmChanged;
        Music.PropertyChanged -= MusicChanged;
        if (novel is not null) novel.Search.Changed -= NovelChanged;
        NavigateToPlaybackAsync = null; NavigationToken = null;
        NavigateToNovelAsync = null; NovelNavigationToken = null;
    }
    public string Id => "search-results";
    public string Title => "搜索";
    public string Glyph => "\uE721";
    public bool ShowInSidebar => false;
    public FrameworkElement View => view;
    public IDesktopModule ContentOwner => Music;
    internal MusicCapability Music { get; }
    public IReadOnlyList<DesktopSearchTarget> Targets { get; } = [new("music", "音乐"), new("film", "影视"), new("novel", "小说")];
    public string SelectedTarget => tab is "film" or "novel" ? tab : "music";
    public string SelectedTab => tab;
    public string Query => query;
    public IReadOnlyList<SearchResultTab> Tabs { get; }
    public ObservableCollection<FilmCardItem> Cards { get; } = [];
    public string Error => error;
    public bool RawListMode
    {
        get => raw;
        set
        {
            if (raw == value) return;
            RememberScroll(); raw = value; Changed(); PublishFilm(); RestoreScroll();
        }
    }
    public bool CanLoadMore => session?.CanLoadMore == true;
    public bool HasMore => session?.HasMore == true;
    public string MetadataStatus => session?.MetadataState switch
    {
        FilmSearchChannelState.Unavailable => "TMDB 不可用",
        FilmSearchChannelState.Error => "TMDB 请求失败",
        FilmSearchChannelState.Loading => "TMDB 加载中…",
        _ => ""
    };
    public string Status => session is null ? "" : session.Loading ? "搜索中…" :
        session.SitesState == FilmSearchChannelState.Cancelled ? "已取消" : Cards.Count > 0 ? "" :
        session.SitesState == FilmSearchChannelState.NoSources ? "无可用搜索来源" :
        session.SitesState == FilmSearchChannelState.Error ? "搜索不可用" : "无搜索结果";
    public Func<FilmCardItem, CancellationToken, Task>? NavigateToPlaybackAsync { get; set; }
    public Func<CancellationToken>? NavigationToken { get; set; }
    public Func<NovelBook, CancellationToken, Task>? NavigateToNovelAsync { get; set; }
    public Func<CancellationToken>? NovelNavigationToken { get; set; }
    internal NovelCapability? Novel => novel;
    public event PropertyChangedEventHandler? PropertyChanged;

    public async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed, this);
        RetireNovel();
        active = true;
        long operation = ++submission;
        if (tab is not ("film" or "novel") && musicLocation is not null)
            await PresentMusicAsync(operation, query, tab, submit: false, cancellationToken);
        else if (tab == "film" && query.Length > 0) session = film.Search.Start(query, cancellationToken);
        else if (tab == "novel" && novel is not null && query.Length > 0)
            await PresentNovelAsync(operation, query, BeginNovel(cancellationToken));
        if (!IsCurrent(operation, cancellationToken)) return;
        view.Present(); PublishFilm(); RestoreScroll();
    }
    public void Deactivate()
    {
        RememberScroll();
        active = false;
        submission++;
        view.ReleaseMusic();
        _ = film.Search.StopAsync();
        RetireNovel();
        if (novel is not null) novel.SearchWebOwner = null;
    }
    public async Task SubmitAsync(string query, string target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed, this);
        active = true;
        long operation = ++submission;
        query = query.Trim();
        ArgumentException.ThrowIfNullOrEmpty(query);
        if (target is not ("music" or "film" or "novel")) throw new ArgumentException("Unknown search target.", nameof(target));
        RetireNovel();
        RememberScroll();
        if (this.query != query)
        {
            this.query = query;
            aggregateScroll = rawScroll = novelScroll = 0;
            musicLocation = null;
            film.Search.InvalidateQuery(query);
            session = null;
            Cards.Clear();
            Changed(nameof(Query));
        }
        error = ""; Changed(nameof(Error));
        tab = target is "film" or "novel" ? target : "overview";
        RefreshTabs();
        if (target == "film") { session = film.Search.Start(query, token); PublishFilm(); }
        else
        {
            await film.Search.StopAsync();
            if (!IsCurrent(operation, token)) return;
            if (target == "novel") await PresentNovelAsync(operation, query, BeginNovel(token));
            else await PresentMusicAsync(operation, query, "overview", submit: true, token);
        }
        if (IsCurrent(operation, token)) { view.Present(); RestoreScroll(); }
    }
    public async Task OpenTabAsync(string next)
    {
        if (!active || next == tab || !Tabs.Any(item => item.Id == next) || query.Length == 0) return;
        long operation = ++submission;
        string word = query;
        var token = next == "novel" ? NovelNavigationToken?.Invoke() ?? default : NavigationToken?.Invoke() ?? default;
        RememberScroll();
        bool leavingFilm = tab == "film" && next != "film";
        RetireNovel();
        tab = next;
        RefreshTabs();
        error = ""; Changed(nameof(Error));
        if (leavingFilm) await film.Search.StopAsync();
        if (!IsCurrent(operation, token)) return;
        try
        {
            if (next == "film") { session = film.Search.Start(word, token); PublishFilm(); }
            else if (next == "novel") await PresentNovelAsync(operation, word, BeginNovel(token));
            else await PresentMusicAsync(operation, word, next, submit: false, token);
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (IsCurrent(operation, token)) { error = "搜索暂时不可用"; Changed(nameof(Error)); }
        }
        if (IsCurrent(operation, token)) { view.Present(); RestoreScroll(); }
    }
    private CancellationToken BeginNovel(CancellationToken token)
    {
        novelPresentation = CancellationTokenSource.CreateLinkedTokenSource(token);
        return novelPresentation.Token;
    }
    private void RetireNovel()
    {
        var previous = novelPresentation; novelPresentation = null;
        previous?.Cancel(); previous?.Dispose();
        // Retire admission and in-flight pages, not the retained results or cursors.
        novel?.Search.Cancel();
    }
    private bool IsCurrentNovel(long operation, string word, CancellationToken token) =>
        IsCurrent(operation, token) && tab == "novel" && query == word;
    private async Task PresentNovelAsync(long operation, string word, CancellationToken token)
    {
        if (novel is null) throw new InvalidOperationException("Novel search is not composed.");
        await novel.Sources.EnsureReadyAsync(token);
        if (!IsCurrentNovel(operation, word, token)) return;
        StartNovel(operation, word, token);
        view.PublishNovel();
    }
    private void StartNovel(long operation, string word, CancellationToken token)
    {
        _ = Observe();
        async Task Observe()
        {
            try { await novel!.Search.StartAsync(word, token); }
            catch (OperationCanceledException) { }
            catch { if (IsCurrentNovel(operation, word, token)) { error = "小说搜索失败"; Changed(nameof(Error)); } }
        }
    }
    internal async Task LoadMoreNovelAsync()
    {
        if (!active || tab != "novel" || novel is null || novelPresentation is null) return;
        long operation = submission; string word = query; var token = novelPresentation.Token;
        try { await novel.Search.LoadMoreAsync(token); }
        catch (OperationCanceledException) { }
        catch { if (IsCurrentNovel(operation, word, token)) { error = "小说搜索失败"; Changed(nameof(Error)); } }
    }
    public async Task OpenNovelAsync(NovelBook book)
    {
        if (!active || tab != "novel" || novel is null || !novel.Search.Books.Contains(book) || NavigateToNovelAsync is null) return;
        long operation = submission; string word = query;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(NovelNavigationToken?.Invoke() ?? default,
            novelPresentation?.Token ?? default);
        try { await NavigateToNovelAsync(book, lifetime.Token); }
        catch (OperationCanceledException) { }
        catch { if (IsCurrentNovel(operation, word, lifetime.Token)) { error = "无法打开书籍"; Changed(nameof(Error)); } }
    }
    private bool IsCurrent(long operation, CancellationToken token) =>
        !disposed && active && operation == submission && !token.IsCancellationRequested;
    private async Task PresentMusicAsync(long operation, string word, string next, bool submit, CancellationToken token)
    {
        await musicGate.WaitAsync(token);
        try
        {
            if (!IsCurrent(operation, token)) return;
            changingMusic = true;
            if (submit || musicLocation is null) await Music.SearchOnlineAsync(word, token);
            else await Music.RestoreNavigationAsync(musicLocation, token);
            if (!IsCurrent(operation, token)) return;
            await Music.OpenSearchTabAsync(next);
            if (IsCurrent(operation, token)) musicLocation = Music.CaptureNavigationLocation();
        }
        finally { changingMusic = false; musicGate.Release(); }
    }
    public Task LoadMoreAsync() => film.Search.LoadMoreAsync();
    public async Task OpenCardAsync(FilmCardItem item)
    {
        if (!active || tab != "film" || !Cards.Contains(item) || NavigateToPlaybackAsync is null) return;
        try { await NavigateToPlaybackAsync(item, NavigationToken?.Invoke() ?? default); }
        catch (OperationCanceledException) { }
        catch { if (active) { error = "无法打开影片"; Changed(nameof(Error)); } }
    }
    private void PublishFilm()
    {
        error = session?.Error is { Length: > 0 } failure ? FilmPlaybackError.Message(failure, "搜索不可用") : "";
        Changed(nameof(Error));
        var values = (raw ? session?.RawItems : session?.Items) ?? [];
        var cards = values.Select(item => new FilmCardItem(
            FilmSearchJson.Text(item, "id"), FilmSearchJson.Text(item, "title"),
            FilmSearchJson.Text(item, "sourceKind") == "tmdb" ? film.Search.ResolveImage(FilmSearchJson.Text(item, "poster")) : FilmSearchJson.Text(item, "poster"), "")
        {
            TextBadge = FilmSearchJson.Text(item, "textBadge"), Subtitle = FilmSearchJson.Text(item, "siteLabel"),
            SearchContext = new(session!, item)
        }).ToArray();
        for (int index = 0; index < cards.Length; index++)
        {
            if (index < Cards.Count)
            {
                if (!Same(Cards[index], cards[index])) Cards[index] = cards[index];
            }
            else Cards.Add(cards[index]);
        }
        while (Cards.Count > cards.Length) Cards.RemoveAt(Cards.Count - 1);
        foreach (string property in new[] { nameof(CanLoadMore), nameof(HasMore), nameof(MetadataStatus), nameof(Status) }) Changed(property);
        static bool Same(FilmCardItem before, FilmCardItem after) => before.Id == after.Id && before.Title == after.Title &&
            before.Poster == after.Poster && before.TextBadge == after.TextBadge && before.Subtitle == after.Subtitle &&
            before.SearchContext is { } context && after.SearchContext is { } next && context.SameSelection(next);
    }
    private void RefreshTabs()
    {
        foreach (var item in Tabs) item.Select(item.Id == tab);
        Changed(nameof(SelectedTab)); Changed(nameof(SelectedTarget));
        if (active) view.Present();
    }
    private void RememberScroll()
    {
        if (tab == "novel") { novelScroll = view.NovelScroll.VerticalOffset; return; }
        if (tab != "film") return;
        if (raw) rawScroll = view.FilmScroll.VerticalOffset; else aggregateScroll = view.FilmScroll.VerticalOffset;
    }
    private void RestoreScroll()
    {
        if (!active || tab is not ("film" or "novel")) return;
        long operation = submission;
        _ = view.Dispatcher.InvokeAsync(() =>
        {
            if (active && tab == "film" && operation == submission)
                view.FilmScroll.ScrollToVerticalOffset(raw ? rawScroll : aggregateScroll);
            if (active && tab == "novel" && operation == submission)
                view.NovelScroll.ScrollToVerticalOffset(novelScroll);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class SearchResultTab(string id, string label) : INotifyPropertyChanged
{
    public string Id { get; } = id;
    public string Label { get; } = label;
    public bool Selected { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Select(bool value)
    {
        if (Selected == value) return;
        Selected = value;
        PropertyChanged?.Invoke(this, new(nameof(Selected)));
    }
}
