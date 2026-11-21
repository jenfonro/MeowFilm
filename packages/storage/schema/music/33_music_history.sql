-- Shared native music playback history (archived queue playlists and daily played songs).
CREATE TABLE music_history_playlists (
    scope TEXT NOT NULL,
    playlist_id TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    played_at REAL NOT NULL,
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
    ctx_collection_id TEXT,
    ctx_playlist_id TEXT,
    ctx_source_title TEXT,
    ctx_playlist TEXT,
    ctx_source_type TEXT,
    ctx_source_id TEXT,
    ctx_source_platform TEXT,
    ctx_platform TEXT,
    ctx_source_label TEXT,
    ctx_search_keyword TEXT,
    ctx_daily_branch TEXT,
    ctx_heartbeat_playlist_id TEXT,
    ctx_heartbeat_account_id TEXT,
    ctx_provider TEXT,
    ctx_remote_session TEXT,
    ctx_queue_source_id TEXT,
    ctx_queue_source_title TEXT,
    ctx_queue_source_kind TEXT,
    ctx_queue_source_platform TEXT,
    ctx_similar_batch_id TEXT,
    ctx_similar_based_on_song_id TEXT REFERENCES music_songs(song_id),
    ctx_similar_provider TEXT,
    PRIMARY KEY (scope, playlist_id)
) STRICT;

CREATE INDEX idx_music_history_playlists_order ON music_history_playlists(scope, position);

CREATE TABLE music_history_playlist_entries (
    scope TEXT NOT NULL,
    playlist_id TEXT NOT NULL,
    entry_id TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    song_id TEXT NOT NULL REFERENCES music_songs(song_id),
    ctx_collection_id TEXT,
    ctx_playlist_id TEXT,
    ctx_source_title TEXT,
    ctx_playlist TEXT,
    ctx_source_type TEXT,
    ctx_source_id TEXT,
    ctx_source_platform TEXT,
    ctx_platform TEXT,
    ctx_source_label TEXT,
    ctx_search_keyword TEXT,
    ctx_daily_branch TEXT,
    ctx_heartbeat_playlist_id TEXT,
    ctx_heartbeat_account_id TEXT,
    ctx_provider TEXT,
    ctx_remote_session TEXT,
    ctx_queue_source_id TEXT,
    ctx_queue_source_title TEXT,
    ctx_queue_source_kind TEXT,
    ctx_queue_source_platform TEXT,
    ctx_similar_batch_id TEXT,
    ctx_similar_based_on_song_id TEXT REFERENCES music_songs(song_id),
    ctx_similar_provider TEXT,
    PRIMARY KEY (scope, playlist_id, entry_id),
    FOREIGN KEY (scope, playlist_id) REFERENCES music_history_playlists(scope, playlist_id) ON DELETE CASCADE
) STRICT;

CREATE INDEX idx_music_history_playlist_entries_order ON music_history_playlist_entries(scope, playlist_id, position);

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
    updated_at REAL NOT NULL,
    PRIMARY KEY (scope, day_date)
) STRICT;

CREATE INDEX idx_music_history_days_order ON music_history_days(scope, position);

CREATE TABLE music_history_day_songs (
    scope TEXT NOT NULL,
    day_date TEXT NOT NULL,
    song_id TEXT NOT NULL REFERENCES music_songs(song_id),
    position INTEGER NOT NULL CHECK (position >= 0),
    played_at REAL NOT NULL,
    ctx_collection_id TEXT,
    ctx_playlist_id TEXT,
    ctx_source_title TEXT,
    ctx_playlist TEXT,
    ctx_source_type TEXT,
    ctx_source_id TEXT,
    ctx_source_platform TEXT,
    ctx_platform TEXT,
    ctx_source_label TEXT,
    ctx_search_keyword TEXT,
    ctx_daily_branch TEXT,
    ctx_heartbeat_playlist_id TEXT,
    ctx_heartbeat_account_id TEXT,
    ctx_provider TEXT,
    ctx_remote_session TEXT,
    ctx_queue_source_id TEXT,
    ctx_queue_source_title TEXT,
    ctx_queue_source_kind TEXT,
    ctx_queue_source_platform TEXT,
    ctx_similar_batch_id TEXT,
    ctx_similar_based_on_song_id TEXT REFERENCES music_songs(song_id),
    ctx_similar_provider TEXT,
    PRIMARY KEY (scope, day_date, song_id),
    FOREIGN KEY (scope, day_date) REFERENCES music_history_days(scope, day_date) ON DELETE CASCADE
) STRICT;

CREATE INDEX idx_music_history_day_songs_order ON music_history_day_songs(scope, day_date, position);
