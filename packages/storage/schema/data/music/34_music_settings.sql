-- Shared native music settings, appearance, source exclusions, and search policy (Windows & iOS).
CREATE TABLE music_catalog_source (
    scope TEXT PRIMARY KEY NOT NULL,
    revision INTEGER NOT NULL DEFAULT 0 CHECK (revision BETWEEN 0 AND 9007199254740991),
    platform TEXT NOT NULL DEFAULT 'wy'
) STRICT;

CREATE TABLE music_appearance (
    scope TEXT PRIMARY KEY NOT NULL,
    revision INTEGER NOT NULL DEFAULT 0 CHECK (revision BETWEEN 0 AND 9007199254740991),
    playlist_view TEXT NOT NULL,
    player_theme TEXT NOT NULL,
    color_mode TEXT NOT NULL,
    color_palette TEXT NOT NULL
) STRICT;

CREATE TABLE music_playback_settings (
    scope TEXT PRIMARY KEY NOT NULL,
    revision INTEGER NOT NULL DEFAULT 0 CHECK (revision BETWEEN 0 AND 9007199254740991),
    mode INTEGER NOT NULL DEFAULT 1 CHECK (mode IN (1, 2, 3)),
    lyrics_enabled INTEGER NOT NULL DEFAULT 1 CHECK (lyrics_enabled IN (0, 1)),
    quality_request TEXT
) STRICT;

CREATE TABLE music_playback_lists (
    scope TEXT NOT NULL REFERENCES music_playback_settings(scope) ON DELETE CASCADE,
    list_kind TEXT NOT NULL CHECK (list_kind IN (
        'qualityOrder', 'qualityEnabled', 'spatialOrder', 'spatialEnabled',
        'officialPlatforms', 'officialEnabled', 'metadataPlatforms', 'metadataEnabled'
    )),
    position INTEGER NOT NULL CHECK (position >= 0),
    item_value TEXT NOT NULL,
    PRIMARY KEY (scope, list_kind, position)
) STRICT;

CREATE TABLE music_source_exclusions (
    scope TEXT NOT NULL REFERENCES music_playback_settings(scope) ON DELETE CASCADE,
    exclusion_id TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    song_id TEXT NOT NULL,
    provider TEXT NOT NULL DEFAULT '',
    platform TEXT NOT NULL DEFAULT '',
    provider_name TEXT NOT NULL DEFAULT '',
    title TEXT NOT NULL DEFAULT '',
    artist TEXT NOT NULL DEFAULT '',
    excluded_at_ms INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (scope, exclusion_id)
) STRICT;

CREATE INDEX idx_music_source_exclusions_order ON music_source_exclusions(scope, position);
CREATE INDEX idx_music_source_exclusions_song ON music_source_exclusions(song_id);

CREATE TABLE music_search_policy (
    scope TEXT PRIMARY KEY NOT NULL,
    revision INTEGER NOT NULL DEFAULT 0 CHECK (revision BETWEEN 0 AND 9007199254740991),
    songs_pages INTEGER NOT NULL CHECK (songs_pages >= 1),
    songs_concurrency INTEGER NOT NULL CHECK (songs_concurrency >= 1),
    songs_cache_minutes INTEGER NOT NULL CHECK (songs_cache_minutes BETWEEN 1 AND 1440),
    playlists_pages INTEGER NOT NULL CHECK (playlists_pages >= 1),
    playlists_concurrency INTEGER NOT NULL CHECK (playlists_concurrency >= 1),
    playlists_cache_minutes INTEGER NOT NULL CHECK (playlists_cache_minutes BETWEEN 1 AND 1440)
) STRICT;

CREATE TABLE music_search_policy_platforms (
    scope TEXT NOT NULL REFERENCES music_search_policy(scope) ON DELETE CASCADE,
    target_kind TEXT NOT NULL CHECK (target_kind IN ('songs', 'playlists')),
    list_kind TEXT NOT NULL CHECK (list_kind IN ('order', 'enabled')),
    position INTEGER NOT NULL CHECK (position >= 0),
    platform TEXT NOT NULL,
    PRIMARY KEY (scope, target_kind, list_kind, position)
) STRICT;
