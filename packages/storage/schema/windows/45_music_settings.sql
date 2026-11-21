-- Windows native music settings, policies, search history, source exclusions, and local lyrics.
CREATE TABLE windows_music_playback_settings (
    scope TEXT PRIMARY KEY NOT NULL,
    mode INTEGER NOT NULL
) STRICT;

CREATE TABLE windows_music_online_settings (
    scope TEXT PRIMARY KEY NOT NULL,
    revision INTEGER NOT NULL DEFAULT 0,
    lyrics INTEGER NOT NULL CHECK (lyrics IN (0, 1))
) STRICT;

CREATE TABLE windows_music_device_settings (
    scope TEXT PRIMARY KEY NOT NULL,
    revision INTEGER NOT NULL DEFAULT 0,
    volume INTEGER NOT NULL
) STRICT;

CREATE TABLE windows_music_appearance (
    scope TEXT PRIMARY KEY NOT NULL,
    revision INTEGER NOT NULL DEFAULT 0,
    playlist_view TEXT NOT NULL,
    player_theme TEXT NOT NULL,
    color_mode TEXT NOT NULL,
    color_palette TEXT NOT NULL
) STRICT;

CREATE TABLE windows_music_catalog_source (
    scope TEXT PRIMARY KEY NOT NULL,
    revision INTEGER NOT NULL DEFAULT 0,
    platform TEXT NOT NULL
) STRICT;

CREATE TABLE windows_music_desktop_lyrics (
    scope TEXT PRIMARY KEY NOT NULL,
    revision INTEGER NOT NULL DEFAULT 0,
    layout TEXT NOT NULL,
    normal_color TEXT NOT NULL,
    highlight_color TEXT NOT NULL,
    geom_monitor TEXT,
    geom_x REAL,
    geom_y REAL,
    geom_width REAL,
    geom_height REAL
) STRICT;

CREATE TABLE windows_music_quality_policy (
    scope TEXT PRIMARY KEY NOT NULL,
    revision INTEGER NOT NULL DEFAULT 0,
    request TEXT NOT NULL
) STRICT;

CREATE TABLE windows_music_quality_list (
    scope TEXT NOT NULL REFERENCES windows_music_quality_policy(scope) ON DELETE CASCADE,
    list_kind TEXT NOT NULL CHECK (list_kind IN ('qualityOrder', 'qualityEnabled', 'spatialOrder', 'spatialEnabled')),
    position INTEGER NOT NULL CHECK (position >= 0),
    item_value TEXT NOT NULL,
    PRIMARY KEY (scope, list_kind, position)
) STRICT;

CREATE TABLE windows_music_official_sources (
    scope TEXT PRIMARY KEY NOT NULL,
    revision INTEGER NOT NULL DEFAULT 0
) STRICT;

CREATE TABLE windows_music_official_source_list (
    scope TEXT NOT NULL REFERENCES windows_music_official_sources(scope) ON DELETE CASCADE,
    list_kind TEXT NOT NULL CHECK (list_kind IN ('platforms', 'enabled')),
    position INTEGER NOT NULL CHECK (position >= 0),
    item_value TEXT NOT NULL,
    PRIMARY KEY (scope, list_kind, position)
) STRICT;

CREATE TABLE windows_music_metadata_policy (
    scope TEXT PRIMARY KEY NOT NULL,
    revision INTEGER NOT NULL DEFAULT 0
) STRICT;

CREATE TABLE windows_music_metadata_policy_list (
    scope TEXT NOT NULL REFERENCES windows_music_metadata_policy(scope) ON DELETE CASCADE,
    list_kind TEXT NOT NULL CHECK (list_kind IN ('platforms', 'enabled')),
    position INTEGER NOT NULL CHECK (position >= 0),
    item_value TEXT NOT NULL,
    PRIMARY KEY (scope, list_kind, position)
) STRICT;

CREATE TABLE windows_music_search_policy (
    scope TEXT PRIMARY KEY NOT NULL,
    revision INTEGER NOT NULL DEFAULT 0,
    songs_pages INTEGER NOT NULL,
    songs_concurrency INTEGER NOT NULL,
    songs_cache_minutes INTEGER NOT NULL,
    playlists_pages INTEGER NOT NULL,
    playlists_concurrency INTEGER NOT NULL,
    playlists_cache_minutes INTEGER NOT NULL
) STRICT;

CREATE TABLE windows_music_search_policy_list (
    scope TEXT NOT NULL REFERENCES windows_music_search_policy(scope) ON DELETE CASCADE,
    target_kind TEXT NOT NULL CHECK (target_kind IN ('songs', 'playlists')),
    list_kind TEXT NOT NULL CHECK (list_kind IN ('order', 'enabled')),
    position INTEGER NOT NULL CHECK (position >= 0),
    item_value TEXT NOT NULL,
    PRIMARY KEY (scope, target_kind, list_kind, position)
) STRICT;

CREATE TABLE windows_music_search_history_state (
    scope TEXT PRIMARY KEY NOT NULL,
    revision INTEGER NOT NULL DEFAULT 0
) STRICT;

CREATE TABLE windows_music_search_history (
    scope TEXT NOT NULL REFERENCES windows_music_search_history_state(scope) ON DELETE CASCADE,
    position INTEGER NOT NULL CHECK (position >= 0),
    query_text TEXT NOT NULL,
    kind TEXT NOT NULL,
    PRIMARY KEY (scope, position)
) STRICT;

CREATE TABLE windows_music_source_exclusions_state (
    scope TEXT PRIMARY KEY NOT NULL,
    revision INTEGER NOT NULL DEFAULT 0
) STRICT;

CREATE TABLE windows_music_source_exclusions (
    scope TEXT NOT NULL REFERENCES windows_music_source_exclusions_state(scope) ON DELETE CASCADE,
    position INTEGER NOT NULL CHECK (position >= 0),
    song_id TEXT NOT NULL REFERENCES music_songs(song_id),
    provider TEXT NOT NULL,
    platform TEXT NOT NULL,
    provider_name TEXT NOT NULL DEFAULT '',
    title TEXT NOT NULL DEFAULT '',
    artist TEXT NOT NULL DEFAULT '',
    excluded_at INTEGER NOT NULL,
    PRIMARY KEY (scope, position)
) STRICT;

CREATE TABLE windows_music_local_lyrics (
    scope TEXT NOT NULL,
    song_id TEXT NOT NULL REFERENCES music_songs(song_id),
    position INTEGER NOT NULL CHECK (position >= 0),
    content_hash TEXT NOT NULL,
    PRIMARY KEY (scope, song_id)
) STRICT;

CREATE TABLE windows_music_local_lyric_lines (
    scope TEXT NOT NULL,
    song_id TEXT NOT NULL,
    line_index INTEGER NOT NULL CHECK (line_index >= 0),
    time_sec REAL NOT NULL,
    text TEXT NOT NULL,
    translation TEXT,
    romanization TEXT,
    end_sec REAL,
    PRIMARY KEY (scope, song_id, line_index),
    FOREIGN KEY (scope, song_id) REFERENCES windows_music_local_lyrics(scope, song_id) ON DELETE CASCADE
) STRICT;

CREATE TABLE windows_music_local_lyric_words (
    scope TEXT NOT NULL,
    song_id TEXT NOT NULL,
    line_index INTEGER NOT NULL,
    word_index INTEGER NOT NULL CHECK (word_index >= 0),
    time_sec REAL NOT NULL,
    text TEXT NOT NULL,
    duration_sec REAL NOT NULL,
    offset_sec REAL,
    PRIMARY KEY (scope, song_id, line_index, word_index),
    FOREIGN KEY (scope, song_id, line_index)
        REFERENCES windows_music_local_lyric_lines(scope, song_id, line_index) ON DELETE CASCADE
) STRICT;
