namespace MeowFilm.Desktop;

internal sealed class ShellNavigationHistory
{
    private const int HistoryLimit = 64;
    private readonly List<(ModuleSession Session, Destination Destination)> _history = [];
    private readonly List<(ModuleSession Session, Destination Destination)> _future = [];

    public bool CanGoBack => _history.Count > 1;
    public bool CanGoForward => _future.Count > 0;
    public string? LastPageId => _history.LastOrDefault().Destination?.PageId;
    public IReadOnlyList<(ModuleSession Session, Destination Destination)> Entries => _history;

    public (ModuleSession Session, Destination Destination) PeekBack() => _history[^2];
    public (ModuleSession Session, Destination Destination) PeekForward() => _future[^1];

    public void Commit(RequestPlan plan, Destination destination)
    {
        var target = (plan.Session, destination);
        if (plan.IsBack && _history.Count > 1 && _history[^2] == target)
        {
            _future.Add(_history[^1]);
            if (_future.Count > HistoryLimit) _future.RemoveAt(0);
            _history.RemoveAt(_history.Count - 1);
        }
        else
        {
            if (plan.IsForward && _future.Count > 0 && _future[^1] == target) _future.RemoveAt(_future.Count - 1);
            else if (!plan.IsBack) _future.Clear();
            if (_history.Count == 0 || _history[^1] != target)
            {
                _history.Add(target);
                if (_history.Count > HistoryLimit) _history.RemoveAt(0);
            }
        }
        if (destination.PageId is null) plan.Session.Current = destination;
    }

    public void CollectRetainedLocations(ModuleSession session, HashSet<string> locations)
    {
        foreach (var entry in _history)
            if (ReferenceEquals(entry.Session, session) && entry.Destination.Location is { } location)
                locations.Add(location);
        foreach (var entry in _future)
            if (ReferenceEquals(entry.Session, session) && entry.Destination.Location is { } location)
                locations.Add(location);
    }
}
