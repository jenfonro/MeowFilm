namespace MeowFilm.Desktop;

public sealed partial class ShellWindow
{
    private readonly List<(IDesktopOwnedNoticeSource Source, EventHandler Handler, HashSet<object> Owners)> ownedNoticeSubscriptions = [];
    private void SubscribeOwnedNotices(IDesktopModule module)
    {
        if (module is not IDesktopOwnedNoticeSource source) return;
        var owners = new HashSet<object>(ReferenceEqualityComparer.Instance);
        void Publish()
        {
            var notices = source.OwnedNotices;
            var current = notices.Select(notice => notice.Owner).ToHashSet(ReferenceEqualityComparer.Instance);
            foreach (var owner in owners.Where(owner => !current.Contains(owner))) Notices.Show(owner, "");
            owners.Clear(); owners.UnionWith(current);
            foreach (var notice in notices) Notices.Show(notice.Owner, notice.Message, persistent: true);
        }
        EventHandler handler = (_, _) => QueueUi(Publish);
        source.OwnedNoticesChanged += handler;
        ownedNoticeSubscriptions.Add((source, handler, owners));
        QueueUi(Publish);
    }
    private void UnsubscribeOwnedNotices()
    {
        foreach (var (source, handler, owners) in ownedNoticeSubscriptions)
        {
            source.OwnedNoticesChanged -= handler;
            foreach (var owner in owners) Notices.Show(owner, "");
        }
        ownedNoticeSubscriptions.Clear();
    }
}
