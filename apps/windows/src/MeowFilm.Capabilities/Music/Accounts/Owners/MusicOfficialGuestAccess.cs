using System.Runtime.CompilerServices;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Accounts;

public sealed partial class MusicAccountOwner
{
    // An anonymous playback lease is NOT an authenticated account session. It is
    // deliberately rejected by the public IsCurrent/AcquireAsync account ports.
    private readonly ConditionalWeakTable<MusicPlatformSession, Lease> officialGuests = new();

    internal async ValueTask<MusicPlatformSession?> AcquireOfficialAsync(string platform, bool allowStandardGuest,
        CancellationToken token)
    {
        var session = await AcquireSettledAsync(platform, token).ConfigureAwait(false);
        if (session is not null || platform != "wy" || !allowStandardGuest) return session;
        lock (gate)
        {
            var state = states[platform];
            if (closed || state.Revoked || state.Uncertain || state.Active is not null ||
                state.Current is not { Cookie: null }) return null;
            AccountRules.Require(state.Epoch < AccountRules.MaxInteger, "LIMIT");
            session = new("wy", "public:wy", state.Epoch + 1, "0", "__remember_me=true");
            officialGuests.Add(session, new(state.Epoch));
            return session;
        }
    }

    internal bool IsOfficialGuest(MusicPlatformSession session) => officialGuests.TryGetValue(session, out _);

    internal bool IsOfficialCurrent(MusicPlatformSession session)
    {
        if (!officialGuests.TryGetValue(session, out var lease)) return IsCurrent(session);
        lock (gate)
            return !closed && states["wy"] is { Revoked: false, Uncertain: false, Active: null } state &&
                state.Epoch == lease.Epoch && state.Current is { Cookie: null } &&
                session.Generation == lease.Epoch + 1;
    }
}
