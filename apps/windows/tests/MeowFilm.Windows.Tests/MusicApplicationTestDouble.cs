using System.Collections.ObjectModel;
using System.Text.Json;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Capabilities.Feedback;
using MeowFilm.Capabilities.Recommendations;
using MeowFilm.Capabilities.Search;

namespace MeowFilm.Windows.Tests;

/// <summary>
/// Explicit current application contract for focused UI tests. Unused recommendation
/// activity is inert test behavior, not a production capability fallback.
/// </summary>
internal abstract class MusicApplicationTestDouble : IMusicApplication
{
    public virtual Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public abstract Task<JsonElement> ExecuteAsync(string method, object? parameters = null,
        CancellationToken cancellationToken = default);
    public virtual Task PrepareShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public virtual Task CancelShutdownPreparationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public virtual Task AbortAsync(CancellationToken cancellationToken = default) => DisposeAsync().AsTask();
    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public virtual event Action? DiscoveryInvalidated { add { } remove { } }
    public virtual Task<MusicSearchDiscoverySnapshot> ReadDiscoveryAsync(bool refresh,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(EmptyDiscovery);
    }
    public virtual Task<MusicSearchDiscoverySnapshot> RotateDiscoveryAsync(long generation,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(EmptyDiscovery);
    }
    public virtual bool IsDiscoveryCurrent(long generation) => generation == EmptyDiscovery.Generation;

    public virtual event Action<string>? AccountDetailsInvalidated { add { } remove { } }
    public virtual Task<MusicAccountDetails> ReadDetailsAsync(string platform, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(MissingDetails(platform));
    }
    public virtual Task<MusicAccountDetails> RefreshDetailsAsync(string platform, long expectedGeneration,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(MissingDetails(platform));
    }
    public virtual event Action? VipPageInvalidated { add { } remove { } }
    public virtual Task<MusicVipPageSnapshot> ReadVipPageAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(new MusicVipPageSnapshot(0, null, null, null));
    }
    public virtual Task<MusicVipSignReceipt> SignVipPageAsync(long expectedGeneration,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(new MusicVipSignReceipt(expectedGeneration, null));
    }

    public virtual IMusicPlaybackFeedback Feedback => InertFeedback.Instance;
    public virtual event Action? LibrarySyncChanged { add { } remove { } }
    public virtual event Action<JsonElement>? SearchChanged { add { } remove { } }
    public virtual event Action? ChorusInvalidated { add { } remove { } }
    public virtual bool IsOfficialPlaybackCurrent(string platform, string scope, long generation) => false;

    public virtual event Action<JsonElement>? Changed { add { } remove { } }
    public virtual IDisposable BindSelection(
        Func<MusicRecommendationSelection, CancellationToken, Task<bool>> selectAsync)
    {
        ArgumentNullException.ThrowIfNull(selectAsync);
        return EmptyBinding.Instance;
    }
    public virtual Task OnPlaybackAsync(MusicRecommendationPlaybackState state,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
    public virtual Task<bool> TryAdvanceAsync(string entryId, bool naturalEnd, bool play,
        CancellationToken cancellationToken = default) => Task.FromResult(false);
    public virtual MusicRecommendationFeedback? GetFeedback(string entryId) => null;
    public virtual Task PrepareRecommendationsAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    private static readonly MusicSearchDiscoverySnapshot EmptyDiscovery =
        new(1, [], [], [], [], [], 0);

    private static MusicAccountDetails MissingDetails(string platform) =>
        new(platform, 0, "none", null, 0, 0, new(null, "unknown", null), 0, 0, []);

    private sealed class InertFeedback : IMusicPlaybackFeedback
    {
        internal static readonly InertFeedback Instance = new();
        public string? LastError => null;
        public Task<MusicFeedbackSettings> ReadSettingsAsync(bool refresh = false,
            CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new MusicFeedbackSettings(0, Array.Empty<string>()));
        }
        public Task<MusicFeedbackSettings> SaveSettingsAsync(long revision, IReadOnlyList<string> enabled,
            CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new MusicFeedbackSettings(revision + 1, enabled.ToArray()));
        }
        public void Select(MusicFeedbackSelection value) { }
        public void UpdateSelection(MusicFeedbackSelection value) { }
        public void Started(string entryId, double position, double duration, double rate = 1, int mode = 1) { }
        public void Observe(string entryId, double position, double duration, bool isPlaying,
            double rate = 1, int mode = 1) { }
        public void Finish(string reason = "ui") { }
    }

    private sealed class EmptyBinding : IDisposable
    {
        internal static readonly EmptyBinding Instance = new();
        public void Dispose() { }
    }
}

internal abstract class MusicApplicationForwarder(IMusicApplication inner) : MusicApplicationTestDouble
{
    protected IMusicApplication Inner { get; } = inner ?? throw new ArgumentNullException(nameof(inner));
    public override Task InitializeAsync(CancellationToken cancellationToken = default) =>
        Inner.InitializeAsync(cancellationToken);
    public override Task<JsonElement> ExecuteAsync(string method, object? parameters = null,
        CancellationToken cancellationToken = default) =>
        Inner.ExecuteAsync(method, parameters, cancellationToken);
    public override Task PrepareShutdownAsync(CancellationToken cancellationToken = default) =>
        Inner.PrepareShutdownAsync(cancellationToken);
    public override Task CancelShutdownPreparationAsync(CancellationToken cancellationToken = default) =>
        Inner.CancelShutdownPreparationAsync(cancellationToken);
    public override Task AbortAsync(CancellationToken cancellationToken = default) =>
        Inner.AbortAsync(cancellationToken);
    public override ValueTask DisposeAsync() => Inner.DisposeAsync();

    public override event Action? DiscoveryInvalidated
    {
        add => Inner.DiscoveryInvalidated += value;
        remove => Inner.DiscoveryInvalidated -= value;
    }
    public override Task<MusicSearchDiscoverySnapshot> ReadDiscoveryAsync(bool refresh,
        CancellationToken token = default) => Inner.ReadDiscoveryAsync(refresh, token);
    public override Task<MusicSearchDiscoverySnapshot> RotateDiscoveryAsync(long generation,
        CancellationToken token = default) => Inner.RotateDiscoveryAsync(generation, token);
    public override bool IsDiscoveryCurrent(long generation) => Inner.IsDiscoveryCurrent(generation);

    public override event Action<string>? AccountDetailsInvalidated
    {
        add => Inner.AccountDetailsInvalidated += value;
        remove => Inner.AccountDetailsInvalidated -= value;
    }
    public override Task<MusicAccountDetails> ReadDetailsAsync(string platform, CancellationToken token = default) =>
        Inner.ReadDetailsAsync(platform, token);
    public override Task<MusicAccountDetails> RefreshDetailsAsync(string platform, long expectedGeneration,
        CancellationToken token = default) => Inner.RefreshDetailsAsync(platform, expectedGeneration, token);
    public override event Action? VipPageInvalidated
    {
        add => Inner.VipPageInvalidated += value;
        remove => Inner.VipPageInvalidated -= value;
    }
    public override Task<MusicVipPageSnapshot> ReadVipPageAsync(CancellationToken token = default) =>
        Inner.ReadVipPageAsync(token);
    public override Task<MusicVipSignReceipt> SignVipPageAsync(long expectedGeneration,
        CancellationToken token = default) =>
        Inner.SignVipPageAsync(expectedGeneration, token);

    public override IMusicPlaybackFeedback Feedback => Inner.Feedback;
    public override event Action? LibrarySyncChanged
    {
        add => Inner.LibrarySyncChanged += value;
        remove => Inner.LibrarySyncChanged -= value;
    }
    public override event Action<JsonElement>? SearchChanged
    {
        add => Inner.SearchChanged += value;
        remove => Inner.SearchChanged -= value;
    }
    public override event Action? ChorusInvalidated
    {
        add => Inner.ChorusInvalidated += value;
        remove => Inner.ChorusInvalidated -= value;
    }
    public override bool IsOfficialPlaybackCurrent(string platform, string scope, long generation) =>
        Inner.IsOfficialPlaybackCurrent(platform, scope, generation);

    public override event Action<JsonElement>? Changed
    {
        add => Inner.Changed += value;
        remove => Inner.Changed -= value;
    }
    public override IDisposable BindSelection(
        Func<MusicRecommendationSelection, CancellationToken, Task<bool>> selectAsync) =>
        Inner.BindSelection(selectAsync);
    public override Task OnPlaybackAsync(MusicRecommendationPlaybackState state,
        CancellationToken cancellationToken = default) =>
        Inner.OnPlaybackAsync(state, cancellationToken);
    public override Task<bool> TryAdvanceAsync(string entryId, bool naturalEnd, bool play,
        CancellationToken cancellationToken = default) =>
        Inner.TryAdvanceAsync(entryId, naturalEnd, play, cancellationToken);
    public override MusicRecommendationFeedback? GetFeedback(string entryId) => Inner.GetFeedback(entryId);
    public override Task PrepareRecommendationsAsync(CancellationToken cancellationToken = default) =>
        Inner.PrepareRecommendationsAsync(cancellationToken);
}
