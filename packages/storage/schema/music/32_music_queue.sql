-- Shared native music playback queue, priority/history lists, and similar-radio session state.
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
    PRIMARY KEY (scope, entry_id)
) STRICT;

CREATE INDEX idx_music_queue_entries_order ON music_queue_entries(scope, position);

CREATE TABLE music_queue_list_items (
    scope TEXT NOT NULL REFERENCES music_queue_state(scope) ON DELETE CASCADE,
    list_kind TEXT NOT NULL CHECK (list_kind IN ('priority', 'history', 'failed')),
    position INTEGER NOT NULL CHECK (position >= 0),
    item_value TEXT NOT NULL,
    PRIMARY KEY (scope, list_kind, position)
) STRICT;

CREATE TABLE music_queue_similar_state (
    scope TEXT PRIMARY KEY NOT NULL REFERENCES music_queue_state(scope) ON DELETE CASCADE,
    seed_song_id TEXT REFERENCES music_songs(song_id),
    seed_title TEXT,
    seed_entry_id TEXT,
    from_home INTEGER CHECK (from_home IS NULL OR from_home IN (0, 1)),
    provider TEXT,
    continue_enabled INTEGER CHECK (continue_enabled IS NULL OR continue_enabled IN (0, 1)),
    offset_val INTEGER,
    exhausted INTEGER CHECK (exhausted IS NULL OR exhausted IN (0, 1)),
    similar_scope TEXT,
    status TEXT,
    provider_pid_kind TEXT CHECK (provider_pid_kind IS NULL OR provider_pid_kind IN ('text', 'number')),
    provider_pid_value TEXT,
    provider_sid_kind TEXT CHECK (provider_sid_kind IS NULL OR provider_sid_kind IN ('text', 'number')),
    provider_sid_value TEXT,
    provider_count INTEGER,
    has_return_queue INTEGER NOT NULL DEFAULT 0 CHECK (has_return_queue IN (0, 1)),
    return_current_id TEXT,
    return_source_id TEXT,
    return_source_title TEXT,
    return_source_kind TEXT,
    return_source_platform TEXT,
    return_source_type TEXT,
    return_source_source_title TEXT,
    return_source_source_id TEXT,
    return_source_playlist TEXT,
    return_source_seed_entry_id TEXT,
    return_source_remote_session TEXT,
    return_source_daily_branch TEXT,
    return_source_search_keyword TEXT,
    CHECK ((provider_pid_kind IS NULL) = (provider_pid_value IS NULL)),
    CHECK ((provider_sid_kind IS NULL) = (provider_sid_value IS NULL))
) STRICT;

CREATE TABLE music_queue_similar_return_entries (
    scope TEXT NOT NULL REFERENCES music_queue_similar_state(scope) ON DELETE CASCADE,
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
    PRIMARY KEY (scope, entry_id)
) STRICT;

CREATE INDEX idx_music_queue_similar_return_order ON music_queue_similar_return_entries(scope, position);

CREATE TABLE music_queue_similar_list_items (
    scope TEXT NOT NULL REFERENCES music_queue_similar_state(scope) ON DELETE CASCADE,
    list_kind TEXT NOT NULL CHECK (list_kind IN ('return_priority', 'return_history', 'source_ids', 'attempted')),
    position INTEGER NOT NULL CHECK (position >= 0),
    item_value TEXT NOT NULL,
    PRIMARY KEY (scope, list_kind, position)
) STRICT;

CREATE TABLE music_queue_similar_batches (
    scope TEXT NOT NULL REFERENCES music_queue_similar_state(scope) ON DELETE CASCADE,
    batch_id TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    based_on_song_id TEXT REFERENCES music_songs(song_id),
    based_on_title TEXT,
    provider TEXT,
    PRIMARY KEY (scope, batch_id)
) STRICT;

CREATE TABLE music_queue_similar_batch_entries (
    scope TEXT NOT NULL,
    batch_id TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    entry_id TEXT NOT NULL,
    PRIMARY KEY (scope, batch_id, position),
    FOREIGN KEY (scope, batch_id) REFERENCES music_queue_similar_batches(scope, batch_id) ON DELETE CASCADE
) STRICT;
