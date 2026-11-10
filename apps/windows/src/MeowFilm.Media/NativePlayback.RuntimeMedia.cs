using System.Diagnostics;
using System.Windows.Interop;
using LibVLCSharp.Shared;

namespace MeowFilm.Media;

/// <summary>Native publication controls. PreserveFrom identifies the exact same-episode donor, not the new attempt.</summary>
public sealed record RuntimeMediaPlaybackIntent(string OwnerId, string Identity, string Title,
    double Position, bool Paused, NativePlaybackScope? PreserveFrom = null)
{
    public bool PreserveCurrent => PreserveFrom is not null;
    // Exact-instance retirement receipt; callers retain the outcome without an
    // owner-wide history of every attempted input.
    internal Task? Retirement { get; set; }
}

public sealed partial class NativePlayback
{
    private sealed class RuntimeOutput(IAsyncDisposable output) : IAsyncDisposable
    {
        private readonly object gate = new();
        private Task? closing;
        internal bool ReleaseFailed { get { lock (gate) return closing?.IsFaulted == true; } }
        public ValueTask DisposeAsync()
        {
            lock (gate) return new(closing ??= CloseAsync());
        }
        private async Task CloseAsync()
        {
            // Publish the one receipt before invoking a child that can reenter disposal.
            await Task.Yield();
            try { await output.DisposeAsync().ConfigureAwait(false); }
            catch { throw Error(PlaybackError.ReleaseFailed); }
        }
    }

    private RuntimeMediaPlaybackIntent? _runtimeHandoffIntent;
    private HwndSource? _runtimeHandoffWindow;

    /// <summary>
    /// Prepares HTTP(S) input without origin or address admission.
    /// A real libVLC video-output counter, not Playing/Paused, gates publication.
    /// </summary>
    public Task PrepareRuntimeMediaAsync(RuntimeMediaPlaybackIntent intent, Uri address,
        CancellationToken token = default) =>
        DispatchAsync(() => PrepareRuntimeCoreAsync(intent, address, null, token));

    public Task PrepareRuntimeMediaAsync(RuntimeMediaPlaybackIntent intent,
        INativeMediaResourceLease lease, CancellationToken token = default) =>
        TrackOwnedMediaRequestAsync(() => PrepareRuntimeOwnedAsync(intent, lease, token));

    private async Task PrepareRuntimeOwnedAsync(RuntimeMediaPlaybackIntent intent,
        INativeMediaResourceLease lease, CancellationToken token)
    {
        if (lease is null) throw Error(PlaybackError.InvalidResourceLease);
        var resource = new MediaResourceSession(lease);
        try
        {
            await DispatchAsync(async () =>
            {
                resource.Prepare(token);
                await PrepareRuntimeCoreAsync(intent, resource.Address, resource, resource.Token);
            }).ConfigureAwait(false);
        }
        catch
        {
            await resource.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task PrepareRuntimeCoreAsync(RuntimeMediaPlaybackIntent intent, Uri address,
        MediaResourceSession? resource, CancellationToken token, RuntimeVideoMaterial? video = null)
    {
        if (intent is null || !double.IsFinite(intent.Position) || intent.Position < 0 ||
            intent.Position >= long.MaxValue / 1000d)
            throw Error(PlaybackError.InvalidRequest);
        if (intent.Retirement is not null) throw Error(PlaybackError.Superseded);
        if (intent.PreserveFrom is { } donor)
        {
            if (donor.OwnerId != intent.OwnerId) throw Error(PlaybackError.InvalidRequest);
            RequireScope(donor);
        }
        await PrepareHandoffCoreAsync(address, resource?.Headers ?? new Dictionary<string, string>(),
            null, token, resource, intent, video);
    }

    private async Task WaitForRuntimeHandoffOutputAsync(Session prepared, CancellationToken token)
    {
        var deadline = Stopwatch.StartNew();
        // The provisional player uses an invisible owned HWND. Never replace the healthy
        // donor surface or let libVLC create a second top-level window while checking it.
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_handoffSession, prepared) ||
                _handoffTarget is not { } target || !HandoffScopeCurrent(target))
                throw Error(PlaybackError.Superseded);
            CheckHandoffInput(prepared, _handoffFailure!);
            if (prepared.Media.Statistics is { DisplayedPictures: > 0 }) break;
            if (deadline.Elapsed >= TimeSpan.FromSeconds(12)) throw Error(PlaybackError.OpenFailed);
            if (prepared.Player.State == VLCState.Paused) prepared.Player.SetPause(false);
            await Task.Delay(30, token);
        }
        prepared.Player.SetPause(true);
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(_handoffSession, prepared))
            throw Error(PlaybackError.Superseded);
        CheckHandoffInput(prepared, _handoffFailure!);
        prepared.RuntimeOutputReady = true;
    }

    /// <summary>Only the precise preparation may publish; an older caller cannot adopt another owner's candidate.</summary>
    public Task CommitRuntimeMediaAsync(RuntimeMediaPlaybackIntent intent, Action committed, CancellationToken token = default) =>
        TryCommitRuntimeMediaAsync(intent, static () => true, committed, token);

    /// <summary>
    /// False defers publication without retiring the prepared input. The donor's
    /// attached child may already be drained; its native session remains current.
    /// The caller must release its own control lock before awaiting business admission.
    /// </summary>
    public async Task<bool> TryCommitRuntimeMediaAsync(RuntimeMediaPlaybackIntent intent,
        Func<bool> canCommit, Action committed, CancellationToken token = default)
    {
        bool swapped = false;
        await DispatchAsync(async () =>
        {
            if (intent is null || !ReferenceEquals(_runtimeHandoffIntent, intent))
                throw Error(PlaybackError.Superseded);
            swapped = await CommitHandoffCoreAsync(canCommit, committed, token);
        }).ConfigureAwait(false);
        return swapped;
    }

    public Task DiscardRuntimeMediaAsync(RuntimeMediaPlaybackIntent intent) => DispatchAsync(() =>
        intent?.Retirement ?? (intent is not null && ReferenceEquals(_runtimeHandoffIntent, intent)
            ? DiscardHandoffCoreAsync() : Task.CompletedTask));

    private void AttachRuntimeHandoffOutput(MediaPlayer player)
    {
        _runtimeHandoffWindow = new(new HwndSourceParameters("meowfilm-runtime-media-check")
        {
            Width = 1, Height = 1, WindowStyle = unchecked((int)0x80000000),
            ExtendedWindowStyle = 0x08000080
        });
        player.Hwnd = _runtimeHandoffWindow.Handle;
    }

    private void ReleaseRuntimeHandoffOutput()
    {
        _runtimeHandoffWindow?.Dispose();
        _runtimeHandoffWindow = null;
        _runtimeHandoffIntent = null;
    }

    /// <summary>Commands are fenced by the exact accepted native occurrence, never the currently selected UI page.</summary>
    public Task SetRuntimePausedAsync(NativePlaybackScope scope, bool paused, CancellationToken token = default) =>
        DispatchAsync(async () =>
        {
            token.ThrowIfCancellationRequested(); RequireScope(scope);
            if (paused) await PauseWithIntentAsync(); else await ResumeWithIntentAsync();
        });

    public Task SeekRuntimeAsync(NativePlaybackScope scope, double seconds, CancellationToken token = default) =>
        DispatchAsync(() =>
        {
            token.ThrowIfCancellationRequested(); RequireScope(scope);
            return CommandAsync(Command.Seek, seconds);
        });

    public Task SetRuntimeAspectAsync(NativePlaybackScope scope, string? ratio, CancellationToken token = default) =>
        DispatchAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            if (ratio is not (null or "16:9" or "4:3" or "1:1")) throw Error(PlaybackError.InvalidRequest);
            RequireScope(scope).Player.AspectRatio = ratio;
            return Task.CompletedTask;
        });

    public Task StopRuntimeAsync(NativePlaybackScope scope) => DispatchAsync(() =>
    {
        RequireScope(scope);
        return StopCoreAsync();
    });

    /// <summary>
    /// Owns one secondary output of this exact paused native occurrence (for example
    /// an external player). Every replacement/stop drains it before another owner
    /// may output. Ownership transfers on entry, including rejected attachment.
    /// </summary>
    public async Task<IAsyncDisposable> OwnRuntimeOutputAsync(NativePlaybackScope scope,
        IAsyncDisposable output, CancellationToken token = default)
    {
        IAsyncDisposable? owned = null;
        await TrackOwnedMediaRequestAsync(async () =>
        {
            owned = await OwnRuntimeOutputCoreAsync(scope, output, token);
        }).ConfigureAwait(false);
        return owned!;
    }

    private async Task<IAsyncDisposable> OwnRuntimeOutputCoreAsync(NativePlaybackScope scope,
        IAsyncDisposable output, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(output);
        var owned = new RuntimeOutput(output);
        try
        {
            await DispatchAsync(async () =>
            {
                await _commands.WaitAsync(token);
                try
                {
                    var session = RequireScope(scope);
                    if (!_pauseRequested) throw Error(PlaybackError.InvalidRequest);
                    if (session.RuntimeOutput is { } previous) await previous.DisposeAsync();
                    token.ThrowIfCancellationRequested();
                    if (!ReferenceEquals(RequireScope(scope), session)) throw Error(PlaybackError.Superseded);
                    session.RuntimeOutput = owned;
                }
                finally { _commands.Release(); }
            }).ConfigureAwait(false);
            return owned;
        }
        catch
        {
            await owned.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Prepares an owned runtime input using the existing muted/paused handoff.
    /// Owns the lease immediately, even on rejection. token is playback lifetime, not
    /// the smart-search/UI subscription; commit retains it with the resource capability.
    /// The accepted output remains intact until CommitHandoffAsync publishes this input.
    /// </summary>
    public Task PrepareResourceHandoffAsync(INativeMediaResourceLease lease, CancellationToken token = default) =>
        TrackOwnedMediaRequestAsync(() => PrepareResourceHandoffOwnedAsync(lease, token));

    private async Task PrepareResourceHandoffOwnedAsync(INativeMediaResourceLease lease, CancellationToken token)
    {
        if (lease is null) throw Error(PlaybackError.InvalidResourceLease);
        var resource = new MediaResourceSession(lease);
        try
        {
            await DispatchAsync(async () =>
            {
                resource.Prepare(token);
                await PrepareHandoffCoreAsync(resource.Address, resource.Headers, null, resource.Token, resource);
            }).ConfigureAwait(false);
        }
        catch
        {
            await resource.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
