using System.Collections.ObjectModel;
using System.Text.Json;

namespace MeowFilm.Capabilities;

public sealed partial class MusicSettingsController
{
    private long metadataRevision, metadataGeneration;
    private string[] metadataOrder = [], metadataEnabled = [];
    private bool metadataLoaded, metadataBusy;
    private Task metadataWork = Task.CompletedTask;

    public ObservableCollection<MusicPlatformPriorityRow> MetadataPlatforms { get; } = [];
    public bool CanEditMetadata => CanEdit && metadataLoaded && !metadataBusy;

    internal async Task ReadMetadataSettings(CancellationToken token)
    {
        long generation = metadataGeneration;
        var settings = (await CallSettled("music.metadata.settings.get", cancellationToken: token))
            .GetProperty("settings");
        if (generation != metadataGeneration || stopping || disposed) return;
        PublishMetadataSettings(settings);
        await RefreshMetadataArtworkAsync(generation, token);
    }

    private async Task RefreshMetadataArtworkAsync(long generation, CancellationToken token)
    {
        if (generation != metadataGeneration || stopping || disposed || GetCurrentSong() is not { } song) return;
        var snapshot = await application.SongSnapshotsAsync([song.Id], token);
        token.ThrowIfCancellationRequested();
        if (generation != metadataGeneration || stopping || disposed || GetCurrentSong() is not { } currentSong || currentSong.Id != song.Id) return;
        currentSong.ApplyRepository(snapshot.Songs.GetValueOrDefault(song.Id), snapshot.Publication);
    }

    private void RetireMetadataSettings()
    {
        metadataGeneration++;
        metadataLoaded = false;
        Changed(nameof(CanEditMetadata));
    }

    private void PublishMetadataSettings(JsonElement settings)
    {
        metadataRevision = MusicDto.Integer(settings, "revision");
        var enabled = settings.GetProperty("enabled").EnumerateArray()
            .Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal);
        var platforms = settings.GetProperty("platforms").EnumerateArray()
            .Select(value => value.GetString()!).ToArray();
        metadataOrder = platforms;
        metadataEnabled = settings.GetProperty("enabled").EnumerateArray().Select(value => value.GetString()!).ToArray();
        platforms = platforms.Concat(Library.MusicLibraryOwner.MetadataPlatforms.Except(platforms)).ToArray();
        ReconcilePriorityRows(MetadataPlatforms, platforms, row => row.Platform,
            platform => new(platform, enabled.Contains(platform)),
            (row, platform) => row.Apply(enabled.Contains(platform)));
        metadataLoaded = true;
        Changed(nameof(CanEditMetadata));
    }

    private bool MetadataSettingsMatch(string[] platforms, string[] enabled) =>
        metadataOrder.SequenceEqual(platforms) && metadataEnabled.SequenceEqual(enabled);

    public Task SetMetadataEnabledAsync(MusicPlatformPriorityRow row, bool enabled)
    {
        if (!CanEditMetadata || !MetadataPlatforms.Contains(row) || row.Enabled == enabled)
            return Task.CompletedTask;
        var order = enabled && !metadataOrder.Contains(row.Platform) ? metadataOrder.Append(row.Platform).ToArray() : metadataOrder;
        var active = MetadataPlatforms.Where(value => value.Enabled)
            .Select(value => value.Platform).ToHashSet(StringComparer.Ordinal);
        if (enabled) active.Add(row.Platform); else active.Remove(row.Platform);
        var selected = metadataEnabled.Where(active.Contains).ToList();
        if (enabled && !selected.Contains(row.Platform)) selected.Add(row.Platform);
        return SaveMetadataSettingsAsync(order, selected.ToArray());
    }

    public Task MoveMetadataPlatformAsync(MusicPlatformPriorityRow source, MusicPlatformPriorityRow target)
    {
        if (!CanEditMetadata || ReferenceEquals(source, target) ||
            !MetadataPlatforms.Contains(source) || !MetadataPlatforms.Contains(target))
            return Task.CompletedTask;
        var order = MetadataPlatforms.Select(row => row.Platform).ToList();
        int from = order.IndexOf(source.Platform), to = order.IndexOf(target.Platform);
        if (from < 0 || to < 0) return Task.CompletedTask;
        order.RemoveAt(from);
        order.Insert(to, source.Platform);
        var active = MetadataPlatforms.Where(row => row.Enabled)
            .Select(row => row.Platform).ToHashSet(StringComparer.Ordinal);
        return SaveMetadataSettingsAsync(order.ToArray(), order.Where(active.Contains).ToArray());
    }

    private Task SaveMetadataSettingsAsync(string[] platforms, string[] enabled)
    {
        if (!CanEditMetadata) return Task.CompletedTask;
        long revision = metadataRevision;
        long generation = metadataGeneration;
        metadataBusy = true;
        Changed(nameof(CanEditMetadata));
        OnRetireLyrics();
        return metadataWork = RunAsync(async () =>
        {
            try
            {
                var result = await Call("music.metadata.settings.save",
                    new { ifRevision = revision, platforms, enabled }, lifetime.Token);
                if (generation != metadataGeneration || stopping || disposed) return;
                PublishMetadataSettings(result.GetProperty("settings"));
                await RefreshMetadataArtworkAsync(generation, lifetime.Token);
            }
            catch
            {
                if (generation != metadataGeneration || stopping || disposed) return;
                try
                {
                    await ReadMetadataSettings(lifetime.Token);
                    if (generation != metadataGeneration || stopping || disposed) return;
                    if (MetadataSettingsMatch(platforms, enabled)) return;
                }
                catch
                {
                    if (generation != metadataGeneration || stopping || disposed) return;
                    metadataLoaded = false;
                    throw;
                }
                throw;
            }
            finally
            {
                metadataBusy = false;
                if (generation == metadataGeneration && !stopping && !disposed)
                    Changed(nameof(CanEditMetadata));
                if (!stopping && !disposed && GetNeedsLyrics() && GetCurrentSong() is not null)
                    await OnRestoreLyricsAsync();
            }
        });
    }
}
