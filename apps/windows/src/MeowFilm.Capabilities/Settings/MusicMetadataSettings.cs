using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;

namespace MeowFilm.Capabilities;

public sealed class MusicMetadataPlatformRow(string platform, bool enabled) : INotifyPropertyChanged
{
    private bool isEnabled = enabled;
    public string Platform { get; } = platform;
    public string Label => Platform switch
    {
        "wy" => "网易云音乐",
        "tx" => "QQ 音乐",
        "kg" => "酷狗音乐",
        "kw" => "酷我音乐",
        "mg" => "咪咕音乐",
        _ => Platform
    };
    public bool Enabled => isEnabled;
    internal void Apply(bool value)
    {
        if (isEnabled == value) return;
        isEnabled = value;
        PropertyChanged?.Invoke(this, new(nameof(Enabled)));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed partial class MusicCapability
{
    private long metadataRevision;
    private bool metadataLoaded, metadataBusy;
    public ObservableCollection<MusicMetadataPlatformRow> MetadataPlatforms { get; } = [];
    public bool CanEditMetadata => CanEdit && metadataLoaded && !metadataBusy;

    private async Task ReadMetadataSettings(CancellationToken token)
    {
        var settings = (await Call("music.metadata.settings.get", cancellationToken: token))
            .GetProperty("settings");
        PublishMetadataSettings(settings);
    }

    private void PublishMetadataSettings(JsonElement settings)
    {
        metadataRevision = MusicDto.Integer(settings, "revision");
        var enabled = settings.GetProperty("enabled").EnumerateArray()
            .Select(item => item.GetString()).ToHashSet(StringComparer.Ordinal);
        var platforms = settings.GetProperty("platforms").EnumerateArray()
            .Select(value => value.GetString()!).ToArray();
        var existing = MetadataPlatforms.ToDictionary(row => row.Platform, StringComparer.Ordinal);
        for (var index = 0; index < platforms.Length; index++)
        {
            var platform = platforms[index];
            if (!existing.TryGetValue(platform, out var row))
                row = new(platform, enabled.Contains(platform));
            else
                row.Apply(enabled.Contains(platform));
            var currentIndex = MetadataPlatforms.IndexOf(row);
            if (currentIndex < 0) MetadataPlatforms.Insert(index, row);
            else if (currentIndex != index) MetadataPlatforms.Move(currentIndex, index);
        }
        while (MetadataPlatforms.Count > platforms.Length)
            MetadataPlatforms.RemoveAt(MetadataPlatforms.Count - 1);
        savedArtworkPlatforms = MetadataPlatforms.Select(row => row.Platform).ToArray();
        savedArtworkEnabled = MetadataPlatforms.Where(row => row.Enabled)
            .Select(row => row.Platform).ToHashSet(StringComparer.Ordinal);
        artworkPolicyKnown = true;
        metadataLoaded = true;
        Changed(nameof(CurrentArtwork));
        Changed(nameof(CanEditMetadata));
    }

    private bool MetadataSettingsMatch(string[] platforms, string[] enabled) =>
        MetadataPlatforms.Select(row => row.Platform).SequenceEqual(platforms) &&
        MetadataPlatforms.Where(row => row.Enabled).Select(row => row.Platform).SequenceEqual(enabled);

    public Task SetMetadataEnabledAsync(MusicMetadataPlatformRow row, bool enabled)
    {
        if (!CanEditMetadata || !MetadataPlatforms.Contains(row) || row.Enabled == enabled)
            return Task.CompletedTask;
        var order = MetadataPlatforms.Select(value => value.Platform).ToArray();
        var active = MetadataPlatforms.Where(value => value.Enabled)
            .Select(value => value.Platform).ToHashSet(StringComparer.Ordinal);
        if (enabled) active.Add(row.Platform); else active.Remove(row.Platform);
        return SaveMetadataSettingsAsync(order, order.Where(active.Contains).ToArray());
    }

    public Task MoveMetadataPlatformAsync(MusicMetadataPlatformRow source, MusicMetadataPlatformRow target)
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
        metadataBusy = true;
        artworkPolicyKnown = false;
        Changed(nameof(CanEditMetadata));
        Changed(nameof(CurrentArtwork));
        RetireLyrics();
        return RunAsync(async () =>
        {
            try
            {
                var result = await Call("music.metadata.settings.save",
                    new { ifRevision = revision, platforms, enabled }, lifetime.Token);
                PublishMetadataSettings(result.GetProperty("settings"));
            }
            catch
            {
                try
                {
                    await ReadMetadataSettings(lifetime.Token);
                    if (MetadataSettingsMatch(platforms, enabled)) return;
                }
                catch
                {
                    metadataLoaded = false;
                    throw;
                }
                throw;
            }
            finally
            {
                metadataBusy = false;
                Changed(nameof(CanEditMetadata));
                if (playerDetailOpen && current is not null && !stopping)
                    await RestoreLyricsAsync();
            }
        });
    }
}
