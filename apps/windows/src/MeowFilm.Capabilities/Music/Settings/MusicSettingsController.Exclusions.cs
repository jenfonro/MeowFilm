using System.Collections.ObjectModel;
using System.ComponentModel;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities;

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

public sealed partial class MusicSettingsController
{
    public ObservableCollection<MusicSourceExclusionRow> SourceExclusions { get; } = [];
    public ObservableCollection<MusicExclusionOption> ExclusionSongs { get; } = [];
    public ObservableCollection<MusicExclusionOption> ExclusionProviders { get; } = [];
    public IReadOnlyList<MusicExclusionOption> ExclusionPlatforms { get; } =
        MusicSourceRow.AllPlatforms.Select(platform => new MusicExclusionOption(platform, MusicSourceRow.PlatformName(platform))).ToArray();
    private readonly Dictionary<string, SongRow> exclusionSongs = new(StringComparer.Ordinal);
    public bool HasSourceExclusions => SourceExclusions.Count > 0;

    private void RefreshExclusionRestoreAvailability()
    {
        bool editable = CanEdit;
        foreach (var row in SourceExclusions) row.CanRestore = editable;
    }

    private async Task ReloadSourceExclusions(CancellationToken token)
    {
        var list = await application.ListSourceExclusionsAsync(ct: token);
        bool editable = CanEdit;
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
        Changed(nameof(HasSourceExclusions));
    }

    /// <summary>Removes one record; nothing starts playing and no provider is enabled by it.</summary>
    public Task RestoreSourceExclusionAsync(MusicSourceExclusionRow? row)
    {
        if (row is null || !SourceExclusions.Contains(row) || !CanEdit) return Task.CompletedTask;
        return RunAsync(async () =>
        {
            try
            {
                if (row.RecordingSourceKey is { } sourceKey)
                    await application.RemoveRecordingExclusionAsync(row.SongId, sourceKey, row.Platform, lifetime.Token);
                else
                    await application.RemoveSourceExclusionAsync(row.SongId, row.Provider, row.Platform, lifetime.Token);
                await playbackPort.OnSourceExclusionRemovedAsync(row.SongId, lifetime.Token);
            }
            finally { await ReloadSourceExclusions(lifetime.Token); }
        });
    }

    /// <summary>Choices for a manual record: songs of the current queue and every known provider.</summary>
    public async Task<bool> PrepareSourceExclusionFormAsync()
    {
        bool prepared = false;
        await RunAsync(async () =>
        {
            var songs = new Dictionary<string, SongRow>(StringComparer.Ordinal);
            long? queueVersion = null;
            int offset = 0, total;
            do
            {
                var page = await application.GetQueueAsync(offset, 100, lifetime.Token);
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
                var list = await application.ListSourcesAsync(lifetime.Token);
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

    /// <summary>The same exact rule as the player's "wrong source": one song × provider × platform.</summary>
    public Task AddSourceExclusionAsync(MusicExclusionOption? song, MusicExclusionOption? provider, MusicExclusionOption? platform)
    {
        if (song is null || provider is null || platform is null || stopping ||
            !exclusionSongs.TryGetValue(song.Key, out var row) || !ExclusionProviders.Contains(provider) ||
            !ExclusionPlatforms.Contains(platform)) return Task.CompletedTask;
        // Admit the exclusion intent immediately, before its durable write joins the
        // command queue: an in-flight replacement must not commit while this is waiting.
        playbackPort.AdmitSourceExclusion(row.Id, provider.Key, platform.Key);
        string name = provider.Key == MusicLibraryOwner.OfficialProvider ? MusicSourceRow.OfficialName(platform.Key) : provider.Name;
        return RunAsync(async () =>
        {
            try
            {
                await application.AddSourceExclusionAsync(row.Id, provider.Key, platform.Key, name, row.Title, row.Artist, lifetime.Token);
                playbackPort.OnSourceExclusionAdded(row.Id, provider.Key, platform.Key);
            }
            finally { await ReloadSourceExclusions(lifetime.Token); }
        });
    }
}
