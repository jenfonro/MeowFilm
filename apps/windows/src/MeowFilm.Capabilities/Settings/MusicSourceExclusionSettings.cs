using System.Collections.ObjectModel;
using System.Text.Json;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities;

/// <summary>A user-confirmed wrong source of one song, until restored.</summary>
public sealed record MusicSourceExclusionRow(string SongId, string Provider, string Platform, string Title, string Artist,
    string ProviderName)
{
    public string PlatformName => MusicSourceRow.PlatformName(Platform);
    public string SongText => Artist.Length == 0 ? Title : $"{Title} - {Artist}";
}
/// <summary>A pickable value of the manual exclusion form.</summary>
public sealed record MusicExclusionOption(string Key, string Name, string Detail = "");

public sealed partial class MusicCapability
{
    public ObservableCollection<MusicSourceExclusionRow> SourceExclusions { get; } = [];
    public ObservableCollection<MusicExclusionOption> ExclusionSongs { get; } = [];
    public ObservableCollection<MusicExclusionOption> ExclusionProviders { get; } = [];
    public IReadOnlyList<MusicExclusionOption> ExclusionPlatforms { get; } =
        MusicSourceRow.AllPlatforms.Select(platform => new MusicExclusionOption(platform, MusicSourceRow.PlatformName(platform))).ToArray();
    private readonly Dictionary<string, SongRow> exclusionSongs = new(StringComparer.Ordinal);
    public bool HasSourceExclusions => SourceExclusions.Count > 0;

    public Task RefreshSourceExclusionsAsync() => RunAsync(() => ReloadSourceExclusions(lifetime.Token));
    private async Task ReloadSourceExclusions(CancellationToken token)
    {
        var reply = await Call("music.sourceExclusions.list", cancellationToken: token);
        SourceExclusions.Clear();
        foreach (var item in reply.GetProperty("items").EnumerateArray().Reverse())
            SourceExclusions.Add(new(MusicDto.Text(item, "songId"), MusicDto.Text(item, "provider"),
                MusicDto.Text(item, "platform"), MusicDto.Text(item, "title"), MusicDto.Text(item, "artist"),
                MusicDto.Text(item, "providerName")));
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
                exclusionsRevision++;
                if (musicRecovery is { Session: { } session } state && state.Song.Id == row.SongId)
                {
                    await session.LoadExclusionsAsync(lifetime.Token);
                    UpdateSourceChoices();
                }
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
        if (musicRecovery is { Switching: { } switching } active && active.Song.Id == row.Id)
            switching.Cancel();
        string name = provider.Key == MusicLibraryOwner.OfficialProvider ? MusicSourceRow.OfficialName(platform.Key) : provider.Name;
        return RunAsync(async () =>
        {
            try
            {
                await CallSettledReceipt("music.sourceExclusions.add", new { songId = row.Id, provider = provider.Key,
                    platform = platform.Key, providerName = name, title = row.Title, artist = row.Artist }, lifetime.Token);
                exclusionsRevision++;
                if (musicRecovery is { Session: { } session } state && state.Song.Id == row.Id)
                {
                    session.Exclude(provider.Key, platform.Key);
                    UpdateSourceChoices();
                }
            }
            finally { await ReloadSourceExclusions(lifetime.Token); }
        });
    }
}
