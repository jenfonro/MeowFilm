-- Windows-specific permanent music accounts, FM preferences, feedback outbox, network playlists, sync state, policy revisions, desktop lyrics, and user-imported local lyrics.
CREATE TABLE windows_music_accounts (
    platform TEXT PRIMARY KEY NOT NULL,
    generation INTEGER NOT NULL CHECK (generation >= 1),
    cookie TEXT,
    scope TEXT,
    validated_at_ms INTEGER,
    profile_platform TEXT,
    profile_user_id TEXT,
    profile_display_name TEXT,
    profile_avatar TEXT,
    CHECK (
        (cookie IS NULL AND scope IS NULL AND validated_at_ms IS NULL AND
         profile_platform IS NULL AND profile_user_id IS NULL AND profile_display_name IS NULL AND profile_avatar IS NULL)
        OR
        (cookie IS NOT NULL AND length(cookie) > 0 AND scope IS NOT NULL AND validated_at_ms IS NOT NULL AND
         profile_platform IS NOT NULL AND profile_user_id IS NOT NULL AND profile_display_name IS NOT NULL)
    )
) STRICT;

CREATE TABLE windows_music_fm_preferences (
    account_id TEXT PRIMARY KEY NOT NULL,
    mode TEXT NOT NULL,
    submode TEXT,
    rule_id TEXT,
    tab_tag_id TEXT
) STRICT;

CREATE TABLE windows_music_feedback_outbox (
    playback_id TEXT PRIMARY KEY NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    consent_revision INTEGER NOT NULL,
    recording_id TEXT NOT NULL,
    source TEXT,
    source_id TEXT,
    algorithm TEXT,
    start_confirmed INTEGER NOT NULL CHECK (start_confirmed IN (0, 1)),
    play_scene TEXT,
    account_scope TEXT NOT NULL,
    account_generation INTEGER NOT NULL,
    account_user_id TEXT NOT NULL,
    start_event_id TEXT,
    start_at_ms INTEGER,
    start_inflight INTEGER CHECK (start_inflight IS NULL OR start_inflight IN (0, 1)),
    start_position_sec REAL,
    start_realtime_sec REAL,
    start_reason TEXT,
    start_rate REAL,
    end_event_id TEXT,
    end_at_ms INTEGER,
    end_inflight INTEGER CHECK (end_inflight IS NULL OR end_inflight IN (0, 1)),
    end_position_sec REAL,
    end_realtime_sec REAL,
    end_reason TEXT,
    end_rate REAL
) STRICT;

CREATE INDEX idx_windows_music_feedback_order ON windows_music_feedback_outbox(position);

CREATE TABLE windows_music_network_playlists (
    scope TEXT NOT NULL REFERENCES music_library_state(scope) ON DELETE CASCADE,
    playlist_id TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    platform TEXT NOT NULL,
    remote_id TEXT NOT NULL,
    name TEXT NOT NULL,
    cover TEXT NOT NULL DEFAULT '',
    song_count INTEGER NOT NULL DEFAULT 0,
    revision INTEGER NOT NULL DEFAULT 0,
    present INTEGER NOT NULL DEFAULT 1 CHECK (present IN (0, 1)),
    sub_account_id TEXT,
    sub_present INTEGER CHECK (sub_present IS NULL OR sub_present IN (0, 1)),
    sub_list_id TEXT,
    sub_creator_id TEXT,
    sub_creator_list_id TEXT,
    intent_account_id TEXT,
    intent_present INTEGER CHECK (intent_present IS NULL OR intent_present IN (0, 1)),
    PRIMARY KEY (scope, playlist_id)
) STRICT;

CREATE INDEX idx_windows_music_network_playlists_order ON windows_music_network_playlists(scope, position);

CREATE TABLE windows_music_sync_state (
    scope TEXT PRIMARY KEY NOT NULL,
    revision INTEGER NOT NULL DEFAULT 0 CHECK (revision BETWEEN 0 AND 9007199254740991),
    pending_platform TEXT,
    pending_account_id TEXT,
    pending_collection_id TEXT,
    pending_remote_id TEXT,
    pending_operation TEXT,
    pending_before_present INTEGER CHECK (pending_before_present IS NULL OR pending_before_present IN (0, 1)),
    pending_desired_present INTEGER CHECK (pending_desired_present IS NULL OR pending_desired_present IN (0, 1)),
    pending_name TEXT,
    pending_locator_list_id TEXT,
    pending_locator_creator_id TEXT,
    pending_locator_creator_list_id TEXT,
    confirmation_token TEXT,
    confirmation_code TEXT,
    confirmation_count INTEGER,
    approved_token TEXT,
    approved_code TEXT,
    approved_count INTEGER,
    status_phase TEXT,
    status_at_ms INTEGER,
    status_code TEXT
) STRICT;

CREATE TABLE windows_music_sync_platforms (
    scope TEXT NOT NULL REFERENCES windows_music_sync_state(scope) ON DELETE CASCADE,
    platform TEXT NOT NULL,
    favorites INTEGER NOT NULL CHECK (favorites IN (0, 1)),
    playlists INTEGER NOT NULL CHECK (playlists IN (0, 1)),
    account_id TEXT,
    PRIMARY KEY (scope, platform)
) STRICT;

CREATE TABLE windows_music_sync_pending_items (
    scope TEXT NOT NULL REFERENCES windows_music_sync_state(scope) ON DELETE CASCADE,
    list_kind TEXT NOT NULL CHECK (list_kind IN ('before', 'desired', 'ids', 'directory')),
    position INTEGER NOT NULL CHECK (position >= 0),
    item_value TEXT NOT NULL,
    PRIMARY KEY (scope, list_kind, position)
) STRICT;

CREATE TABLE windows_music_sync_issues (
    scope TEXT NOT NULL REFERENCES windows_music_sync_state(scope) ON DELETE CASCADE,
    position INTEGER NOT NULL CHECK (position >= 0),
    platform TEXT,
    collection_id TEXT,
    code TEXT NOT NULL,
    PRIMARY KEY (scope, position)
) STRICT;

CREATE TABLE windows_music_policy_revisions (
    scope TEXT NOT NULL REFERENCES music_playback_settings(scope) ON DELETE CASCADE,
    policy_area TEXT NOT NULL CHECK (policy_area IN ('online', 'quality', 'official', 'metadata', 'exclusions')),
    revision INTEGER NOT NULL DEFAULT 0 CHECK (revision BETWEEN 0 AND 9007199254740991),
    PRIMARY KEY (scope, policy_area)
) STRICT;

CREATE TABLE windows_music_desktop_lyrics (
    scope TEXT PRIMARY KEY NOT NULL,
    revision INTEGER NOT NULL DEFAULT 0 CHECK (revision BETWEEN 0 AND 9007199254740991),
    layout TEXT NOT NULL,
    normal_color TEXT NOT NULL,
    highlight_color TEXT NOT NULL,
    geom_monitor TEXT,
    geom_x REAL,
    geom_y REAL,
    geom_width REAL,
    geom_height REAL
) STRICT;

CREATE TABLE windows_music_song_local_files (
    song_id TEXT PRIMARY KEY NOT NULL REFERENCES music_songs(song_id) ON DELETE CASCADE,
    local_file_id TEXT NOT NULL
) STRICT;

CREATE INDEX idx_windows_music_song_local_file_id ON windows_music_song_local_files(local_file_id);
CREATE INDEX idx_windows_music_songs_cover_url ON music_songs(cover_url, song_id) WHERE cover_url <> '';

CREATE TABLE windows_music_play_context_extras (
    context_id TEXT PRIMARY KEY NOT NULL REFERENCES music_play_contexts(context_id) ON DELETE CASCADE,
    playlist_id TEXT,
    source_platform TEXT,
    heartbeat_playlist_id TEXT,
    heartbeat_account_id TEXT
) STRICT;

CREATE TABLE windows_music_queue_source_kind (
    scope TEXT PRIMARY KEY NOT NULL REFERENCES music_queue_state(scope) ON DELETE CASCADE,
    source_kind TEXT NOT NULL
) STRICT;

CREATE TABLE windows_music_history_playlist_source_kind (
    scope TEXT NOT NULL,
    playlist_id TEXT NOT NULL,
    source_kind TEXT NOT NULL,
    PRIMARY KEY (scope, playlist_id),
    FOREIGN KEY (scope, playlist_id) REFERENCES music_history_playlists(scope, playlist_id) ON DELETE CASCADE
) STRICT;

CREATE TABLE windows_music_search_history (
    scope TEXT NOT NULL,
    kind TEXT NOT NULL,
    query_text TEXT NOT NULL,
    position INTEGER NOT NULL DEFAULT 0 CHECK (position >= 0),
    used_at_ms INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (scope, kind, query_text)
) STRICT;

CREATE INDEX idx_windows_music_search_history_order ON windows_music_search_history(scope, position);

CREATE TABLE windows_music_feedback_platforms (
    platform TEXT PRIMARY KEY NOT NULL CHECK (platform IN ('wy', 'tx', 'kg'))
) STRICT;

CREATE TABLE windows_music_local_lyrics (
    scope TEXT NOT NULL,
    song_id TEXT NOT NULL REFERENCES music_songs(song_id),
    position INTEGER NOT NULL CHECK (position >= 0),
    content_hash TEXT NOT NULL,
    PRIMARY KEY (scope, song_id)
) STRICT;

CREATE INDEX idx_windows_music_local_lyrics_song ON windows_music_local_lyrics(song_id);

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
