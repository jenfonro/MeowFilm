-- Shared native music settings, appearance, source exclusions, and lyrics cache (Windows & iOS).
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
    color_palette TEXT NOT NULL,
    lock_lyric INTEGER NOT NULL DEFAULT 0 CHECK (lock_lyric IN (0, 1)),
    lyric_translation INTEGER NOT NULL DEFAULT 1 CHECK (lyric_translation IN (0, 1)),
    lyric_background INTEGER NOT NULL DEFAULT 0 CHECK (lyric_background IN (0, 1))
) STRICT;

CREATE TABLE music_playback_settings (
    scope TEXT PRIMARY KEY NOT NULL,
    revision INTEGER NOT NULL DEFAULT 0 CHECK (revision BETWEEN 0 AND 9007199254740991),
    mode INTEGER NOT NULL DEFAULT 1,
    has_mode INTEGER NOT NULL DEFAULT 0 CHECK (has_mode IN (0, 1)),
    lyrics_enabled INTEGER NOT NULL DEFAULT 1 CHECK (lyrics_enabled IN (0, 1)),
    online_revision INTEGER CHECK (online_revision IS NULL OR online_revision BETWEEN 0 AND 9007199254740991),
    show_translation INTEGER NOT NULL DEFAULT 1 CHECK (show_translation IN (0, 1)),
    quality_request TEXT,
    quality_revision INTEGER CHECK (quality_revision IS NULL OR quality_revision BETWEEN 0 AND 9007199254740991),
    official_revision INTEGER CHECK (official_revision IS NULL OR official_revision BETWEEN 0 AND 9007199254740991),
    metadata_revision INTEGER CHECK (metadata_revision IS NULL OR metadata_revision BETWEEN 0 AND 9007199254740991),
    exclusions_revision INTEGER CHECK (exclusions_revision IS NULL OR exclusions_revision BETWEEN 0 AND 9007199254740991),
    position_occurrence_id TEXT,
    position_seconds REAL NOT NULL DEFAULT 0
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
    exclusion_kind TEXT NOT NULL DEFAULT 'source' CHECK (exclusion_kind IN ('source', 'recording')),
    exclusion_id TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    song_id TEXT NOT NULL,
    provider TEXT NOT NULL DEFAULT '',
    platform TEXT NOT NULL DEFAULT '',
    provider_name TEXT NOT NULL DEFAULT '',
    title TEXT NOT NULL DEFAULT '',
    artist TEXT NOT NULL DEFAULT '',
    source_key TEXT,
    excluded_at INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (scope, exclusion_kind, exclusion_id)
) STRICT;

CREATE INDEX idx_music_source_exclusions_order ON music_source_exclusions(scope, exclusion_kind, position);
CREATE INDEX idx_music_source_exclusions_song ON music_source_exclusions(song_id);

CREATE TABLE music_lyrics (
    song_id TEXT PRIMARY KEY NOT NULL,
    source_key TEXT NOT NULL,
    updated_at REAL NOT NULL,
    stored_bytes INTEGER NOT NULL CHECK (stored_bytes >= 0)
) STRICT;

CREATE INDEX idx_music_lyrics_updated ON music_lyrics(updated_at);

CREATE TABLE music_lyric_lines (
    song_id TEXT NOT NULL REFERENCES music_lyrics(song_id) ON DELETE CASCADE,
    position INTEGER NOT NULL CHECK (position >= 0),
    line_index INTEGER NOT NULL CHECK (line_index >= 0),
    time_sec REAL NOT NULL,
    end_sec REAL,
    text TEXT NOT NULL,
    translation TEXT,
    romanization TEXT,
    PRIMARY KEY (song_id, position)
) STRICT;

CREATE TABLE music_lyric_words (
    song_id TEXT NOT NULL,
    line_position INTEGER NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    time_sec REAL NOT NULL,
    duration_sec REAL NOT NULL,
    offset_sec REAL,
    text TEXT NOT NULL,
    PRIMARY KEY (song_id, line_position, position),
    FOREIGN KEY (song_id, line_position) REFERENCES music_lyric_lines(song_id, position) ON DELETE CASCADE
) STRICT;
