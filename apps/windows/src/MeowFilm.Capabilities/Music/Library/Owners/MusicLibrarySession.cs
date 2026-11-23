namespace MeowFilm.Capabilities.Library;

/// <summary>
/// Pure strongly-typed transactional working set over MusicLibraryDatabase.
/// Holds lazy-loaded domain records and stages incremental mutations directly into MusicLibraryCommit.
/// </summary>
internal sealed class MusicLibrarySession
{
    private readonly MusicLibraryDatabase store;
    private readonly MusicLibrarySession? parent;
    private readonly bool writable;
    private readonly HashSet<string>? knownSongs;

    private bool libraryLoaded, libraryDirty;
    private MusicLibraryAreaRecord? libraryPrev, libraryCurrent;

    private bool queueLoaded, queueDirty;
    private MusicQueueStateRecord? queuePrev, queueCurrent;

    private bool playbackModeLoaded, playbackModeDirty;
    private MusicPlaybackModeRecord? playbackModeCurrent;

    private bool onlineSettingsLoaded, onlineSettingsDirty;
    private MusicOnlineSettingsRecord? onlineSettingsCurrent;

    private bool deviceSettingsLoaded, deviceSettingsDirty;
    private MusicDeviceSettingsRecord? deviceSettingsCurrent;

    private bool appearanceLoaded, appearanceDirty;
    private MusicAppearanceRecord? appearanceCurrent;

    private bool catalogSourceLoaded, catalogSourceDirty;
    private MusicCatalogSourceRecord? catalogSourceCurrent;

    private bool desktopLyricsLoaded, desktopLyricsDirty;
    private MusicDesktopLyricsRecord? desktopLyricsCurrent;

    private bool qualityPolicyLoaded, qualityPolicyDirty;
    private MusicQualityPolicyRecord? qualityPolicyPrev, qualityPolicyCurrent;

    private bool officialSourcesLoaded, officialSourcesDirty;
    private MusicOfficialSourcesRecord? officialSourcesPrev, officialSourcesCurrent;

    private bool metadataPolicyLoaded, metadataPolicyDirty;
    private MusicMetadataPolicyRecord? metadataPolicyPrev, metadataPolicyCurrent;

    private bool searchPolicyLoaded, searchPolicyDirty;
    private MusicSearchPolicyRecord? searchPolicyPrev, searchPolicyCurrent;

    private bool searchHistoryLoaded, searchHistoryDirty;
    private MusicSearchHistoryRecord? searchHistoryPrev, searchHistoryCurrent;

    private bool sourceExclusionsRevLoaded, sourceExclusionsRevDirty;
    private long? sourceExclusionsRevCurrent;
    private readonly List<MusicSourceExclusionMutation> sourceExclusionMutations = [];

    private readonly List<MusicHistoryPlaylistMutation> historyPlaylistMutations = [];
    private readonly List<MusicHistoryDayMutation> historyDayMutations = [];
    private readonly List<MusicLocalLyricMutation> localLyricMutations = [];

    private bool librarySyncLoaded, librarySyncDirty;
    private MusicLibrarySyncStateRecord? librarySyncPrev, librarySyncCurrent;

    internal MusicLibrarySession(
        MusicLibraryDatabase store,
        string scope,
        MusicLibraryRevision revision,
        MusicLibrarySession? parent = null,
        bool writable = false,
        HashSet<string>? knownSongs = null)
    {
        this.store = store;
        Scope = scope;
        Revision = revision;
        this.parent = parent;
        this.writable = writable;
        this.knownSongs = knownSongs ?? parent?.knownSongs ?? new HashSet<string>(StringComparer.Ordinal);
        Songs = new MusicSongSession(store, knownSongs: this.knownSongs);
    }

    internal string Scope { get; }
    internal MusicLibraryRevision Revision { get; }
    internal MusicSongSession Songs { get; }
    internal bool IsQueueLoaded => queueLoaded || parent?.IsQueueLoaded == true;
    internal bool IsMetadataPolicyLoaded => metadataPolicyLoaded || parent?.IsMetadataPolicyLoaded == true;

    internal MusicLibrarySession Candidate(bool forWrite) =>
        new(store, Scope, Revision, parent: this, writable: forWrite, knownSongs: knownSongs);

    private void RequireWritable() => MusicRules.Require(writable, "INVALID_REQUEST");

    // ---- Library ----
    internal MusicLibraryAreaRecord? ReadLibrary()
    {
        if (!libraryLoaded)
        {
            libraryCurrent = parent is not null ? parent.ReadLibrary() : store.ReadLibrary(Scope, Revision);
            libraryPrev = libraryCurrent;
            libraryLoaded = true;
        }
        return libraryCurrent;
    }

    internal void SetLibrary(MusicLibraryAreaRecord next)
    {
        RequireWritable();
        if (!libraryLoaded) _ = ReadLibrary();
        libraryCurrent = next;
        libraryDirty = true;
    }

    // ---- Queue ----
    internal MusicQueueStateRecord? ReadQueue()
    {
        if (!queueLoaded)
        {
            queueCurrent = parent is not null ? parent.ReadQueue() : store.ReadQueue(Scope, Revision);
            queuePrev = queueCurrent;
            queueLoaded = true;
        }
        return queueCurrent;
    }

    internal void SetQueue(MusicQueueStateRecord next)
    {
        RequireWritable();
        if (!queueLoaded) _ = ReadQueue();
        queueCurrent = next;
        queueDirty = true;
    }

    // ---- History ----
    internal IReadOnlyList<MusicHistoryPlaylistSummaryRecord> ReadHistoryPlaylistSummaries(string? playlistId = null) =>
        store.ReadHistoryPlaylistSummaries(Scope, Revision, playlistId);

    internal MusicHistoryPlaylistRecord? ReadHistoryPlaylist(string playlistId) =>
        store.ReadHistoryPlaylist(Scope, Revision, playlistId);

    internal IReadOnlyList<MusicQueueEntryRecord> ReadHistoryPlaylistEntries(string playlistId, string? entryId = null) =>
        store.ReadHistoryPlaylistEntries(Scope, Revision, playlistId, entryId);

    internal IReadOnlyList<MusicHistoryDaySummaryRecord> ReadHistoryDaySummaries(string? dayDate = null) =>
        store.ReadHistoryDaySummaries(Scope, Revision, dayDate);

    internal MusicHistoryDayRecord? ReadHistoryDay(string dayDate) =>
        store.ReadHistoryDay(Scope, Revision, dayDate);

    internal IReadOnlyList<MusicHistoryDaySongRecord> ReadHistoryDaySongs(string dayDate, string? songId = null) =>
        store.ReadHistoryDaySongs(Scope, Revision, dayDate, songId);

    internal void StageHistoryPlaylist(MusicHistoryPlaylistMutation mutation)
    {
        RequireWritable();
        historyPlaylistMutations.Add(mutation);
    }

    internal void StageHistoryDay(MusicHistoryDayMutation mutation)
    {
        RequireWritable();
        historyDayMutations.Add(mutation);
    }

    // ---- Playback / Settings / Appearance / Policies ----
    internal MusicPlaybackModeRecord? ReadPlaybackMode()
    {
        if (!playbackModeLoaded)
        {
            playbackModeCurrent = parent is not null ? parent.ReadPlaybackMode() : store.ReadPlaybackMode(Scope, Revision);
            playbackModeLoaded = true;
        }
        return playbackModeCurrent;
    }

    internal void SetPlaybackMode(MusicPlaybackModeRecord next)
    {
        RequireWritable();
        playbackModeCurrent = next;
        playbackModeLoaded = true;
        playbackModeDirty = true;
    }

    internal MusicOnlineSettingsRecord? ReadOnlineSettings()
    {
        if (!onlineSettingsLoaded)
        {
            onlineSettingsCurrent = parent is not null ? parent.ReadOnlineSettings() : store.ReadOnlineSettings(Scope, Revision);
            onlineSettingsLoaded = true;
        }
        return onlineSettingsCurrent;
    }

    internal void SetOnlineSettings(MusicOnlineSettingsRecord next)
    {
        RequireWritable();
        onlineSettingsCurrent = next;
        onlineSettingsLoaded = true;
        onlineSettingsDirty = true;
    }

    internal MusicDeviceSettingsRecord? ReadDeviceSettings()
    {
        if (!deviceSettingsLoaded)
        {
            deviceSettingsCurrent = parent is not null ? parent.ReadDeviceSettings() : store.ReadDeviceSettings(Scope, Revision);
            deviceSettingsLoaded = true;
        }
        return deviceSettingsCurrent;
    }

    internal void SetDeviceSettings(MusicDeviceSettingsRecord next)
    {
        RequireWritable();
        deviceSettingsCurrent = next;
        deviceSettingsLoaded = true;
        deviceSettingsDirty = true;
    }

    internal MusicAppearanceRecord? ReadAppearance()
    {
        if (!appearanceLoaded)
        {
            appearanceCurrent = parent is not null ? parent.ReadAppearance() : store.ReadAppearance(Scope, Revision);
            appearanceLoaded = true;
        }
        return appearanceCurrent;
    }

    internal void SetAppearance(MusicAppearanceRecord next)
    {
        RequireWritable();
        appearanceCurrent = next;
        appearanceLoaded = true;
        appearanceDirty = true;
    }

    internal MusicCatalogSourceRecord? ReadCatalogSource()
    {
        if (!catalogSourceLoaded)
        {
            catalogSourceCurrent = parent is not null ? parent.ReadCatalogSource() : store.ReadCatalogSource(Scope, Revision);
            catalogSourceLoaded = true;
        }
        return catalogSourceCurrent;
    }

    internal void SetCatalogSource(MusicCatalogSourceRecord next)
    {
        RequireWritable();
        catalogSourceCurrent = next;
        catalogSourceLoaded = true;
        catalogSourceDirty = true;
    }

    internal MusicDesktopLyricsRecord? ReadDesktopLyrics()
    {
        if (!desktopLyricsLoaded)
        {
            desktopLyricsCurrent = parent is not null ? parent.ReadDesktopLyrics() : store.ReadDesktopLyrics(Scope, Revision);
            desktopLyricsLoaded = true;
        }
        return desktopLyricsCurrent;
    }

    internal void SetDesktopLyrics(MusicDesktopLyricsRecord next)
    {
        RequireWritable();
        desktopLyricsCurrent = next;
        desktopLyricsLoaded = true;
        desktopLyricsDirty = true;
    }

    internal MusicQualityPolicyRecord? ReadQualityPolicy()
    {
        if (!qualityPolicyLoaded)
        {
            qualityPolicyCurrent = parent is not null ? parent.ReadQualityPolicy() : store.ReadQualityPolicy(Scope, Revision);
            qualityPolicyPrev = qualityPolicyCurrent;
            qualityPolicyLoaded = true;
        }
        return qualityPolicyCurrent;
    }

    internal void SetQualityPolicy(MusicQualityPolicyRecord next)
    {
        RequireWritable();
        if (!qualityPolicyLoaded) _ = ReadQualityPolicy();
        qualityPolicyCurrent = next;
        qualityPolicyDirty = true;
    }

    internal MusicOfficialSourcesRecord? ReadOfficialSources()
    {
        if (!officialSourcesLoaded)
        {
            officialSourcesCurrent = parent is not null ? parent.ReadOfficialSources() : store.ReadOfficialSources(Scope, Revision);
            officialSourcesPrev = officialSourcesCurrent;
            officialSourcesLoaded = true;
        }
        return officialSourcesCurrent;
    }

    internal void SetOfficialSources(MusicOfficialSourcesRecord next)
    {
        RequireWritable();
        if (!officialSourcesLoaded) _ = ReadOfficialSources();
        officialSourcesCurrent = next;
        officialSourcesDirty = true;
    }

    internal MusicMetadataPolicyRecord? ReadMetadataPolicy()
    {
        if (!metadataPolicyLoaded)
        {
            metadataPolicyCurrent = parent is not null ? parent.ReadMetadataPolicy() : store.ReadMetadataPolicy(Scope, Revision);
            metadataPolicyPrev = metadataPolicyCurrent;
            metadataPolicyLoaded = true;
        }
        return metadataPolicyCurrent;
    }

    internal void SetMetadataPolicy(MusicMetadataPolicyRecord next)
    {
        RequireWritable();
        if (!metadataPolicyLoaded) _ = ReadMetadataPolicy();
        metadataPolicyCurrent = next;
        metadataPolicyDirty = true;
    }

    internal MusicSearchPolicyRecord? ReadSearchPolicy()
    {
        if (!searchPolicyLoaded)
        {
            searchPolicyCurrent = parent is not null ? parent.ReadSearchPolicy() : store.ReadSearchPolicy(Scope, Revision);
            searchPolicyPrev = searchPolicyCurrent;
            searchPolicyLoaded = true;
        }
        return searchPolicyCurrent;
    }

    internal void SetSearchPolicy(MusicSearchPolicyRecord next)
    {
        RequireWritable();
        if (!searchPolicyLoaded) _ = ReadSearchPolicy();
        searchPolicyCurrent = next;
        searchPolicyDirty = true;
    }

    internal MusicSearchHistoryRecord? ReadSearchHistory()
    {
        if (!searchHistoryLoaded)
        {
            searchHistoryCurrent = parent is not null ? parent.ReadSearchHistory() : store.ReadSearchHistory(Scope, Revision);
            searchHistoryPrev = searchHistoryCurrent;
            searchHistoryLoaded = true;
        }
        return searchHistoryCurrent;
    }

    internal void SetSearchHistory(MusicSearchHistoryRecord next)
    {
        RequireWritable();
        if (!searchHistoryLoaded) _ = ReadSearchHistory();
        searchHistoryCurrent = next;
        searchHistoryDirty = true;
    }

    // ---- Source exclusions ----
    internal long? ReadSourceExclusionsRevision()
    {
        if (!sourceExclusionsRevLoaded)
        {
            sourceExclusionsRevCurrent = parent is not null
                ? parent.ReadSourceExclusionsRevision()
                : store.ReadSourceExclusionsRevision(Scope, Revision);
            sourceExclusionsRevLoaded = true;
        }
        return sourceExclusionsRevCurrent;
    }

    internal IReadOnlyList<MusicSourceExclusionRecord> ReadSourceExclusions(string? exclusionId = null) =>
        store.ReadSourceExclusions(Scope, Revision, exclusionId);

    internal void SetSourceExclusionsRevision(long nextRevision)
    {
        RequireWritable();
        sourceExclusionsRevCurrent = nextRevision;
        sourceExclusionsRevLoaded = true;
        sourceExclusionsRevDirty = true;
    }

    internal void StageSourceExclusion(MusicSourceExclusionMutation mutation)
    {
        RequireWritable();
        sourceExclusionMutations.Add(mutation);
    }

    // ---- Local lyrics ----
    internal IReadOnlyList<MusicLocalLyricSummaryRecord> ReadLocalLyricSummaries(string? songId = null) =>
        store.ReadLocalLyricSummaries(Scope, Revision, songId);

    internal MusicLocalLyricRecord? ReadLocalLyric(string songId) =>
        store.ReadLocalLyric(Scope, Revision, songId);

    internal void StageLocalLyric(MusicLocalLyricMutation mutation)
    {
        RequireWritable();
        localLyricMutations.Add(mutation);
    }

    // ---- Library Sync ----
    internal MusicLibrarySyncStateRecord? ReadLibrarySync()
    {
        if (!librarySyncLoaded)
        {
            librarySyncCurrent = parent is not null ? parent.ReadLibrarySync() : store.ReadLibrarySync(Scope, Revision);
            librarySyncPrev = librarySyncCurrent;
            librarySyncLoaded = true;
        }
        return librarySyncCurrent;
    }

    internal void SetLibrarySync(MusicLibrarySyncStateRecord next)
    {
        RequireWritable();
        if (!librarySyncLoaded) _ = ReadLibrarySync();
        librarySyncCurrent = next;
        librarySyncDirty = true;
    }

    // ---- Commit & Publish ----
    internal MusicLibraryCommit BuildCommit() => new()
    {
        Songs = Songs.BuildMutations(),
        Library = libraryDirty && libraryCurrent is not null ? (libraryPrev, libraryCurrent) : null,
        Queue = queueDirty && queueCurrent is not null ? (queuePrev, queueCurrent) : null,
        HistoryPlaylists = historyPlaylistMutations.ToArray(),
        HistoryDays = historyDayMutations.ToArray(),
        PlaybackMode = playbackModeDirty ? playbackModeCurrent : null,
        OnlineSettings = onlineSettingsDirty ? onlineSettingsCurrent : null,
        DeviceSettings = deviceSettingsDirty ? deviceSettingsCurrent : null,
        Appearance = appearanceDirty ? appearanceCurrent : null,
        CatalogSource = catalogSourceDirty ? catalogSourceCurrent : null,
        DesktopLyrics = desktopLyricsDirty ? desktopLyricsCurrent : null,
        QualityPolicy = qualityPolicyDirty && qualityPolicyCurrent is not null ? (qualityPolicyPrev, qualityPolicyCurrent) : null,
        OfficialSources = officialSourcesDirty && officialSourcesCurrent is not null ? (officialSourcesPrev, officialSourcesCurrent) : null,
        MetadataPolicy = metadataPolicyDirty && metadataPolicyCurrent is not null ? (metadataPolicyPrev, metadataPolicyCurrent) : null,
        SearchPolicy = searchPolicyDirty && searchPolicyCurrent is not null ? (searchPolicyPrev, searchPolicyCurrent) : null,
        SearchHistory = searchHistoryDirty && searchHistoryCurrent is not null ? (searchHistoryPrev, searchHistoryCurrent) : null,
        SourceExclusionsRevision = sourceExclusionsRevDirty ? sourceExclusionsRevCurrent : null,
        SourceExclusions = sourceExclusionMutations.ToArray(),
        LocalLyrics = localLyricMutations.ToArray(),
        LibrarySync = librarySyncDirty && librarySyncCurrent is not null ? (librarySyncPrev, librarySyncCurrent) : null
    };

    internal MusicLibrarySession Publish(MusicLibraryRevision nextRevision, MusicLibraryCommit commit)
    {
        foreach (var song in commit.Songs)
            knownSongs?.Add(song.Record.SongId);

        var next = new MusicLibrarySession(store, Scope, nextRevision, knownSongs: knownSongs);
        if (libraryLoaded) { next.libraryLoaded = true; next.libraryCurrent = next.libraryPrev = libraryCurrent; }
        else if (parent?.libraryLoaded == true) { next.libraryLoaded = true; next.libraryCurrent = next.libraryPrev = parent.libraryCurrent; }

        if (queueLoaded) { next.queueLoaded = true; next.queueCurrent = next.queuePrev = queueCurrent; }
        else if (parent?.queueLoaded == true) { next.queueLoaded = true; next.queueCurrent = next.queuePrev = parent.queueCurrent; }

        if (playbackModeLoaded) { next.playbackModeLoaded = true; next.playbackModeCurrent = playbackModeCurrent; }
        else if (parent?.playbackModeLoaded == true) { next.playbackModeLoaded = true; next.playbackModeCurrent = parent.playbackModeCurrent; }

        if (onlineSettingsLoaded) { next.onlineSettingsLoaded = true; next.onlineSettingsCurrent = onlineSettingsCurrent; }
        else if (parent?.onlineSettingsLoaded == true) { next.onlineSettingsLoaded = true; next.onlineSettingsCurrent = parent.onlineSettingsCurrent; }

        if (deviceSettingsLoaded) { next.deviceSettingsLoaded = true; next.deviceSettingsCurrent = deviceSettingsCurrent; }
        else if (parent?.deviceSettingsLoaded == true) { next.deviceSettingsLoaded = true; next.deviceSettingsCurrent = parent.deviceSettingsCurrent; }

        if (appearanceLoaded) { next.appearanceLoaded = true; next.appearanceCurrent = appearanceCurrent; }
        else if (parent?.appearanceLoaded == true) { next.appearanceLoaded = true; next.appearanceCurrent = parent.appearanceCurrent; }

        if (catalogSourceLoaded) { next.catalogSourceLoaded = true; next.catalogSourceCurrent = catalogSourceCurrent; }
        else if (parent?.catalogSourceLoaded == true) { next.catalogSourceLoaded = true; next.catalogSourceCurrent = parent.catalogSourceCurrent; }

        if (desktopLyricsLoaded) { next.desktopLyricsLoaded = true; next.desktopLyricsCurrent = desktopLyricsCurrent; }
        else if (parent?.desktopLyricsLoaded == true) { next.desktopLyricsLoaded = true; next.desktopLyricsCurrent = parent.desktopLyricsCurrent; }

        if (qualityPolicyLoaded) { next.qualityPolicyLoaded = true; next.qualityPolicyCurrent = next.qualityPolicyPrev = qualityPolicyCurrent; }
        else if (parent?.qualityPolicyLoaded == true) { next.qualityPolicyLoaded = true; next.qualityPolicyCurrent = next.qualityPolicyPrev = parent.qualityPolicyCurrent; }

        if (officialSourcesLoaded) { next.officialSourcesLoaded = true; next.officialSourcesCurrent = next.officialSourcesPrev = officialSourcesCurrent; }
        else if (parent?.officialSourcesLoaded == true) { next.officialSourcesLoaded = true; next.officialSourcesCurrent = next.officialSourcesPrev = parent.officialSourcesCurrent; }

        if (metadataPolicyLoaded) { next.metadataPolicyLoaded = true; next.metadataPolicyCurrent = next.metadataPolicyPrev = metadataPolicyCurrent; }
        else if (parent?.metadataPolicyLoaded == true) { next.metadataPolicyLoaded = true; next.metadataPolicyCurrent = next.metadataPolicyPrev = parent.metadataPolicyCurrent; }

        if (searchPolicyLoaded) { next.searchPolicyLoaded = true; next.searchPolicyCurrent = next.searchPolicyPrev = searchPolicyCurrent; }
        else if (parent?.searchPolicyLoaded == true) { next.searchPolicyLoaded = true; next.searchPolicyCurrent = next.searchPolicyPrev = parent.searchPolicyCurrent; }

        if (searchHistoryLoaded) { next.searchHistoryLoaded = true; next.searchHistoryCurrent = next.searchHistoryPrev = searchHistoryCurrent; }
        else if (parent?.searchHistoryLoaded == true) { next.searchHistoryLoaded = true; next.searchHistoryCurrent = next.searchHistoryPrev = parent.searchHistoryCurrent; }

        if (sourceExclusionsRevLoaded) { next.sourceExclusionsRevLoaded = true; next.sourceExclusionsRevCurrent = sourceExclusionsRevCurrent; }
        else if (parent?.sourceExclusionsRevLoaded == true) { next.sourceExclusionsRevLoaded = true; next.sourceExclusionsRevCurrent = parent.sourceExclusionsRevCurrent; }

        if (librarySyncLoaded) { next.librarySyncLoaded = true; next.librarySyncCurrent = next.librarySyncPrev = librarySyncCurrent; }
        else if (parent?.librarySyncLoaded == true) { next.librarySyncLoaded = true; next.librarySyncCurrent = next.librarySyncPrev = parent.librarySyncCurrent; }

        return next;
    }
}
