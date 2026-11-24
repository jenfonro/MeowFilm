using System.Collections.ObjectModel;
using System.ComponentModel;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities;

public sealed class MusicQualityRow(string key, bool enabled) : ISettingsPriorityItem
{
    private bool isEnabled = enabled;
    public string Key { get; } = key;
    public bool Enabled => isEnabled;
    public string Label => MusicQualityLabels.Name(Key);
    internal void Apply(bool value)
    {
        if (isEnabled == value) return;
        isEnabled = value;
        PropertyChanged?.Invoke(this, new(nameof(Enabled)));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

[Flags]
internal enum QualitySettingsSurface
{
    None = 0,
    Settings = 1,
    Player = 2
}

/// <summary>A user-confirmed wrong source of one song, until restored.</summary>
public sealed class MusicSourceExclusionRow : INotifyPropertyChanged
{
    private bool canRestore;

    public MusicSourceExclusionRow(string songId, string provider, string platform, string title, string artist,
        string providerName, MusicSourceExclusionTier tier = MusicSourceExclusionTier.ProviderPlatform, bool canRestore = true)
    {
        SongId = songId; Provider = provider; Platform = platform;
        Title = title; Artist = artist; ProviderName = providerName;
        Tier = tier;
        this.canRestore = canRestore;
    }

    public string SongId { get; }
    public string Provider { get; }
    public string Platform { get; }
    public string Title { get; }
    public string Artist { get; }
    public string ProviderName { get; }
    public MusicSourceExclusionTier Tier { get; }
    public bool IsRecordingExclusion => Tier == MusicSourceExclusionTier.Recording;
    public string? RecordingSourceKey => MusicSourceExclusionsOwner.TryExtractRecordingSourceKey(Provider);
    public string DisplayScope => IsRecordingExclusion ? $"{ProviderName}（单录音）" : ProviderName;
    public string PlatformName => MusicSourceRow.PlatformName(Platform);
    public string SongText => Artist.Length == 0 ? Title : $"{Title} - {Artist}";
    public bool CanRestore
    {
        get => canRestore;
        internal set
        {
            if (canRestore == value) return;
            canRestore = value;
            PropertyChanged?.Invoke(this, new(nameof(CanRestore)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>A pickable value of the manual exclusion form.</summary>
public sealed record MusicExclusionOption(string Key, string Name, string Detail = "");

/// <summary>
/// Standalone domain handler for playback quality policy, spatial quality preferences,
/// and wrong-source exclusion management.
/// </summary>
internal sealed class MusicPlaybackQualitySettings(MusicRuntimeContext ctx, Action<string> notify)
{
    private MusicQualityPolicyRecord? savedQualityPolicy;
    private string savedQualitySignature = "", qualityStatus = "";
    private bool qualityLoaded, qualityBusy, qualitySaving;
    private long qualityGeneration;
    private QualitySettingsSurface qualitySurfaces;
    private CancellationTokenSource? qualityCancellation;
    private Task qualityWork = Task.CompletedTask;

    private readonly Dictionary<string, SongRow> exclusionSongs = new(StringComparer.Ordinal);

    internal event Action<string, bool>? QualityPolicyAccepted;
    internal event Action<bool>? OnlineLyricsEnabledChanged;
    internal event Action<string, string, string>? SourceExclusionAdmitted;
    internal event Action<string, string, string>? SourceExclusionAdded;
    internal event Func<string, CancellationToken, Task>? SourceExclusionRemoved;

    internal ObservableCollection<MusicQualityRow> QualityCategories { get; } = [];
    internal ObservableCollection<MusicQualityRow> SpatialQualities { get; } = [];
    internal string[] PlaybackQualityOptions { get; } = ["auto", "128k", "192k", "320k", "flac", "flac24bit", "master"];
    internal string PlaybackQualityRequest { get; private set; } = "flac";
    internal string QualityStatus => qualityStatus;
    internal bool CanEditQuality => ctx.CanEdit && qualityLoaded && !qualityBusy;
    internal MusicQualityPolicyRecord? SavedQualityPolicy => savedQualityPolicy;
    internal string SavedQualitySignature => savedQualitySignature;
    internal Task QualityWork => qualityWork;

    internal ObservableCollection<MusicSourceExclusionRow> SourceExclusions { get; } = [];
    internal ObservableCollection<MusicExclusionOption> ExclusionSongs { get; } = [];
    internal ObservableCollection<MusicExclusionOption> ExclusionProviders { get; } = [];
    internal IReadOnlyList<MusicExclusionOption> ExclusionPlatforms { get; } =
        MusicSourceRow.AllPlatforms.Select(platform => new MusicExclusionOption(platform, MusicSourceRow.PlatformName(platform))).ToArray();
    internal bool HasSourceExclusions => SourceExclusions.Count > 0;

    internal void NotifyQualityBindings()
    {
        notify(nameof(MusicSettingsController.CanEditQuality));
        notify(nameof(MusicSettingsController.QualityStatus));
        notify(nameof(MusicSettingsController.PlaybackQualityRequest));
    }

    internal void AcceptQuality(MusicQualitySettingsResult result)
    {
        var priorSignature = savedQualitySignature;
        savedQualityPolicy = result.Settings;
        savedQualitySignature = result.Signature;
        MusicDto.Require(IsQualityHash(savedQualitySignature));
        bool signatureChanged = priorSignature.Length > 0 && priorSignature != savedQualitySignature;
        QualityPolicyAccepted?.Invoke(savedQualitySignature, signatureChanged);
        PublishQualitySettings();
        qualityLoaded = true;
        NotifyQualityBindings();
    }

    private void PublishQualitySettings()
    {
        if (savedQualityPolicy is not { } policy) return;
        PlaybackQualityRequest = policy.Request;
        void Rows(ObservableCollection<MusicQualityRow> destination, string[] order, string[] enabled, string[] options)
        {
            var active = enabled.ToHashSet(StringComparer.Ordinal);
            var keys = order.Concat(options.Except(order)).ToArray();
            MusicSettingsController.ReconcilePriorityRows(destination, keys, row => row.Key,
                key => new(key, active.Contains(key)),
                (row, key) => row.Apply(active.Contains(key)));
        }
        Rows(QualityCategories, policy.QualityOrder, policy.QualityEnabled, MusicQualityPolicy.Categories);
        Rows(SpatialQualities, policy.SpatialOrder, policy.SpatialEnabled, MusicQualityPolicy.Spatial);
    }

    private MusicQualityPolicyRecord DesiredQuality(string? request = null)
    {
        MusicDto.Require(savedQualityPolicy is not null);
        return savedQualityPolicy! with { Request = request ?? PlaybackQualityRequest };
    }

    internal Task SetPlaybackQualityRequestAsync(string request)
    {
        if (!CanEditQuality || !PlaybackQualityOptions.Contains(request) ||
            request == PlaybackQualityRequest) return Task.CompletedTask;
        return SaveQualitySettings(DesiredQuality(request));
    }

    internal Task SetQualityEnabledAsync(MusicQualityRow row, bool enabled, bool spatial)
    {
        if (!CanEditQuality) return Task.CompletedTask;
        var rows = spatial ? SpatialQualities : QualityCategories;
        if (!rows.Contains(row) || row.Enabled == enabled) return Task.CompletedTask;
        var desired = DesiredQuality();
        var active = rows.Where(value => value.Enabled).Select(value => value.Key)
            .ToHashSet(StringComparer.Ordinal);
        if (enabled) active.Add(row.Key); else active.Remove(row.Key);
        var nextEnabled = rows.Where(value => active.Contains(value.Key)).Select(value => value.Key).ToArray();
        if (spatial)
        {
            var nextOrder = enabled && !desired.SpatialOrder.Contains(row.Key)
                ? desired.SpatialOrder.Append(row.Key).ToArray()
                : desired.SpatialOrder;
            desired = desired with { SpatialEnabled = nextEnabled, SpatialOrder = nextOrder };
        }
        else
        {
            var nextOrder = enabled && !desired.QualityOrder.Contains(row.Key)
                ? desired.QualityOrder.Append(row.Key).ToArray()
                : desired.QualityOrder;
            desired = desired with { QualityEnabled = nextEnabled, QualityOrder = nextOrder };
        }
        return SaveQualitySettings(desired);
    }

    internal Task MoveQualityAsync(MusicQualityRow source, MusicQualityRow target, bool spatial)
    {
        if (!CanEditQuality || ReferenceEquals(source, target)) return Task.CompletedTask;
        var rows = spatial ? SpatialQualities : QualityCategories;
        if (!rows.Contains(source) || !rows.Contains(target)) return Task.CompletedTask;
        var order = rows.Select(row => row.Key).ToList();
        int from = order.IndexOf(source.Key), to = order.IndexOf(target.Key);
        if (from < 0 || to < 0) return Task.CompletedTask;
        order.RemoveAt(from);
        order.Insert(to, source.Key);
        var desired = DesiredQuality();
        desired = spatial
            ? desired with { SpatialOrder = order.ToArray() }
            : desired with { QualityOrder = order.ToArray() };
        return SaveQualitySettings(desired);
    }

    internal Task DeactivateAsync(QualitySettingsSurface surface) =>
        ReleaseSurface(surface) ? qualityWork : Task.CompletedTask;

    internal bool ReleaseSurface(QualitySettingsSurface surface)
    {
        if ((qualitySurfaces & surface) == 0) return false;
        qualitySurfaces &= ~surface;
        if (qualitySurfaces != QualitySettingsSurface.None) return false;
        RetireQualitySettings();
        return true;
    }

    private void RetireQualitySettings()
    {
        qualityGeneration++;
        qualityLoaded = false;
        if (!qualitySaving) qualityCancellation?.Cancel();
        NotifyQualityBindings();
    }

    private bool QualityCurrent(long generation) => generation == qualityGeneration && !ctx.Stopping && !ctx.Disposed;

    internal async Task ActivateAsync(QualitySettingsSurface surface)
    {
        qualitySurfaces |= surface;
        if (qualityLoaded || ctx.Stopping || ctx.Disposed) return;
        long generation = qualityGeneration;
        if (qualityBusy) await qualityWork;
        if (!QualityCurrent(generation) || qualityLoaded || qualitySurfaces == QualitySettingsSurface.None) return;
        await EnsureQualitySettingsAsync();
    }

    private Task EnsureQualitySettingsAsync() => BeginQuality(async (token, generation) =>
    {
        var result = await ctx.RunSettingReceiptAsync(MusicSettingReceiptKeys.QualityPolicy,
            ct => ctx.Application.GetQualityPolicyAsync(ct), token);
        token.ThrowIfCancellationRequested();
        if (!QualityCurrent(generation)) return;
        AcceptQuality(result);
        qualityStatus = "";
    });

    private bool QualitySettingsMatch(MusicQualityPolicyRecord desired) =>
        savedQualityPolicy is { } current &&
        current.Request == desired.Request &&
        current.QualityOrder.SequenceEqual(desired.QualityOrder) &&
        current.QualityEnabled.SequenceEqual(desired.QualityEnabled) &&
        current.SpatialOrder.SequenceEqual(desired.SpatialOrder) &&
        current.SpatialEnabled.SequenceEqual(desired.SpatialEnabled);

    private Task SaveQualitySettings(MusicQualityPolicyRecord desired) => BeginQuality(async (token, generation) =>
    {
        try
        {
            token.ThrowIfCancellationRequested();
            // The accepted policy change retires future refresh authority in
            // AcceptQuality. The already opened media keeps its playback token.
            ctx.CancelOnlinePlayback();
            var saved = await ctx.RunSettingReceiptAsync(MusicSettingReceiptKeys.QualityPolicy,
                ct => ctx.Application.SaveQualityPolicyAsync(desired.Revision, desired.Request,
                    desired.QualityOrder, desired.QualityEnabled, desired.SpatialOrder, desired.SpatialEnabled, ct),
                token);
            if (!QualityCurrent(generation)) return;
            AcceptQuality(saved); qualityStatus = "";
            var online = await ctx.Application.GetOnlineSettingsAsync(token);
            if (!QualityCurrent(generation)) return;
            OnlineLyricsEnabledChanged?.Invoke(online.LyricsEnabled);
        }
        catch (MusicOperationException error) when (error.Code is
            "COMMIT_UNKNOWN" or "STORAGE_CONFLICT" or "PENDING_WRITE" or "REVISION_CONFLICT")
        {
            if (!QualityCurrent(generation)) return;
            bool matches;
            try
            {
                var current = await ctx.RunSettingReceiptAsync(MusicSettingReceiptKeys.QualityPolicy,
                    ct => ctx.Application.GetQualityPolicyAsync(ct), ctx.Lifetime.Token);
                if (!QualityCurrent(generation)) return;
                AcceptQuality(current);
                var online = await ctx.Application.GetOnlineSettingsAsync(ctx.Lifetime.Token);
                if (!QualityCurrent(generation)) return;
                OnlineLyricsEnabledChanged?.Invoke(online.LyricsEnabled);
                matches = QualitySettingsMatch(desired);
            }
            catch
            {
                qualityLoaded = false;
                throw;
            }
            if (matches)
                qualityStatus = "";
            else if (error.Code == "REVISION_CONFLICT")
                qualityStatus = "音质设置已更新";
            else
                throw;
        }
    }, saving: true);

    private Task BeginQuality(Func<CancellationToken, long, Task> action, bool saving = false)
    {
        if (saving && !ctx.CanEdit || qualityBusy || ctx.Stopping || ctx.Disposed) return Task.CompletedTask;
        var request = CancellationTokenSource.CreateLinkedTokenSource(ctx.Lifetime.Token);
        long generation = qualityGeneration;
        qualitySaving = saving;
        qualityCancellation = request; qualityBusy = true; qualityStatus = ""; NotifyQualityBindings();
        return qualityWork = ctx.RunAsync(async () =>
        {
            try { await action(request.Token, generation); }
            catch (OperationCanceledException) { if (QualityCurrent(generation)) qualityStatus = "已取消"; }
            catch (MusicOperationException error) when (error.Code == "CANCELLED")
            { if (QualityCurrent(generation)) qualityStatus = "已取消"; }
            catch (MusicOperationException error)
            {
                if (!QualityCurrent(generation)) return;
                qualityStatus = error.Code switch
                {
                    "INVALID_REQUEST" => "音质设置无效",
                    "PENDING_WRITE" or "COMMIT_UNKNOWN" or "STORAGE_CONFLICT" => saving ? "音质设置保存失败" : "音质设置加载失败",
                    _ => saving ? "音质设置操作失败" : "音质设置加载失败"
                };
            }
            catch when (!QualityCurrent(generation)) { }
            finally
            {
                if (ReferenceEquals(qualityCancellation, request)) qualityCancellation = null;
                request.Dispose(); qualityBusy = false; qualitySaving = false;
                if (QualityCurrent(generation)) NotifyQualityBindings();
            }
        });
    }

    internal static bool IsQualityHash(string value) => value.Length == 64 &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    // Source exclusions management
    internal void RefreshExclusionRestoreAvailability()
    {
        bool editable = ctx.CanEdit;
        foreach (var row in SourceExclusions) row.CanRestore = editable;
    }

    internal async Task ReloadExclusionsAsync(CancellationToken token)
    {
        var list = await ctx.Application.ListSourceExclusionsAsync(ct: token);
        bool editable = ctx.CanEdit;
        var incoming = list.Items.Reverse()
            .Select(item => new MusicSourceExclusionRow(
                item.SongId, item.Provider, item.Platform, item.Title, item.Artist, item.ProviderName,
                MusicSourceExclusionsOwner.TierOf(item), editable))
            .ToArray();
        var existing = SourceExclusions.ToDictionary(row => (row.SongId, row.Provider, row.Platform));
        var nextRows = new MusicSourceExclusionRow[incoming.Length];
        for (int index = 0; index < incoming.Length; index++)
        {
            var candidate = incoming[index];
            if (existing.TryGetValue((candidate.SongId, candidate.Provider, candidate.Platform), out var retained) &&
                retained.Title == candidate.Title && retained.Artist == candidate.Artist &&
                retained.ProviderName == candidate.ProviderName)
            {
                retained.CanRestore = editable;
                nextRows[index] = retained;
            }
            else
            {
                nextRows[index] = candidate;
            }
        }
        var keep = nextRows.ToHashSet(ReferenceEqualityComparer.Instance);
        for (int index = SourceExclusions.Count - 1; index >= 0; index--)
            if (!keep.Contains(SourceExclusions[index])) SourceExclusions.RemoveAt(index);
        for (int index = 0; index < nextRows.Length; index++)
        {
            var row = nextRows[index];
            if (index < SourceExclusions.Count && ReferenceEquals(SourceExclusions[index], row)) continue;
            int currentIndex = SourceExclusions.IndexOf(row);
            if (currentIndex >= 0) SourceExclusions.Move(currentIndex, index);
            else SourceExclusions.Insert(index, row);
        }
        notify(nameof(MusicSettingsController.HasSourceExclusions));
    }

    internal Task RestoreExclusionAsync(MusicSourceExclusionRow? row)
    {
        if (row is null || !SourceExclusions.Contains(row) || !ctx.CanEdit) return Task.CompletedTask;
        return ctx.RunAsync(async () =>
        {
            try
            {
                if (row.RecordingSourceKey is { } sourceKey)
                    await ctx.Application.RemoveRecordingExclusionAsync(row.SongId, sourceKey, row.Platform, ctx.Lifetime.Token);
                else
                    await ctx.Application.RemoveSourceExclusionAsync(row.SongId, row.Provider, row.Platform, ctx.Lifetime.Token);
                if (SourceExclusionRemoved is { } removed)
                    await removed(row.SongId, ctx.Lifetime.Token);
            }
            finally { await ReloadExclusionsAsync(ctx.Lifetime.Token); }
        });
    }

    internal async Task<bool> PrepareExclusionFormAsync()
    {
        bool prepared = false;
        await ctx.RunAsync(async () =>
        {
            var songs = new Dictionary<string, SongRow>(StringComparer.Ordinal);
            long? queueVersion = null;
            int offset = 0, total;
            do
            {
                var page = await ctx.Application.GetQueueAsync(offset, 100, ctx.Lifetime.Token);
                long version = page.Version;
                if (queueVersion is not null && queueVersion != version)
                    throw new MusicOperationException("REVISION_CONFLICT");
                queueVersion = version;
                foreach (var entry in page.Entries)
                {
                    var song = SongRow.FromRecord(entry.Song, entry.EntryId);
                    if (song.IsOnline) songs.TryAdd(song.Id, song);
                }
                offset += page.Entries.Length;
                total = page.Total;
            } while (offset < total);

            var providers = new List<MusicExclusionOption>
            {
                new(MusicLibraryOwner.OfficialProvider, "官方音源")
            };
            try
            {
                var list = await ctx.Application.ListSourcesAsync(ctx.Lifetime.Token);
                foreach (var item in list.Items)
                    providers.Add(new(item.InstallationId, item.Name));
            }
            catch (MusicOperationException error) when (error.Code == "SCRIPT_RUNTIME_UNAVAILABLE") { }

            exclusionSongs.Clear();
            ExclusionSongs.Clear();
            foreach (var song in songs.Values)
            {
                exclusionSongs.Add(song.Id, song);
                ExclusionSongs.Add(new(song.Id, song.Title, song.Artist));
            }
            ExclusionProviders.Clear();
            foreach (var provider in providers)
                ExclusionProviders.Add(provider);
            prepared = true;
        });
        return prepared;
    }

    internal Task AddExclusionAsync(MusicExclusionOption? song, MusicExclusionOption? provider, MusicExclusionOption? platform)
    {
        if (song is null || provider is null || platform is null || ctx.Stopping ||
            !exclusionSongs.TryGetValue(song.Key, out var row) || !ExclusionProviders.Contains(provider) ||
            !ExclusionPlatforms.Contains(platform)) return Task.CompletedTask;
        // Admit the exclusion intent immediately, before its durable write joins the
        // command queue: an in-flight replacement must not commit while this is waiting.
        SourceExclusionAdmitted?.Invoke(row.Id, provider.Key, platform.Key);
        string name = provider.Key == MusicLibraryOwner.OfficialProvider ? MusicSourceRow.OfficialName(platform.Key) : provider.Name;
        return ctx.RunAsync(async () =>
        {
            try
            {
                await ctx.Application.AddSourceExclusionAsync(row.Id, provider.Key, platform.Key, name, row.Title, row.Artist, ctx.Lifetime.Token);
                SourceExclusionAdded?.Invoke(row.Id, provider.Key, platform.Key);
            }
            finally { await ReloadExclusionsAsync(ctx.Lifetime.Token); }
        });
    }
}
