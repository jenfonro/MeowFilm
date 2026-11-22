-- Windows-specific music accounts, desktop settings, sync state, playback caches, and normalized home snapshot.
CREATE TABLE windows_music_accounts (
    platform TEXT PRIMARY KEY NOT NULL,
    generation INTEGER NOT NULL CHECK (generation >= 1),
    cookie TEXT,
    scope TEXT,
    validated_at INTEGER,
    profile_platform TEXT,
    profile_user_id TEXT,
    profile_display_name TEXT,
    profile_avatar TEXT,
    CHECK (
        (cookie IS NULL AND scope IS NULL AND validated_at IS NULL AND
         profile_platform IS NULL AND profile_user_id IS NULL AND profile_display_name IS NULL AND profile_avatar IS NULL)
        OR
        (cookie IS NOT NULL AND length(cookie) > 0 AND scope IS NOT NULL AND validated_at IS NOT NULL AND
         profile_platform IS NOT NULL AND profile_user_id IS NOT NULL AND profile_display_name IS NOT NULL)
    )
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

CREATE TABLE windows_music_feedback_enabled (
    platform TEXT PRIMARY KEY NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0)
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

CREATE TABLE windows_music_device_settings (
    scope TEXT PRIMARY KEY NOT NULL,
    revision INTEGER NOT NULL DEFAULT 0,
    volume INTEGER NOT NULL
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

-- Windows rebuildable caches: images, chorus, playback sources, audio index, source match, and normalized home snapshot.
CREATE TABLE windows_images (
    address TEXT PRIMARY KEY NOT NULL,
    file_id TEXT NOT NULL CHECK (length(file_id) = 64),
    bytes INTEGER NOT NULL CHECK (bytes BETWEEN 1 AND 8388608),
    updated_at INTEGER NOT NULL CHECK (updated_at > 0)
) STRICT;

CREATE INDEX idx_windows_images_updated ON windows_images(updated_at);
CREATE INDEX idx_windows_images_file ON windows_images(file_id);

CREATE TABLE windows_music_chorus (
    resource_key TEXT PRIMARY KEY NOT NULL,
    updated_at INTEGER NOT NULL,
    start_sec REAL NOT NULL,
    end_sec REAL NOT NULL
) STRICT;

CREATE INDEX idx_windows_music_chorus_updated ON windows_music_chorus(updated_at);

CREATE TABLE windows_music_playback_sources (
    account_scope TEXT NOT NULL,
    song_id TEXT NOT NULL,
    entry_kind TEXT NOT NULL CHECK (entry_kind IN ('urls', 'pointers')),
    signature TEXT NOT NULL,
    updated_at INTEGER NOT NULL,
    expires_until INTEGER NOT NULL,
    stored_bytes INTEGER NOT NULL CHECK (stored_bytes >= 0),
    info_ok INTEGER CHECK (info_ok IS NULL OR info_ok IN (0, 1)),
    info_kind TEXT,
    info_platform TEXT,
    info_source_key TEXT,
    info_scope TEXT,
    info_generation INTEGER,
    info_quality TEXT,
    info_actual_quality TEXT,
    info_quality_verified INTEGER CHECK (info_quality_verified IS NULL OR info_quality_verified IN (0, 1)),
    info_playback_verified INTEGER CHECK (info_playback_verified IS NULL OR info_playback_verified IN (0, 1)),
    info_quality_policy_signature TEXT,
    info_quality_cache_signature TEXT,
    info_url TEXT,
    info_expires_at INTEGER,
    info_duration REAL,
    info_size INTEGER,
    info_full INTEGER CHECK (info_full IS NULL OR info_full IN (0, 1)),
    info_delivery TEXT,
    info_requested_quality TEXT,
    rec_source_key TEXT,
    rec_platform TEXT,
    rec_info_source TEXT,
    rec_name TEXT,
    rec_singer TEXT,
    rec_album_name TEXT,
    rec_duration REAL,
    rec_img TEXT,
    rec_quality_known INTEGER CHECK (rec_quality_known IS NULL OR rec_quality_known IN (0, 1)),
    PRIMARY KEY (account_scope, song_id, entry_kind)
) STRICT;

CREATE INDEX idx_windows_music_playback_sources_cap ON windows_music_playback_sources(account_scope, entry_kind, updated_at);

CREATE TABLE windows_music_playback_source_headers (
    account_scope TEXT NOT NULL,
    song_id TEXT NOT NULL,
    entry_kind TEXT NOT NULL,
    header_name TEXT NOT NULL,
    header_value TEXT NOT NULL,
    PRIMARY KEY (account_scope, song_id, entry_kind, header_name),
    FOREIGN KEY (account_scope, song_id, entry_kind)
        REFERENCES windows_music_playback_sources(account_scope, song_id, entry_kind) ON DELETE CASCADE
) STRICT;

-- Bounded third-party recording wire metadata for cached playback pointers (when not loaded from music_song_sources).
CREATE TABLE windows_music_playback_source_rec_extras (
    account_scope TEXT NOT NULL,
    song_id TEXT NOT NULL,
    entry_kind TEXT NOT NULL,
    node_id INTEGER NOT NULL CHECK (node_id >= 0),
    parent_id INTEGER,
    prop_key TEXT,
    item_index INTEGER,
    kind TEXT NOT NULL CHECK (kind IN ('null', 'boolean', 'integer', 'real', 'number_text', 'text', 'array', 'object')),
    bool_value INTEGER CHECK (bool_value IN (0, 1)),
    int_value INTEGER,
    real_value REAL,
    text_value TEXT,
    PRIMARY KEY (account_scope, song_id, entry_kind, node_id),
    FOREIGN KEY (account_scope, song_id, entry_kind)
        REFERENCES windows_music_playback_sources(account_scope, song_id, entry_kind) ON DELETE CASCADE
) STRICT;

CREATE TABLE windows_music_audio_files (
    resource_key TEXT PRIMARY KEY NOT NULL,
    file_name TEXT NOT NULL UNIQUE,
    file_size INTEGER NOT NULL CHECK (file_size >= 0),
    sha256 TEXT NOT NULL,
    signature TEXT NOT NULL,
    confirmed_at INTEGER NOT NULL,
    duration REAL,
    info_kind TEXT,
    info_platform TEXT,
    info_song_id TEXT,
    info_source_key TEXT,
    info_scope TEXT,
    info_generation INTEGER,
    info_installation_id TEXT,
    info_provider_name TEXT,
    info_quality TEXT,
    info_requested_quality TEXT,
    info_actual_quality TEXT,
    info_quality_verified INTEGER CHECK (info_quality_verified IS NULL OR info_quality_verified IN (0, 1)),
    info_playback_verified INTEGER CHECK (info_playback_verified IS NULL OR info_playback_verified IN (0, 1)),
    info_quality_policy_signature TEXT,
    info_quality_cache_signature TEXT,
    info_duration REAL,
    info_size INTEGER,
    info_full INTEGER CHECK (info_full IS NULL OR info_full IN (0, 1)),
    info_delivery TEXT,
    info_quality_evidence TEXT,
    info_codec TEXT,
    info_sample_rate INTEGER,
    info_bit_depth INTEGER,
    info_channels INTEGER,
    info_bitrate INTEGER
) STRICT;

CREATE INDEX idx_windows_music_audio_confirmed ON windows_music_audio_files(confirmed_at);
CREATE INDEX idx_windows_music_audio_song ON windows_music_audio_files(info_song_id, info_kind);

CREATE TABLE windows_music_source_matches (
    song_id TEXT PRIMARY KEY NOT NULL,
    position INTEGER NOT NULL DEFAULT 0 CHECK (position >= 0),
    stored_bytes INTEGER NOT NULL CHECK (stored_bytes >= 0)
) STRICT;

CREATE INDEX idx_windows_music_source_matches_order ON windows_music_source_matches(position);

CREATE TABLE windows_music_source_match_platforms (
    song_id TEXT NOT NULL REFERENCES windows_music_source_matches(song_id) ON DELETE CASCADE,
    platform TEXT NOT NULL,
    state TEXT NOT NULL,
    checked_at INTEGER NOT NULL,
    context TEXT,
    query_text TEXT,
    code TEXT,
    matched_at INTEGER,
    PRIMARY KEY (song_id, platform)
) STRICT;

CREATE TABLE windows_music_source_match_excluded (
    song_id TEXT NOT NULL REFERENCES windows_music_source_matches(song_id) ON DELETE CASCADE,
    position INTEGER NOT NULL CHECK (position >= 0),
    excluded_key TEXT NOT NULL,
    PRIMARY KEY (song_id, excluded_key)
) STRICT;

-- Normalized Windows music home display snapshot (zero *_kind/*_value pairs; locators and actions factored out).
CREATE TABLE windows_music_home_state (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    platform TEXT NOT NULL
) STRICT;

CREATE TABLE windows_music_home_pages (
    page_index INTEGER PRIMARY KEY CHECK (page_index >= 0),
    platform TEXT NOT NULL
) STRICT;

CREATE TABLE windows_music_home_locators (
    locator_uid TEXT PRIMARY KEY NOT NULL,
    page_index INTEGER NOT NULL REFERENCES windows_music_home_pages(page_index) ON DELETE CASCADE,
    kind TEXT,
    locator_id TEXT,
    playlist_id TEXT,
    dir_id TEXT,
    list_id TEXT,
    version TEXT,
    total_version TEXT,
    creator_id TEXT,
    creator_list_id TEXT
) STRICT;

CREATE INDEX idx_windows_music_home_locators_page ON windows_music_home_locators(page_index);

CREATE TABLE windows_music_home_actions (
    action_uid TEXT PRIMARY KEY NOT NULL,
    page_index INTEGER NOT NULL REFERENCES windows_music_home_pages(page_index) ON DELETE CASCADE,
    type TEXT,
    action_id TEXT,
    branch TEXT,
    page_code TEXT,
    provider TEXT,
    mode TEXT,
    submode TEXT,
    rule_id TEXT,
    tab_tag_id TEXT,
    source_type TEXT,
    source_name TEXT,
    entry TEXT,
    song_id TEXT,
    channel_id TEXT,
    channel_name TEXT,
    channel_page_code TEXT,
    channel_kind TEXT,
    channel_placement TEXT,
    locator_uid TEXT REFERENCES windows_music_home_locators(locator_uid) ON DELETE SET NULL
) STRICT;

CREATE INDEX idx_windows_music_home_actions_page ON windows_music_home_actions(page_index);
CREATE INDEX idx_windows_music_home_actions_locator ON windows_music_home_actions(locator_uid);

CREATE TABLE windows_music_home_action_items (
    action_uid TEXT NOT NULL REFERENCES windows_music_home_actions(action_uid) ON DELETE CASCADE,
    list_kind TEXT NOT NULL CHECK (list_kind IN ('sourceIds', 'ids')),
    position INTEGER NOT NULL CHECK (position >= 0),
    item_value TEXT NOT NULL,
    PRIMARY KEY (action_uid, list_kind, position)
) STRICT;

CREATE TABLE windows_music_home_sections (
    section_uid TEXT PRIMARY KEY NOT NULL,
    page_index INTEGER NOT NULL REFERENCES windows_music_home_pages(page_index) ON DELETE CASCADE,
    section_index INTEGER NOT NULL CHECK (section_index >= 0),
    section_id TEXT,
    title TEXT,
    type TEXT,
    layout TEXT,
    available INTEGER CHECK (available IS NULL OR available IN (0, 1)),
    error_code TEXT,
    requires_account INTEGER CHECK (requires_account IS NULL OR requires_account IN (0, 1)),
    display INTEGER CHECK (display IS NULL OR display IN (0, 1)),
    selected_tab TEXT,
    selected_tag TEXT,
    action_uid TEXT REFERENCES windows_music_home_actions(action_uid) ON DELETE SET NULL,
    UNIQUE (page_index, section_index)
) STRICT;

CREATE INDEX idx_windows_music_home_sections_action ON windows_music_home_sections(action_uid);

CREATE TABLE windows_music_home_tabs (
    tab_uid TEXT PRIMARY KEY NOT NULL,
    section_uid TEXT NOT NULL REFERENCES windows_music_home_sections(section_uid) ON DELETE CASCADE,
    tab_index INTEGER NOT NULL CHECK (tab_index >= 0),
    tab_id TEXT,
    name TEXT,
    UNIQUE (section_uid, tab_index)
) STRICT;

CREATE TABLE windows_music_home_tags (
    section_uid TEXT NOT NULL REFERENCES windows_music_home_sections(section_uid) ON DELETE CASCADE,
    tag_index INTEGER NOT NULL CHECK (tag_index >= 0),
    tag_id TEXT,
    name TEXT,
    group_name TEXT,
    selected INTEGER CHECK (selected IS NULL OR selected IN (0, 1)),
    PRIMARY KEY (section_uid, tag_index)
) STRICT;

CREATE TABLE windows_music_home_cards (
    card_uid TEXT PRIMARY KEY NOT NULL,
    section_uid TEXT NOT NULL REFERENCES windows_music_home_sections(section_uid) ON DELETE CASCADE,
    tab_uid TEXT REFERENCES windows_music_home_tabs(tab_uid) ON DELETE CASCADE,
    card_index INTEGER NOT NULL CHECK (card_index >= 0),
    song_id TEXT,
    ref_song_id TEXT,
    card_id TEXT,
    card_key TEXT,
    name TEXT,
    title TEXT,
    label TEXT,
    subtitle TEXT,
    context_text TEXT,
    creator TEXT,
    cover TEXT,
    cover_label TEXT,
    kind TEXT,
    page_code TEXT,
    channel_kind TEXT,
    available INTEGER CHECK (available IS NULL OR available IN (0, 1)),
    error_code TEXT,
    requires_account INTEGER CHECK (requires_account IS NULL OR requires_account IN (0, 1)),
    display INTEGER CHECK (display IS NULL OR display IN (0, 1)),
    placement TEXT,
    can_play INTEGER CHECK (can_play IS NULL OR can_play IN (0, 1)),
    rank_text TEXT,
    sales_text TEXT,
    relationship TEXT,
    item_count INTEGER,
    play_count INTEGER,
    presentation TEXT,
    day_of_month INTEGER,
    decoration TEXT,
    update_frequency TEXT,
    cover_ref_kind TEXT,
    cover_ref_id TEXT,
    locator_uid TEXT REFERENCES windows_music_home_locators(locator_uid) ON DELETE SET NULL,
    action_uid TEXT REFERENCES windows_music_home_actions(action_uid) ON DELETE SET NULL,
    playback_action_uid TEXT REFERENCES windows_music_home_actions(action_uid) ON DELETE SET NULL
) STRICT;

CREATE INDEX idx_windows_music_home_cards_parent ON windows_music_home_cards(section_uid, tab_uid, card_index);
CREATE INDEX idx_windows_music_home_cards_tab ON windows_music_home_cards(tab_uid);
CREATE INDEX idx_windows_music_home_cards_locator ON windows_music_home_cards(locator_uid);
CREATE INDEX idx_windows_music_home_cards_action ON windows_music_home_cards(action_uid);
CREATE INDEX idx_windows_music_home_cards_playback ON windows_music_home_cards(playback_action_uid);

CREATE TABLE windows_music_home_card_previews (
    card_uid TEXT NOT NULL REFERENCES windows_music_home_cards(card_uid) ON DELETE CASCADE,
    position INTEGER NOT NULL CHECK (position >= 0),
    title TEXT,
    artist TEXT,
    cover TEXT,
    rank INTEGER,
    last_rank INTEGER,
    song_id TEXT,
    PRIMARY KEY (card_uid, position)
) STRICT;
