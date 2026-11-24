-- Shared rebuildable media presentation caches: images, online lyrics, and chorus timestamps (Windows & iOS).
CREATE TABLE cached_images (
    resource_key TEXT PRIMARY KEY NOT NULL,
    filename TEXT NOT NULL,
    bytes INTEGER NOT NULL CHECK (bytes > 0),
    updated_at_ms INTEGER NOT NULL CHECK (updated_at_ms > 0)
) STRICT;

CREATE INDEX idx_cached_images_filename ON cached_images(filename);
CREATE INDEX idx_cached_images_updated ON cached_images(updated_at_ms, resource_key);

CREATE TABLE cached_music_lyrics (
    song_id TEXT PRIMARY KEY NOT NULL,
    source_key TEXT NOT NULL,
    updated_at_ms INTEGER NOT NULL,
    stored_bytes INTEGER NOT NULL CHECK (stored_bytes >= 0)
) STRICT;

CREATE INDEX idx_cached_music_lyrics_updated ON cached_music_lyrics(updated_at_ms, song_id);

CREATE TABLE cached_music_lyric_lines (
    song_id TEXT NOT NULL REFERENCES cached_music_lyrics(song_id) ON DELETE CASCADE,
    position INTEGER NOT NULL CHECK (position >= 0),
    line_index INTEGER NOT NULL CHECK (line_index >= 0),
    time_sec REAL NOT NULL,
    end_sec REAL,
    text TEXT NOT NULL,
    translation TEXT,
    romanization TEXT,
    PRIMARY KEY (song_id, position)
) STRICT;

CREATE TABLE cached_music_lyric_words (
    song_id TEXT NOT NULL,
    line_position INTEGER NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    time_sec REAL NOT NULL,
    duration_sec REAL NOT NULL,
    text TEXT NOT NULL,
    PRIMARY KEY (song_id, line_position, position),
    FOREIGN KEY (song_id, line_position) REFERENCES cached_music_lyric_lines(song_id, position) ON DELETE CASCADE
) STRICT;

CREATE TABLE cached_music_chorus (
    scope TEXT NOT NULL,
    platform TEXT NOT NULL,
    native_id TEXT NOT NULL,
    recording_duration REAL NOT NULL CHECK (recording_duration > 0),
    start_sec REAL NOT NULL CHECK (start_sec >= 0),
    end_sec REAL NOT NULL CHECK (end_sec > start_sec),
    updated_at_ms INTEGER NOT NULL,
    expires_at_ms INTEGER NOT NULL,
    PRIMARY KEY (scope, platform, native_id, recording_duration)
) STRICT;

CREATE INDEX idx_cached_music_chorus_updated ON cached_music_chorus(updated_at_ms);
CREATE INDEX idx_cached_music_chorus_expiry ON cached_music_chorus(expires_at_ms);
