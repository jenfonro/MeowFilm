-- Native iOS film favorites, player preferences, source probes, video intents, and deletions.
CREATE TABLE ios_film_favorites (
    businessID TEXT PRIMARY KEY,
    sourceID TEXT NOT NULL,
    itemID TEXT NOT NULL,
    kind TEXT,
    target_kind TEXT NOT NULL CHECK (target_kind IN ('movie', 'tv', 'source', 'group')),
    target_title TEXT NOT NULL,
    target_year INTEGER,
    metadata_provider TEXT,
    metadata_id_kind TEXT,
    metadata_id_text TEXT,
    season INTEGER,
    episode INTEGER,
    global_episode INTEGER,
    source_key TEXT,
    source_engine TEXT,
    source_installation_id TEXT,
    source_site_kind TEXT,
    source_site_text TEXT,
    source_detail_kind TEXT,
    source_detail_text TEXT,
    source_line_kind TEXT,
    source_line_text TEXT,
    row_revision INTEGER NOT NULL CHECK (row_revision BETWEEN 1 AND 9007199254740991),
    added_at REAL NOT NULL,
    CHECK ((metadata_id_kind IS NULL AND metadata_id_text IS NULL) OR
           (metadata_id_kind IN ('text', 'integer') AND metadata_id_text IS NOT NULL)),
    CHECK ((source_site_kind IS NULL AND source_site_text IS NULL) OR
           (source_site_kind IN ('text', 'integer') AND source_site_text IS NOT NULL)),
    CHECK ((source_detail_kind IS NULL AND source_detail_text IS NULL) OR
           (source_detail_kind IN ('text', 'integer') AND source_detail_text IS NOT NULL)),
    CHECK ((source_line_kind IS NULL AND source_line_text IS NULL) OR
           (source_line_kind IN ('text', 'integer') AND source_line_text IS NOT NULL))
) STRICT;

CREATE INDEX ios_film_favorites_page ON ios_film_favorites(added_at DESC, businessID);

CREATE TABLE ios_film_favorite_members (
    businessID TEXT NOT NULL REFERENCES ios_film_favorites(businessID) ON DELETE CASCADE,
    ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
    source_key TEXT NOT NULL,
    detail_kind TEXT NOT NULL CHECK (detail_kind IN ('text', 'integer')),
    detail_text TEXT NOT NULL,
    PRIMARY KEY (businessID, ordinal)
) STRICT;

CREATE TABLE ios_film_player_preferences (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    engine TEXT NOT NULL,
    defaultRate REAL NOT NULL,
    holdRate REAL NOT NULL,
    seekStep REAL NOT NULL,
    showsControlCenter INTEGER NOT NULL CHECK (showsControlCenter IN (0, 1)),
    recognizesChapters INTEGER NOT NULL CHECK (recognizesChapters IN (0, 1)),
    singleTapCenter TEXT NOT NULL,
    singleTapSides TEXT NOT NULL,
    doubleTapCenter TEXT NOT NULL,
    doubleTapSides TEXT NOT NULL,
    swipeVolume INTEGER NOT NULL CHECK (swipeVolume IN (0, 1)),
    swipeBrightness INTEGER NOT NULL CHECK (swipeBrightness IN (0, 1)),
    swipeSeek INTEGER NOT NULL CHECK (swipeSeek IN (0, 1)),
    swipeEpisodes INTEGER NOT NULL CHECK (swipeEpisodes IN (0, 1)),
    hapticHold INTEGER NOT NULL CHECK (hapticHold IN (0, 1)),
    hapticBrightness INTEGER NOT NULL CHECK (hapticBrightness IN (0, 1)),
    hapticVolume INTEGER NOT NULL CHECK (hapticVolume IN (0, 1)),
    hapticSeek INTEGER NOT NULL CHECK (hapticSeek IN (0, 1)),
    row_revision INTEGER NOT NULL CHECK (row_revision BETWEEN 1 AND 9007199254740991)
) STRICT;

CREATE TABLE ios_film_source_state (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    revision INTEGER NOT NULL CHECK (revision BETWEEN 0 AND 9007199254740991)
) STRICT;

INSERT INTO ios_film_source_state VALUES (1, 0);

CREATE TABLE ios_film_source_probes (
    installationId TEXT PRIMARY KEY,
    inputRevision INTEGER NOT NULL,
    importedAt INTEGER,
    updatedAt INTEGER,
    updateState TEXT NOT NULL,
    updateError TEXT,
    sourceHash TEXT
) STRICT;

CREATE TABLE ios_film_source_sites (
    installationId TEXT NOT NULL REFERENCES ios_film_source_probes(installationId) ON DELETE CASCADE,
    siteKey TEXT NOT NULL,
    ordinal INTEGER NOT NULL,
    name TEXT NOT NULL,
    api TEXT NOT NULL,
    PRIMARY KEY (installationId, siteKey)
) STRICT;

CREATE TABLE ios_film_source_actions (
    installationId TEXT NOT NULL,
    siteKey TEXT NOT NULL,
    ordinal INTEGER NOT NULL,
    action TEXT NOT NULL,
    PRIMARY KEY (installationId, siteKey, ordinal),
    FOREIGN KEY (installationId, siteKey) REFERENCES ios_film_source_sites(installationId, siteKey) ON DELETE CASCADE
) STRICT;

CREATE TABLE ios_film_source_methods (
    installationId TEXT NOT NULL,
    siteKey TEXT NOT NULL,
    action TEXT NOT NULL,
    method TEXT NOT NULL,
    PRIMARY KEY (installationId, siteKey, action),
    FOREIGN KEY (installationId, siteKey) REFERENCES ios_film_source_sites(installationId, siteKey) ON DELETE CASCADE
) STRICT;

CREATE TABLE ios_film_video_intents (
    installationId TEXT NOT NULL,
    siteKey TEXT NOT NULL,
    ordinal INTEGER NOT NULL,
    name TEXT NOT NULL,
    api TEXT NOT NULL,
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
    homeVisible INTEGER NOT NULL CHECK (homeVisible IN (0, 1)),
    searchEnabled INTEGER NOT NULL CHECK (searchEnabled IN (0, 1)),
    aggregateImages INTEGER NOT NULL CHECK (aggregateImages IN (0, 1)),
    PRIMARY KEY (installationId, siteKey)
) STRICT;

CREATE UNIQUE INDEX ios_film_one_aggregate_images ON ios_film_video_intents(aggregateImages) WHERE aggregateImages = 1;

CREATE TABLE ios_film_video_actions (
    installationId TEXT NOT NULL,
    siteKey TEXT NOT NULL,
    ordinal INTEGER NOT NULL,
    action TEXT NOT NULL,
    PRIMARY KEY (installationId, siteKey, ordinal),
    FOREIGN KEY (installationId, siteKey) REFERENCES ios_film_video_intents(installationId, siteKey) ON DELETE CASCADE
) STRICT;

CREATE TABLE ios_film_video_methods (
    installationId TEXT NOT NULL,
    siteKey TEXT NOT NULL,
    action TEXT NOT NULL,
    method TEXT NOT NULL,
    PRIMARY KEY (installationId, siteKey, action),
    FOREIGN KEY (installationId, siteKey) REFERENCES ios_film_video_intents(installationId, siteKey) ON DELETE CASCADE
) STRICT;

CREATE TABLE ios_film_video_detections (
    installationId TEXT NOT NULL,
    siteKey TEXT NOT NULL,
    result TEXT NOT NULL,
    checkedAt INTEGER NOT NULL CHECK (checkedAt BETWEEN 0 AND 9007199254740991),
    playbackResolved INTEGER NOT NULL CHECK (playbackResolved IN (0, 1)),
    PRIMARY KEY (installationId, siteKey),
    FOREIGN KEY (installationId, siteKey) REFERENCES ios_film_video_intents(installationId, siteKey) ON DELETE CASCADE
) STRICT;

CREATE TABLE ios_film_video_probe_steps (
    installationId TEXT NOT NULL,
    siteKey TEXT NOT NULL,
    ordinal INTEGER NOT NULL,
    operation TEXT NOT NULL,
    state TEXT NOT NULL,
    error TEXT,
    PRIMARY KEY (installationId, siteKey, ordinal),
    FOREIGN KEY (installationId, siteKey) REFERENCES ios_film_video_detections(installationId, siteKey) ON DELETE CASCADE
) STRICT;

CREATE TABLE ios_film_source_deletions (
    installationId TEXT NOT NULL,
    siteKey TEXT NOT NULL,
    PRIMARY KEY (installationId, siteKey)
) STRICT;
