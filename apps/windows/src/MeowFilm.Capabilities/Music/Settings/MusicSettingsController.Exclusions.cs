using System.Collections.ObjectModel;
using System.ComponentModel;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities;

/// <summary>A user-confirmed wrong source of one song, until restored.</summary>
public sealed class MusicSourceExclusionRow : INotifyPropertyChanged
{
    private bool canRestore;

    public MusicSourceExclusionRow(string songId, string provider, string platform, string title, string artist,
        string providerName, bool canRestore = true)
    {
        SongId = songId; Provider = provider; Platform = platform;
        Title = title; Artist = artist; ProviderName = providerName;
        this.canRestore = canRestore;
    }

    public string SongId { get; }
    public string Provider { get; }
    public string Platform { get; }
    public string Title { get; }
    public string Artist { get; }
    public string ProviderName { get; }
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

    public Task RefreshSourceExclusionsAsync() => RunAsync(() => ReloadSourceExclusions(lifetime.Token));

    private async Task ReloadSourceExclusions(CancellationToken token)
    {
        var reply = await Call("music.sourceExclusions.list", cancellationToken: token);
        SourceExclusions.Clear();
        bool editable = CanEdit;
        foreach (var item in reply.GetProperty("items").EnumerateArray().Reverse())
            SourceExclusions.Add(new(MusicDto.Text(item, "songId"), MusicDto.Text(item, "provider"),
                MusicDto.Text(item, "platform"), MusicDto.Text(item, "title"), MusicDto.Text(item, "artist"),
                MusicDto.Text(item, "providerName"), editable));
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
                await CallSettledReceipt("music.sourceExclusions.remove", new { songId = row.SongId, provider = row.Provider,
                    platform = row.Platform }, lifetime.Token);
                await OnSourceExclusionRemovedAsync(row.SongId, lifetime.Token);
            }
            finally { await ReloadSourceExclusions(lifetime.Token); }
        });
    }

    /// <summary>Choices for a manual record: songs of the current queue and every known provider.</summary>
    public Task PrepareSourceExclusionFormAsync() => RunAsync(async () =>
    {
        exclusionSongs.Clear(); ExclusionSongs.Clear();
        var songs = new Dictionary<string, SongRow>(StringComparer.Ordinal);
        long? queueVersion = null;
        int offset = 0, total;
        do
        {
            var page = await Call("music.queue.get", new { offset, limit = 100 }, lifetime.Token);
            long version = MusicDto.Integer(page, "version");
            if (queueVersion is not null && queueVersion != version)
                throw new MusicOperationException("REVISION_CONFLICT");
            queueVersion = version;
            var entries = MusicDto.Page(page, "entries", offset);
            foreach (var entry in entries)
            {
                var song = SongRow.Read(entry.GetProperty("song"), MusicDto.Text(entry, "id"));
                if (song.IsOnline) songs.TryAdd(song.Id, song);
            }
            offset += entries.Length;
            total = MusicDto.Count(page, "total");
        } while (offset < total);
        foreach (var song in songs.Values)
        {
            exclusionSongs.Add(song.Id, song);
            ExclusionSongs.Add(new(song.Id, song.Title, song.Artist));
        }
        ExclusionProviders.Clear();
        ExclusionProviders.Add(new(MusicLibraryOwner.OfficialProvider, "官方音源"));
        try
        {
            var list = await Call("music.sources.list", cancellationToken: lifetime.Token);
            foreach (var item in list.GetProperty("items").EnumerateArray())
                ExclusionProviders.Add(new(MusicDto.Text(item, "installationId"), MusicDto.Text(item, "name")));
        }
        catch (MusicOperationException error) when (error.Code == "SCRIPT_RUNTIME_UNAVAILABLE") { }
    });

    /// <summary>The same exact rule as the player's "wrong source": one song × provider × platform.</summary>
    public Task AddSourceExclusionAsync(MusicExclusionOption? song, MusicExclusionOption? provider, MusicExclusionOption? platform)
    {
        if (song is null || provider is null || platform is null || stopping ||
            !exclusionSongs.TryGetValue(song.Key, out var row) || !ExclusionProviders.Contains(provider) ||
            !ExclusionPlatforms.Contains(platform)) return Task.CompletedTask;
        // Admit the exclusion intent immediately, before its durable write joins the
        // command queue: an in-flight replacement must not commit while this is waiting.
        OnAdmitSourceExclusion(row.Id, provider.Key, platform.Key);
        string name = provider.Key == MusicLibraryOwner.OfficialProvider ? MusicSourceRow.OfficialName(platform.Key) : provider.Name;
        return RunAsync(async () =>
        {
            try
            {
                await CallSettledReceipt("music.sourceExclusions.add", new { songId = row.Id, provider = provider.Key,
                    platform = platform.Key, providerName = name, title = row.Title, artist = row.Artist }, lifetime.Token);
                OnSourceExclusionAdded(row.Id, provider.Key, platform.Key);
            }
            finally { await ReloadSourceExclusions(lifetime.Token); }
        });
    }
}
