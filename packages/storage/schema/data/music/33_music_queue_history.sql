-- Shared native music playback queue and playback history (all timestamps UTC INTEGER ms; play contexts normalized via music_play_contexts).
CREATE TABLE music_queue_state (
    scope TEXT PRIMARY KEY NOT NULL,
    current_id TEXT,
    serial INTEGER NOT NULL DEFAULT 0 CHECK (serial >= 0),
    revision INTEGER NOT NULL DEFAULT 0 CHECK (revision BETWEEN 0 AND 9007199254740991),
    run_id TEXT,
    source_id TEXT,
    source_title TEXT,
    source_kind TEXT,
    source_platform TEXT,
    source_type TEXT,
    source_source_title TEXT,
    source_source_id TEXT,
    source_playlist TEXT,
    source_seed_entry_id TEXT,
    source_remote_session TEXT,
    source_daily_branch TEXT,
    source_search_keyword TEXT
) STRICT;

CREATE TABLE music_queue_entries (
    scope TEXT NOT NULL REFERENCES music_queue_state(scope) ON DELETE CASCADE,
    entry_id TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    song_id TEXT NOT NULL REFERENCES music_songs(song_id),
    context_id TEXT REFERENCES music_play_contexts(context_id),
    PRIMARY KEY (scope, entry_id)
) STRICT;

CREATE INDEX idx_music_queue_entries_order ON music_queue_entries(scope, position);
CREATE INDEX idx_music_queue_entries_song ON music_queue_entries(song_id);
CREATE INDEX idx_music_queue_entries_context ON music_queue_entries(context_id);

CREATE TABLE music_queue_list_items (
    scope TEXT NOT NULL REFERENCES music_queue_state(scope) ON DELETE CASCADE,
    list_kind TEXT NOT NULL CHECK (list_kind IN ('priority', 'history', 'failed')),
    position INTEGER NOT NULL CHECK (position >= 0),
    item_value TEXT NOT NULL,
    PRIMARY KEY (scope, list_kind, position)
) STRICT;

CREATE TABLE music_history_playlists (
    scope TEXT NOT NULL,
    playlist_id TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    played_at_ms INTEGER NOT NULL,
    title TEXT NOT NULL DEFAULT '',
    current_id TEXT,
    source_id TEXT,
    source_title TEXT,
    source_kind TEXT,
    source_platform TEXT,
    source_type TEXT,
    source_source_title TEXT,
    source_source_id TEXT,
    source_playlist TEXT,
    source_seed_entry_id TEXT,
    source_remote_session TEXT,
    source_daily_branch TEXT,
    source_search_keyword TEXT,
    context_id TEXT REFERENCES music_play_contexts(context_id),
    PRIMARY KEY (scope, playlist_id)
) STRICT;

CREATE INDEX idx_music_history_playlists_order ON music_history_playlists(scope, position);
CREATE INDEX idx_music_history_playlists_context ON music_history_playlists(context_id);

CREATE TABLE music_history_playlist_entries (
    scope TEXT NOT NULL,
    playlist_id TEXT NOT NULL,
    entry_id TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    song_id TEXT NOT NULL REFERENCES music_songs(song_id),
    context_id TEXT REFERENCES music_play_contexts(context_id),
    PRIMARY KEY (scope, playlist_id, entry_id),
    FOREIGN KEY (scope, playlist_id) REFERENCES music_history_playlists(scope, playlist_id) ON DELETE CASCADE
) STRICT;

CREATE INDEX idx_music_history_playlist_entries_order ON music_history_playlist_entries(scope, playlist_id, position);
CREATE INDEX idx_music_history_playlist_entries_song ON music_history_playlist_entries(song_id);
CREATE INDEX idx_music_history_playlist_entries_context ON music_history_playlist_entries(context_id);

CREATE TABLE music_history_playlist_lists (
    scope TEXT NOT NULL,
    playlist_id TEXT NOT NULL,
    list_kind TEXT NOT NULL CHECK (list_kind IN ('priority', 'history')),
    position INTEGER NOT NULL CHECK (position >= 0),
    item_value TEXT NOT NULL,
    PRIMARY KEY (scope, playlist_id, list_kind, position),
    FOREIGN KEY (scope, playlist_id) REFERENCES music_history_playlists(scope, playlist_id) ON DELETE CASCADE
) STRICT;

CREATE TABLE music_history_days (
    scope TEXT NOT NULL,
    day_date TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    day_id TEXT NOT NULL,
    updated_at_ms INTEGER NOT NULL,
    PRIMARY KEY (scope, day_date)
) STRICT;

CREATE INDEX idx_music_history_days_order ON music_history_days(scope, position);

CREATE TABLE music_history_day_songs (
    scope TEXT NOT NULL,
    day_date TEXT NOT NULL,
    song_id TEXT NOT NULL REFERENCES music_songs(song_id),
    position INTEGER NOT NULL CHECK (position >= 0),
    played_at_ms INTEGER NOT NULL,
    context_id TEXT REFERENCES music_play_contexts(context_id),
    PRIMARY KEY (scope, day_date, song_id),
    FOREIGN KEY (scope, day_date) REFERENCES music_history_days(scope, day_date) ON DELETE CASCADE
) STRICT;

CREATE INDEX idx_music_history_day_songs_order ON music_history_day_songs(scope, day_date, position);
CREATE INDEX idx_music_history_day_songs_song ON music_history_day_songs(song_id);
CREATE INDEX idx_music_history_day_songs_context ON music_history_day_songs(context_id);
