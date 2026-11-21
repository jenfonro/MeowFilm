-- Windows music accounts, profile/rights details, FM preferences, feedback, network playlists, and sync state.
CREATE TABLE windows_music_accounts (
    platform TEXT PRIMARY KEY NOT NULL,
    generation INTEGER NOT NULL CHECK (generation >= 0),
    cookie TEXT NOT NULL,
    scope TEXT NOT NULL,
    validated_at INTEGER NOT NULL,
    profile_platform TEXT,
    profile_user_id TEXT,
    profile_display_name TEXT,
    profile_avatar TEXT
) STRICT;

CREATE TABLE windows_music_account_details (
    platform TEXT PRIMARY KEY NOT NULL,
    account_generation INTEGER NOT NULL CHECK (account_generation >= 0),
    account_scope TEXT NOT NULL,
    account_fingerprint TEXT NOT NULL,
    user_id TEXT NOT NULL,
    profile_fetched_at INTEGER,
    profile_expires_at INTEGER,
    profile_platform TEXT,
    profile_user_id TEXT,
    profile_display_name TEXT,
    profile_avatar TEXT,
    rights_vip INTEGER CHECK (rights_vip IS NULL OR rights_vip IN (0, 1)),
    rights_tier TEXT,
    rights_expires_at INTEGER,
    rights_checked_at INTEGER,
    rights_recheck_at INTEGER,
    rights_verification_deferred INTEGER CHECK (rights_verification_deferred IS NULL OR rights_verification_deferred IN (0, 1)),
    rights_error_code TEXT,
    rights_retry_at INTEGER
) STRICT;

CREATE TABLE windows_music_account_audio_levels (
    platform TEXT NOT NULL REFERENCES windows_music_account_details(platform) ON DELETE CASCADE,
    position INTEGER NOT NULL CHECK (position >= 0),
    level TEXT NOT NULL,
    PRIMARY KEY (platform, position)
) STRICT;

CREATE TABLE windows_music_fm_preferences (
    account_id TEXT PRIMARY KEY NOT NULL,
    mode TEXT NOT NULL,
    submode TEXT,
    rule_id TEXT,
    tab_tag_id TEXT
) STRICT;

CREATE TABLE windows_music_feedback_settings (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    revision INTEGER NOT NULL DEFAULT 0 CHECK (revision >= 0)
) STRICT;

CREATE TABLE windows_music_feedback_enabled (
    platform TEXT PRIMARY KEY NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0)
) STRICT;

CREATE TABLE windows_music_feedback_outbox (
    playback_id TEXT PRIMARY KEY NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    consent_revision INTEGER NOT NULL,
    recording_id TEXT NOT NULL,
    source TEXT NOT NULL,
    source_id TEXT NOT NULL,
    algorithm TEXT NOT NULL DEFAULT '',
    start_confirmed INTEGER NOT NULL CHECK (start_confirmed IN (0, 1)),
    play_scene TEXT NOT NULL DEFAULT '',
    account_scope TEXT NOT NULL,
    account_generation INTEGER NOT NULL,
    account_user_id TEXT NOT NULL,
    start_event_id TEXT,
    start_at INTEGER,
    start_inflight INTEGER CHECK (start_inflight IS NULL OR start_inflight IN (0, 1)),
    start_time REAL,
    start_realtime REAL,
    start_reason TEXT,
    start_rate REAL,
    end_event_id TEXT,
    end_at INTEGER,
    end_inflight INTEGER CHECK (end_inflight IS NULL OR end_inflight IN (0, 1)),
    end_time REAL,
    end_realtime REAL,
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
    status_at REAL,
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
