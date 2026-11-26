-- iOS-specific permanent film player preferences and music accounts, appearance/playback/library extras, similar queue, network references, sync fences, and feedback outbox.
CREATE TABLE ios_film_player_preferences (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    engine TEXT NOT NULL,
    default_rate REAL NOT NULL,
    hold_rate REAL NOT NULL,
    seek_step REAL NOT NULL,
    shows_control_center INTEGER NOT NULL CHECK (shows_control_center IN (0, 1)),
    recognizes_chapters INTEGER NOT NULL CHECK (recognizes_chapters IN (0, 1)),
    single_tap_center TEXT NOT NULL,
    single_tap_sides TEXT NOT NULL,
    double_tap_center TEXT NOT NULL,
    double_tap_sides TEXT NOT NULL,
    swipe_volume INTEGER NOT NULL CHECK (swipe_volume IN (0, 1)),
    swipe_brightness INTEGER NOT NULL CHECK (swipe_brightness IN (0, 1)),
    swipe_seek INTEGER NOT NULL CHECK (swipe_seek IN (0, 1)),
    swipe_episodes INTEGER NOT NULL CHECK (swipe_episodes IN (0, 1)),
    haptic_hold INTEGER NOT NULL CHECK (haptic_hold IN (0, 1)),
    haptic_brightness INTEGER NOT NULL CHECK (haptic_brightness IN (0, 1)),
    haptic_volume INTEGER NOT NULL CHECK (haptic_volume IN (0, 1)),
    haptic_seek INTEGER NOT NULL CHECK (haptic_seek IN (0, 1))
) STRICT;

CREATE TABLE ios_film_favorite_identity (
    favorite_id TEXT PRIMARY KEY NOT NULL REFERENCES film_favorites(favorite_id) ON DELETE CASCADE,
    source_id TEXT NOT NULL,
    kind TEXT
) STRICT;

CREATE TABLE ios_novel_reading_background (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1) REFERENCES novel_reading_settings(singleton) ON DELETE CASCADE,
    background_image_blob BLOB NOT NULL
) STRICT;

CREATE TABLE ios_music_channel_selection (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    channel_key TEXT NOT NULL
) STRICT;

CREATE TABLE ios_music_accounts (
    installation_id TEXT NOT NULL,
    site_key TEXT NOT NULL,
    account_id TEXT NOT NULL,
    user_id TEXT NOT NULL,
    cookie TEXT NOT NULL,
    credential_revision TEXT NOT NULL,
    PRIMARY KEY (installation_id, site_key, account_id)
) STRICT;

CREATE TABLE ios_search_history (
    query_text TEXT PRIMARY KEY NOT NULL,
    used_at_ms INTEGER NOT NULL
) STRICT;

CREATE INDEX idx_ios_search_history_recent ON ios_search_history(used_at_ms DESC, query_text);

CREATE TABLE ios_music_search_policy_extras (
    scope TEXT PRIMARY KEY NOT NULL REFERENCES music_search_policy(scope) ON DELETE CASCADE,
    search_playlists INTEGER NOT NULL DEFAULT 1 CHECK (search_playlists IN (0, 1))
) STRICT;

CREATE TABLE ios_music_feedback_settings (
    scope TEXT NOT NULL REFERENCES music_playback_settings(scope) ON DELETE CASCADE,
    platform TEXT NOT NULL,
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
    generation INTEGER NOT NULL DEFAULT 0 CHECK (generation >= 0),
    position INTEGER NOT NULL DEFAULT 0 CHECK (position >= 0),
    PRIMARY KEY (scope, platform)
) STRICT;

CREATE TABLE ios_music_play_context_extras (
    context_id TEXT PRIMARY KEY NOT NULL REFERENCES music_play_contexts(context_id) ON DELETE CASCADE,
    collection_id TEXT,
    platform TEXT,
    provider TEXT,
    remote_session TEXT,
    similar_batch_id TEXT,
    similar_based_on_song_id TEXT REFERENCES music_songs(song_id),
    similar_provider TEXT
) STRICT;

CREATE INDEX idx_ios_music_play_context_extras_similar_song ON ios_music_play_context_extras(similar_based_on_song_id);

CREATE TABLE ios_music_queue_extras (
    scope TEXT PRIMARY KEY NOT NULL REFERENCES music_queue_state(scope) ON DELETE CASCADE,
    run_id TEXT,
    source_type TEXT,
    source_source_title TEXT,
    source_source_id TEXT,
    source_playlist TEXT,
    source_seed_entry_id TEXT,
    source_remote_session TEXT,
    source_daily_branch TEXT,
    source_search_keyword TEXT
) STRICT;

CREATE TABLE ios_music_history_playlist_extras (
    scope TEXT NOT NULL,
    playlist_id TEXT NOT NULL,
    source_type TEXT,
    source_source_title TEXT,
    source_source_id TEXT,
    source_playlist TEXT,
    source_seed_entry_id TEXT,
    source_remote_session TEXT,
    source_daily_branch TEXT,
    source_search_keyword TEXT,
    context_id TEXT REFERENCES music_play_contexts(context_id),
    PRIMARY KEY (scope, playlist_id),
    FOREIGN KEY (scope, playlist_id) REFERENCES music_history_playlists(scope, playlist_id) ON DELETE CASCADE
) STRICT;

CREATE INDEX idx_ios_music_history_playlist_extras_context ON ios_music_history_playlist_extras(context_id);

CREATE TABLE ios_music_appearance_extras (
    scope TEXT PRIMARY KEY NOT NULL REFERENCES music_appearance(scope) ON DELETE CASCADE,
    lock_lyric INTEGER NOT NULL DEFAULT 0 CHECK (lock_lyric IN (0, 1)),
    lyric_translation INTEGER NOT NULL DEFAULT 1 CHECK (lyric_translation IN (0, 1)),
    lyric_background INTEGER NOT NULL DEFAULT 0 CHECK (lyric_background IN (0, 1))
) STRICT;

CREATE TABLE ios_music_playback_state (
    scope TEXT PRIMARY KEY NOT NULL REFERENCES music_playback_settings(scope) ON DELETE CASCADE,
    show_translation INTEGER NOT NULL DEFAULT 1 CHECK (show_translation IN (0, 1)),
    occurrence_id TEXT,
    position_seconds REAL NOT NULL DEFAULT 0
) STRICT;

CREATE TABLE ios_music_recording_exclusions (
    scope TEXT NOT NULL REFERENCES music_playback_settings(scope) ON DELETE CASCADE,
    exclusion_id TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    song_id TEXT NOT NULL,
    provider TEXT NOT NULL DEFAULT '',
    platform TEXT NOT NULL DEFAULT '',
    provider_name TEXT NOT NULL DEFAULT '',
    title TEXT NOT NULL DEFAULT '',
    artist TEXT NOT NULL DEFAULT '',
    source_key TEXT NOT NULL,
    excluded_at_ms INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (scope, exclusion_id)
) STRICT;

CREATE INDEX idx_ios_music_recording_exclusions_order ON ios_music_recording_exclusions(scope, position);

CREATE TABLE ios_music_fm_selections (
    scope TEXT NOT NULL REFERENCES music_playback_settings(scope) ON DELETE CASCADE,
    fm_scope TEXT NOT NULL,
    provider TEXT NOT NULL DEFAULT '' CHECK (provider IN ('', 'rule')),
    mode TEXT,
    submode TEXT NOT NULL DEFAULT '',
    rule_id TEXT,
    tab_tag_id TEXT,
    PRIMARY KEY (scope, fm_scope)
) STRICT;

CREATE TABLE ios_music_library_status (
    scope TEXT PRIMARY KEY NOT NULL REFERENCES music_library_state(scope) ON DELETE CASCADE,
    status_phase TEXT NOT NULL DEFAULT 'idle',
    status_at_ms INTEGER NOT NULL DEFAULT 0,
    status_code TEXT
) STRICT;

CREATE TABLE ios_music_library_status_issues (
    scope TEXT NOT NULL REFERENCES ios_music_library_status(scope) ON DELETE CASCADE,
    position INTEGER NOT NULL CHECK (position >= 0),
    platform TEXT,
    collection_id TEXT,
    code TEXT NOT NULL,
    PRIMARY KEY (scope, position)
) STRICT;

CREATE TABLE ios_music_queue_similar_state (
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
    provider_pid TEXT,
    provider_sid TEXT,
    provider_count INTEGER,
    has_return_queue INTEGER NOT NULL DEFAULT 0 CHECK (has_return_queue IN (0, 1)),
    return_current_id TEXT,
    return_source_id TEXT,
    return_source_title TEXT,
    return_source_platform TEXT,
    return_source_type TEXT,
    return_source_source_title TEXT,
    return_source_source_id TEXT,
    return_source_playlist TEXT,
    return_source_seed_entry_id TEXT,
    return_source_remote_session TEXT,
    return_source_daily_branch TEXT,
    return_source_search_keyword TEXT
) STRICT;

CREATE INDEX idx_ios_music_queue_similar_seed_song ON ios_music_queue_similar_state(seed_song_id);

CREATE TABLE ios_music_queue_similar_return_entries (
    scope TEXT NOT NULL REFERENCES ios_music_queue_similar_state(scope) ON DELETE CASCADE,
    entry_id TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    song_id TEXT NOT NULL REFERENCES music_songs(song_id),
    context_id TEXT REFERENCES music_play_contexts(context_id),
    PRIMARY KEY (scope, entry_id)
) STRICT;

CREATE INDEX idx_ios_music_queue_similar_return_order ON ios_music_queue_similar_return_entries(scope, position);
CREATE INDEX idx_ios_music_queue_similar_return_song ON ios_music_queue_similar_return_entries(song_id);
CREATE INDEX idx_ios_music_queue_similar_return_context ON ios_music_queue_similar_return_entries(context_id);

CREATE TABLE ios_music_queue_similar_list_items (
    scope TEXT NOT NULL REFERENCES ios_music_queue_similar_state(scope) ON DELETE CASCADE,
    list_kind TEXT NOT NULL CHECK (list_kind IN ('return_priority', 'return_history', 'source_ids', 'attempted')),
    position INTEGER NOT NULL CHECK (position >= 0),
    item_value TEXT NOT NULL,
    PRIMARY KEY (scope, list_kind, position)
) STRICT;

CREATE TABLE ios_music_queue_similar_batches (
    scope TEXT NOT NULL REFERENCES ios_music_queue_similar_state(scope) ON DELETE CASCADE,
    batch_id TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    based_on_song_id TEXT REFERENCES music_songs(song_id),
    based_on_title TEXT,
    provider TEXT,
    PRIMARY KEY (scope, batch_id)
) STRICT;

CREATE INDEX idx_ios_music_queue_similar_batches_song ON ios_music_queue_similar_batches(based_on_song_id);

CREATE TABLE ios_music_queue_similar_batch_entries (
    scope TEXT NOT NULL,
    batch_id TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    entry_id TEXT NOT NULL,
    PRIMARY KEY (scope, batch_id, position),
    FOREIGN KEY (scope, batch_id) REFERENCES ios_music_queue_similar_batches(scope, batch_id) ON DELETE CASCADE
) STRICT;

CREATE TABLE ios_music_network_references (
    scope TEXT NOT NULL REFERENCES music_library_state(scope) ON DELETE CASCADE,
    ref_id TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    platform TEXT NOT NULL,
    remote_id TEXT NOT NULL,
    name TEXT NOT NULL,
    creator TEXT NOT NULL DEFAULT '',
    artwork TEXT NOT NULL DEFAULT '',
    song_count INTEGER NOT NULL DEFAULT 0,
    revision INTEGER NOT NULL DEFAULT 0,
    present INTEGER NOT NULL DEFAULT 1 CHECK (present IN (0, 1)),
    pending INTEGER NOT NULL DEFAULT 0 CHECK (pending IN (0, 1)),
    rel_account_id TEXT,
    rel_baseline INTEGER CHECK (rel_baseline IS NULL OR rel_baseline IN (0, 1)),
    rel_relation_id TEXT,
    rel_locator TEXT,
    PRIMARY KEY (scope, ref_id)
) STRICT;

CREATE INDEX idx_ios_music_network_references_order ON ios_music_network_references(scope, position);

CREATE TABLE ios_music_sync_state (
    scope TEXT PRIMARY KEY NOT NULL REFERENCES music_library_state(scope) ON DELETE CASCADE,
    phase TEXT NOT NULL DEFAULT 'idle',
    auto_revision INTEGER NOT NULL DEFAULT 0,
    auto_intent_revision INTEGER NOT NULL DEFAULT 0,
    auto_settled_intent_revision INTEGER NOT NULL DEFAULT 0,
    auto_order_only TEXT
) STRICT;

CREATE TABLE ios_music_sync_selections (
    scope TEXT NOT NULL REFERENCES ios_music_sync_state(scope) ON DELETE CASCADE,
    section TEXT NOT NULL CHECK (section IN ('manual', 'automatic')),
    position INTEGER NOT NULL CHECK (position >= 0),
    platform TEXT NOT NULL,
    favorites INTEGER NOT NULL CHECK (favorites IN (0, 1)),
    playlists INTEGER NOT NULL CHECK (playlists IN (0, 1)),
    PRIMARY KEY (scope, section, platform)
) STRICT;

CREATE TABLE ios_music_sync_scopes (
    scope TEXT NOT NULL REFERENCES ios_music_sync_state(scope) ON DELETE CASCADE,
    platform TEXT NOT NULL,
    account_scope TEXT NOT NULL,
    PRIMARY KEY (scope, platform)
) STRICT;

CREATE TABLE ios_music_sync_issues (
    scope TEXT NOT NULL REFERENCES ios_music_sync_state(scope) ON DELETE CASCADE,
    position INTEGER NOT NULL CHECK (position >= 0),
    platform TEXT,
    collection_id TEXT,
    code TEXT NOT NULL,
    PRIMARY KEY (scope, position)
) STRICT;

CREATE TABLE ios_music_sync_auto_bindings (
    scope TEXT NOT NULL REFERENCES ios_music_sync_state(scope) ON DELETE CASCADE,
    platform TEXT NOT NULL,
    account_id TEXT NOT NULL,
    binding_scope TEXT NOT NULL,
    PRIMARY KEY (scope, platform)
) STRICT;

CREATE TABLE ios_music_sync_relationship_platforms (
    scope TEXT NOT NULL REFERENCES ios_music_sync_state(scope) ON DELETE CASCADE,
    position INTEGER NOT NULL CHECK (position >= 0),
    platform TEXT NOT NULL,
    PRIMARY KEY (scope, position)
) STRICT;

CREATE TABLE ios_music_sync_fences (
    scope TEXT NOT NULL REFERENCES ios_music_sync_state(scope) ON DELETE CASCADE,
    fence_index INTEGER NOT NULL CHECK (fence_index >= 0),
    platform TEXT NOT NULL,
    account_id TEXT NOT NULL,
    fence_scope TEXT NOT NULL,
    local_id TEXT,
    local_revision INTEGER,
    acknowledged_id TEXT,
    acknowledged_name TEXT,
    mut_id TEXT,
    mut_action TEXT,
    mut_collection_id TEXT,
    mut_name TEXT,
    mut_directory_revision TEXT,
    before_id TEXT,
    before_name TEXT,
    before_kind TEXT,
    before_writable INTEGER CHECK (before_writable IS NULL OR before_writable IN (0, 1)),
    before_can_rename INTEGER CHECK (before_can_rename IS NULL OR before_can_rename IN (0, 1)),
    before_can_delete INTEGER CHECK (before_can_delete IS NULL OR before_can_delete IN (0, 1)),
    before_can_order INTEGER CHECK (before_can_order IS NULL OR before_can_order IN (0, 1)),
    before_revision TEXT,
    before_sub_remote_id TEXT,
    before_sub_relation_id TEXT,
    before_sub_name TEXT,
    before_sub_creator TEXT,
    before_sub_artwork TEXT,
    before_sub_count INTEGER,
    PRIMARY KEY (scope, fence_index)
) STRICT;

CREATE TABLE ios_music_sync_fence_members (
    scope TEXT NOT NULL,
    fence_index INTEGER NOT NULL,
    member_set TEXT NOT NULL CHECK (member_set IN ('mutation', 'before', 'directory')),
    position INTEGER NOT NULL CHECK (position >= 0),
    native_id TEXT NOT NULL,
    occurrence_id TEXT,
    PRIMARY KEY (scope, fence_index, member_set, position),
    FOREIGN KEY (scope, fence_index) REFERENCES ios_music_sync_fences(scope, fence_index) ON DELETE CASCADE
) STRICT;

CREATE TABLE ios_music_feedback_outbox (
    event_id TEXT PRIMARY KEY NOT NULL,
    instance TEXT NOT NULL,
    scope TEXT NOT NULL,
    generation INTEGER NOT NULL CHECK (generation >= 0),
    phase TEXT NOT NULL CHECK (phase IN ('start', 'end')),
    native_id TEXT NOT NULL,
    source TEXT NOT NULL,
    source_id TEXT NOT NULL,
    algorithm TEXT NOT NULL,
    heartbeat INTEGER NOT NULL CHECK (heartbeat IN (0, 1)),
    listened REAL NOT NULL CHECK (listened >= 0),
    media REAL NOT NULL CHECK (media >= 0),
    rate REAL NOT NULL CHECK (rate > 0),
    occurred_at_ms INTEGER NOT NULL CHECK (occurred_at_ms >= 0),
    reason TEXT,
    state TEXT NOT NULL CHECK (state IN ('pending', 'inflight')),
    start_confirmed INTEGER NOT NULL CHECK (start_confirmed IN (0, 1))
) STRICT;

CREATE INDEX idx_ios_music_feedback_dispatch ON ios_music_feedback_outbox(occurred_at_ms, event_id);
