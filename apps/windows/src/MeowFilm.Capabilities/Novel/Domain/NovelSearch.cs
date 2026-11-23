using System.Collections.ObjectModel;
using System.Text.Json;

namespace MeowFilm.Capabilities;

public sealed record NovelSearchSourceState(NovelSource Source, string Status, int Page, bool Ended);
public sealed class NovelSearch(NovelSourceService sources, NovelRuleProgram rules)
{
    private readonly NovelWork work = new();
    private readonly SemaphoreSlim merging = new(1, 1);
    private CancellationTokenSource? request;
    private long generation;
    private readonly Dictionary<string, NovelBook> raw = [];
    public string Query { get; private set; } = "";
    public ObservableCollection<NovelBook> Books { get; } = [];
    public ObservableCollection<NovelSearchSourceState> States { get; } = [];
    public IReadOnlyList<JsonElement> Groups { get; private set; } = [];
    public bool Loading { get; private set; }
    public bool CanLoadMore => !Loading && States.Any(s => !s.Ended);
    public string Status => Loading ? "搜索中…" : States.Count == 0 ? "无可用书源" :
        Books.Count == 0 ? States.All(s => s.Status == "失败") ? "搜索失败" :
        States.Any(s => s.Status == "已取消") ? "搜索已取消" : "无搜索结果" : "";
    public event EventHandler? Changed;
    public Task CurrentTask { get; private set; } = Task.CompletedTask;
    public Task StartAsync(string query, CancellationToken token = default, bool restart = false)
    {
        query = query.Trim();
        if (!restart && Query == query && States.Count > 0)
            return Loading ? CurrentTask : States.Any(s => s.Status is "已取消" or "等待") ? LoadMoreAsync(token) : Task.CompletedTask;
        Reset(); Query = query;
        return LoadMoreAsync(true, token);
    }
    public void SourcesChanged()
    {
        if (States.Count == 0) return;
        var available = sources.Sources.Where(s => s.Enabled && s.Actions.Contains("search")).ToArray();
        if (available.Length == States.Count && available.All(s => States.Any(state =>
            state.Source.InstallationId == s.InstallationId && state.Source.Revision == s.Revision))) return;
        Reset(); Changed?.Invoke(this, EventArgs.Empty);
    }
    private void Reset() { Cancel(); CurrentTask = Task.CompletedTask; raw.Clear(); Books.Clear(); Groups = []; States.Clear(); }
    public Task LoadMoreAsync(CancellationToken token = default) => LoadMoreAsync(false, token);
    private Task LoadMoreAsync(bool initialize, CancellationToken token)
    {
        if (Loading) return CurrentTask;
        if (Query.Length == 0 || (!initialize && States.All(s => s.Ended))) return Task.CompletedTask;
        token.ThrowIfCancellationRequested();
        request?.Dispose(); request = CancellationTokenSource.CreateLinkedTokenSource(token);
        long id = ++generation; string query = Query;
        try
        {
            Loading = true; Changed?.Invoke(this, EventArgs.Empty);
            return CurrentTask = work.Run(async ct =>
            {
                try
                {
                    if (initialize)
                    {
                        await sources.EnsureExecutionReadyAsync(ct);
                        ct.ThrowIfCancellationRequested();
                        if (id != generation) return false;
                        foreach (var source in sources.Sources.Where(s => s.Enabled && s.Actions.Contains("search")))
                            States.Add(new(source, "等待", 0, false));
                    }
                    await Task.WhenAll(States.Where(s => !s.Ended).ToArray().Select(SearchOne));
                    return true;
                    async Task SearchOne(NovelSearchSourceState state)
                    {
                        int at = States.IndexOf(state);
                        if (at < 0 || id != generation) return;
                        States[at] = state with { Status = "搜索中" };
                        Changed?.Invoke(this, EventArgs.Empty);
                        try
                        {
                            var value = await sources.InvokeAsync(state.Source, "search", new { keyword = query, page = state.Page + 1 }, ct);
                            var books = value.GetProperty("books").EnumerateArray().Select(b => NovelBook.From(b, state.Source)).ToArray();
                            await merging.WaitAsync(ct);
                            try
                            {
                                ct.ThrowIfCancellationRequested(); if (id != generation) return;
                                bool added = books.Any(b => !raw.ContainsKey(b.SourceIdentity));
                                var next = new Dictionary<string, NovelBook>(raw);
                                foreach (var book in books) next[book.SourceIdentity] = book;
                                var merged = await rules.InvokeAsync(new { operation = "merge", books = next.Values.Select(b => b.Json()).ToArray() }, ct);
                                ct.ThrowIfCancellationRequested(); if (id != generation) return;
                                raw.Clear(); foreach (var item in next) raw.Add(item.Key, item.Value);
                                Groups = merged.GetProperty("books").EnumerateArray().Select(b => b.Clone()).ToArray();
                                Books.Clear();
                                foreach (var book in Groups)
                                {
                                    var source = sources.Sources.FirstOrDefault(s => s.InstallationId == NovelJson.Text(book, "installationId"));
                                    if (source is not null) Books.Add(NovelBook.From(book, source));
                                }
                                States[at] = state with { Status = books.Length == 0 ? "无结果" : "完成", Page = state.Page + 1, Ended = !added };
                            }
                            finally { merging.Release(); }
                        }
                        catch (OperationCanceledException) { if (id == generation) States[at] = state with { Status = "已取消" }; }
                        catch { if (id == generation) States[at] = state with { Status = "失败" }; }
                        finally { if (id == generation) Changed?.Invoke(this, EventArgs.Empty); }
                    }
                }
                finally { if (id == generation) { Loading = false; Changed?.Invoke(this, EventArgs.Empty); } }
            }, request.Token);
        }
        catch
        {
            if (id == generation) { Loading = false; Changed?.Invoke(this, EventArgs.Empty); }
            throw;
        }
    }
    public void Cancel()
    {
        ++generation; request?.Cancel(); Loading = false;
        for (int i = 0; i < States.Count; i++) if (States[i].Status == "搜索中") States[i] = States[i] with { Status = "已取消" };
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public async Task PrepareShutdownAsync() { Cancel(); await work.PauseAsync(); request?.Dispose(); request = null; }
}
