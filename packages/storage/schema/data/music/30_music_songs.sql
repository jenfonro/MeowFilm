-- Shared native music permanent song identities and platform source facts (owner CAS uses owner_revisions).
CREATE TABLE music_songs (
    song_id TEXT PRIMARY KEY NOT NULL,
    title TEXT NOT NULL DEFAULT '',
    artist TEXT NOT NULL DEFAULT '',
    album TEXT NOT NULL DEFAULT '',
    duration REAL CHECK (duration IS NULL OR duration > 0),
    baseline_duration REAL CHECK (baseline_duration IS NULL OR baseline_duration > 0),
    cover_url TEXT NOT NULL DEFAULT '',
    local_file_id TEXT,
    artwork_source_key TEXT,
    artwork_source_image TEXT,
    match_key TEXT,
    search_query TEXT
) STRICT;

CREATE INDEX idx_music_songs_match_key ON music_songs(match_key);
CREATE INDEX idx_music_songs_search ON music_songs(search_query);

CREATE TABLE music_song_artists (
    song_id TEXT NOT NULL REFERENCES music_songs(song_id) ON DELETE CASCADE,
    position INTEGER NOT NULL CHECK (position >= 0),
    name TEXT NOT NULL,
    PRIMARY KEY (song_id, position)
) STRICT;

-- Each external recording source belongs uniquely to one permanent song_id.
-- Opaque third-party platform wire fields and format lists live in music_song_source_extras.
CREATE TABLE music_song_sources (
    song_id TEXT NOT NULL REFERENCES music_songs(song_id) ON DELETE CASCADE,
    source_key TEXT NOT NULL UNIQUE,
    position INTEGER NOT NULL CHECK (position >= 0),
    platform TEXT NOT NULL,
    info_source TEXT NOT NULL,
    songmid TEXT,
    mid TEXT,
    native_id TEXT,
    hash TEXT,
    name TEXT,
    singer TEXT,
    album_name TEXT,
    duration REAL CHECK (duration IS NULL OR duration > 0),
    img TEXT,
    quality_known INTEGER CHECK (quality_known IS NULL OR quality_known IN (0, 1)),
    PRIMARY KEY (song_id, source_key)
) STRICT;

CREATE INDEX idx_music_song_sources_order ON music_song_sources(song_id, position);

CREATE TABLE music_song_source_artists (
    song_id TEXT NOT NULL,
    source_key TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    name TEXT NOT NULL,
    PRIMARY KEY (song_id, source_key, position),
    FOREIGN KEY (song_id, source_key) REFERENCES music_song_sources(song_id, source_key) ON DELETE CASCADE
) STRICT;

-- Bounded third-party platform wire metadata (types, audioFormats, newRateFormats, upstream IDs & extra fields).
CREATE TABLE music_song_source_extras (
    song_id TEXT NOT NULL,
    source_key TEXT NOT NULL,
    node_id INTEGER NOT NULL CHECK (node_id >= 0),
    parent_id INTEGER,
    prop_key TEXT,
    item_index INTEGER,
    kind TEXT NOT NULL CHECK (kind IN ('null', 'boolean', 'integer', 'real', 'number_text', 'text', 'array', 'object')),
    bool_value INTEGER CHECK (bool_value IN (0, 1)),
    int_value INTEGER,
    real_value REAL,
    text_value TEXT,
    PRIMARY KEY (song_id, source_key, node_id),
    FOREIGN KEY (song_id, source_key) REFERENCES music_song_sources(song_id, source_key) ON DELETE CASCADE
) STRICT;
