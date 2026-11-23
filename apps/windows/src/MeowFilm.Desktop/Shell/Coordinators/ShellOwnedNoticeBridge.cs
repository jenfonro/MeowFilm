namespace MeowFilm.Desktop;

internal sealed class ShellOwnedNoticeBridge(NoticeStack notices, Action<Action> queueUi) : IDisposable
{
    private readonly List<(IDesktopOwnedNoticeSource Source, EventHandler Handler, HashSet<object> Owners)> _subscriptions = [];

    public void Subscribe(IDesktopModule module)
    {
        if (module is not IDesktopOwnedNoticeSource source) return;
        var owners = new HashSet<object>(ReferenceEqualityComparer.Instance);
        void Publish()
        {
            var currentNotices = source.OwnedNotices;
            var current = currentNotices.Select(notice => notice.Owner).ToHashSet(ReferenceEqualityComparer.Instance);
            foreach (var owner in owners.Where(owner => !current.Contains(owner))) notices.Show(owner, "");
            owners.Clear();
            owners.UnionWith(current);
            foreach (var notice in currentNotices) notices.Show(notice.Owner, notice.Message, persistent: true);
        }
        EventHandler handler = (_, _) => queueUi(Publish);
        source.OwnedNoticesChanged += handler;
        _subscriptions.Add((source, handler, owners));
        queueUi(Publish);
    }

    public List<object> Snapshot()
    {
        var list = new List<object>();
        foreach (var (source, _, _) in _subscriptions)
        {
            foreach (var notice in source.OwnedNotices)
                list.Add(new { id = notice.Owner.GetHashCode().ToString(), level = "info", message = notice.Message });
        }
        return list;
    }

    public void Dispose()
    {
        foreach (var (source, handler, owners) in _subscriptions)
        {
            source.OwnedNoticesChanged -= handler;
            foreach (var owner in owners) notices.Show(owner, "");
        }
        _subscriptions.Clear();
    }
}
