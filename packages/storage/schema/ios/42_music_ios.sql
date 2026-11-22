-- iOS-specific music preferences, accounts, network references, sync fences, discovery cache, home snapshot, and media caches.
CREATE TABLE ios_search_history (
    text TEXT PRIMARY KEY NOT NULL,
    used_at REAL NOT NULL
) STRICT;

CREATE INDEX idx_ios_search_history_recent ON ios_search_history(used_at DESC, text);

CREATE TABLE ios_music_search_preferences (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    search_playlists INTEGER NOT NULL CHECK (search_playlists IN (0, 1)),
    song_pages INTEGER NOT NULL CHECK (song_pages >= 1),
    song_concurrency INTEGER NOT NULL CHECK (song_concurrency >= 1),
    song_cache_minutes INTEGER NOT NULL CHECK (song_cache_minutes BETWEEN 1 AND 1440),
    playlist_pages INTEGER NOT NULL CHECK (playlist_pages >= 1),
    playlist_concurrency INTEGER NOT NULL CHECK (playlist_concurrency >= 1),
    playlist_cache_minutes INTEGER NOT NULL CHECK (playlist_cache_minutes BETWEEN 1 AND 1440),
    row_revision INTEGER NOT NULL CHECK (row_revision BETWEEN 1 AND 9007199254740991)
) STRICT;

CREATE TABLE ios_music_search_platforms (
    kind TEXT NOT NULL CHECK (kind IN ('song', 'playlist')),
    position INTEGER NOT NULL CHECK (position >= 0),
    platform TEXT NOT NULL,
    PRIMARY KEY (kind, position),
    UNIQUE (kind, platform)
) STRICT;

CREATE TABLE ios_music_song_palettes (
    song_key TEXT PRIMARY KEY NOT NULL,
    palette TEXT NOT NULL,
    used_at REAL NOT NULL
) STRICT;

CREATE INDEX idx_ios_music_song_palettes_recent ON ios_music_song_palettes(used_at DESC, song_key);

CREATE TABLE ios_music_palette_state (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    last_palette TEXT NOT NULL
) STRICT;

CREATE TABLE ios_music_channel_selection (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    channel_key TEXT NOT NULL
) STRICT;

CREATE TABLE ios_playback_volume (
    owner TEXT PRIMARY KEY NOT NULL,
    volume REAL NOT NULL CHECK (volume BETWEEN 0 AND 1)
) STRICT;

CREATE TABLE ios_music_accounts (
    installation_id TEXT NOT NULL,
    site_key TEXT NOT NULL,
    account_id TEXT NOT NULL,
    platform TEXT NOT NULL,
    user_id TEXT NOT NULL,
    cookie TEXT NOT NULL,
    credential_revision TEXT NOT NULL,
    row_revision INTEGER NOT NULL CHECK (row_revision BETWEEN 1 AND 9007199254740991),
    PRIMARY KEY (installation_id, site_key, account_id)
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
    member_set TEXT NOT NULL CHECK (member_set IN ('mutation', 'before')),
    position INTEGER NOT NULL CHECK (position >= 0),
    native_id TEXT NOT NULL,
    occurrence_id TEXT,
    PRIMARY KEY (scope, fence_index, member_set, position),
    FOREIGN KEY (scope, fence_index) REFERENCES ios_music_sync_fences(scope, fence_index) ON DELETE CASCADE
) STRICT;

CREATE TABLE ios_music_source_discovery (
    song_id TEXT PRIMARY KEY NOT NULL REFERENCES music_songs(song_id) ON DELETE CASCADE,
    stored_bytes INTEGER NOT NULL CHECK (stored_bytes >= 0)
) STRICT;

CREATE TABLE ios_music_source_discovery_platforms (
    song_id TEXT NOT NULL REFERENCES ios_music_source_discovery(song_id) ON DELETE CASCADE,
    platform TEXT NOT NULL,
    checked_at REAL NOT NULL,
    PRIMARY KEY (song_id, platform)
) STRICT;

CREATE TABLE ios_music_source_discovery_keys (
    song_id TEXT NOT NULL,
    platform TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    source_key TEXT NOT NULL,
    PRIMARY KEY (song_id, platform, source_key),
    FOREIGN KEY (song_id, platform)
        REFERENCES ios_music_source_discovery_platforms(song_id, platform) ON DELETE CASCADE
) STRICT;

CREATE TABLE ios_music_source_discovery_observations (
    song_id TEXT NOT NULL,
    platform TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    request_id TEXT NOT NULL,
    started_at REAL NOT NULL,
    sort_order INTEGER NOT NULL,
    checked_at REAL NOT NULL,
    obs_platform TEXT NOT NULL,
    query_text TEXT NOT NULL,
    context TEXT NOT NULL,
    page INTEGER NOT NULL,
    limit_count INTEGER NOT NULL,
    obs_scope TEXT NOT NULL,
    outcome TEXT NOT NULL,
    failure_code TEXT,
    PRIMARY KEY (song_id, platform, position),
    FOREIGN KEY (song_id, platform)
        REFERENCES ios_music_source_discovery_platforms(song_id, platform) ON DELETE CASCADE
) STRICT;

CREATE TABLE ios_music_home_state (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    source TEXT NOT NULL
) STRICT;

CREATE TABLE ios_music_home_sections (
    section_id TEXT PRIMARY KEY NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    title TEXT NOT NULL DEFAULT '',
    layout TEXT NOT NULL DEFAULT '',
    selected_filter TEXT,
    selected_tab TEXT,
    channel_id TEXT,
    channel_title TEXT,
    channel_kind TEXT,
    channel_page_code TEXT,
    channel_platform TEXT
) STRICT;

CREATE TABLE ios_music_home_section_avatars (
    section_id TEXT NOT NULL REFERENCES ios_music_home_sections(section_id) ON DELETE CASCADE,
    position INTEGER NOT NULL CHECK (position >= 0),
    avatar_url TEXT NOT NULL,
    PRIMARY KEY (section_id, position)
) STRICT;

CREATE TABLE ios_music_home_section_filters (
    section_id TEXT NOT NULL REFERENCES ios_music_home_sections(section_id) ON DELETE CASCADE,
    filter_id TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    title TEXT NOT NULL,
    group_name TEXT,
    PRIMARY KEY (section_id, filter_id)
) STRICT;

CREATE TABLE ios_music_home_section_tabs (
    section_id TEXT NOT NULL REFERENCES ios_music_home_sections(section_id) ON DELETE CASCADE,
    tab_id TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    title TEXT NOT NULL,
    PRIMARY KEY (section_id, tab_id)
) STRICT;

CREATE TABLE ios_music_home_items (
    item_uid TEXT PRIMARY KEY NOT NULL,
    section_id TEXT NOT NULL REFERENCES ios_music_home_sections(section_id) ON DELETE CASCADE,
    tab_id TEXT,
    item_id TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    title TEXT NOT NULL DEFAULT '',
    subtitle TEXT,
    artwork TEXT,
    recommendation_reason TEXT,
    cover_label TEXT,
    requires_account INTEGER CHECK (requires_account IS NULL OR requires_account IN (0, 1)),
    kind TEXT,
    label TEXT,
    context_text TEXT,
    play_count INTEGER,
    presentation TEXT,
    cover_ref_platform TEXT,
    cover_ref_kind TEXT,
    cover_ref_id TEXT,
    ranking_song_artwork TEXT,
    ranking_update_frequency TEXT,
    content_kind TEXT,
    content_record TEXT,
    content_col_platform TEXT,
    content_col_native_id TEXT,
    content_col_kind TEXT,
    content_col_title TEXT,
    content_col_creator TEXT,
    content_col_artwork TEXT,
    content_col_count INTEGER,
    content_col_play_count INTEGER,
    content_chan_id TEXT,
    content_chan_title TEXT,
    content_chan_kind TEXT,
    content_chan_page_code TEXT,
    content_chan_platform TEXT,
    action_type TEXT,
    action_provider TEXT,
    action_mode TEXT,
    action_submode TEXT,
    action_entry TEXT,
    action_rule_id TEXT,
    action_tab_tag_id TEXT,
    action_source_name TEXT,
    action_source_type TEXT,
    action_branch TEXT,
    action_song_id TEXT,
    FOREIGN KEY (section_id, tab_id) REFERENCES ios_music_home_section_tabs(section_id, tab_id) ON DELETE CASCADE
) STRICT;

CREATE INDEX idx_ios_music_home_items_parent ON ios_music_home_items(section_id, tab_id, position);

CREATE TABLE ios_music_home_item_lists (
    item_uid TEXT NOT NULL REFERENCES ios_music_home_items(item_uid) ON DELETE CASCADE,
    list_kind TEXT NOT NULL CHECK (list_kind IN ('previewSongIDs', 'actionSourceIds', 'actionIds')),
    position INTEGER NOT NULL CHECK (position >= 0),
    item_value TEXT NOT NULL,
    PRIMARY KEY (item_uid, list_kind, position)
) STRICT;

CREATE TABLE ios_music_home_item_previews (
    item_uid TEXT NOT NULL REFERENCES ios_music_home_items(item_uid) ON DELETE CASCADE,
    preview_kind TEXT NOT NULL CHECK (preview_kind IN ('displayOnly', 'ranking')),
    position INTEGER NOT NULL CHECK (position >= 0),
    preview_id TEXT,
    title TEXT NOT NULL DEFAULT '',
    artist TEXT NOT NULL DEFAULT '',
    album TEXT,
    artwork TEXT,
    duration REAL,
    rank INTEGER,
    last_rank INTEGER,
    PRIMARY KEY (item_uid, preview_kind, position)
) STRICT;

CREATE TABLE ios_music_audio_urls (
    scope TEXT NOT NULL,
    platform TEXT NOT NULL,
    native_id TEXT NOT NULL,
    quality TEXT NOT NULL,
    url TEXT NOT NULL,
    full INTEGER CHECK (full IS NULL OR full IN (0, 1)),
    actual_quality TEXT,
    bitrate REAL,
    sample_rate REAL,
    duration REAL,
    expires_at REAL,
    license_expires_at REAL,
    saved_at REAL NOT NULL,
    PRIMARY KEY (scope, platform, native_id, quality)
) STRICT;

CREATE INDEX idx_ios_music_audio_urls_age ON ios_music_audio_urls(saved_at);

CREATE TABLE ios_music_chorus (
    scope TEXT NOT NULL,
    platform TEXT NOT NULL,
    native_id TEXT NOT NULL,
    recording_duration REAL NOT NULL CHECK (recording_duration > 0),
    start_sec REAL NOT NULL CHECK (start_sec >= 0),
    end_sec REAL NOT NULL CHECK (end_sec >= start_sec),
    expires_at REAL NOT NULL,
    PRIMARY KEY (scope, platform, native_id, recording_duration)
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
    occurred_at REAL NOT NULL CHECK (occurred_at >= 0),
    reason TEXT,
    state TEXT NOT NULL CHECK (state IN ('pending', 'inflight')),
    start_confirmed INTEGER NOT NULL CHECK (start_confirmed IN (0, 1))
) STRICT;

CREATE INDEX idx_ios_music_feedback_dispatch ON ios_music_feedback_outbox(scope, state, occurred_at);

CREATE TABLE ios_music_images (
    resource TEXT NOT NULL,
    filename TEXT PRIMARY KEY NOT NULL,
    updated_at REAL NOT NULL,
    bytes INTEGER NOT NULL CHECK (bytes > 0),
    ready INTEGER NOT NULL CHECK (ready IN (0, 1))
) STRICT;

CREATE UNIQUE INDEX idx_ios_music_images_ready ON ios_music_images(resource) WHERE ready = 1;
CREATE INDEX idx_ios_music_images_age ON ios_music_images(updated_at);

CREATE TABLE ios_music_audio_files (
    cache_key TEXT PRIMARY KEY NOT NULL CHECK (length(cache_key) = 64),
    file TEXT NOT NULL UNIQUE,
    quality TEXT NOT NULL,
    last_played_at REAL NOT NULL,
    license_expires_at REAL,
    expires_at REAL NOT NULL,
    scope TEXT NOT NULL,
    song_id TEXT NOT NULL,
    source_key TEXT NOT NULL,
    platform TEXT NOT NULL,
    native_id TEXT NOT NULL,
    requested_quality TEXT NOT NULL,
    provider TEXT NOT NULL,
    generation INTEGER NOT NULL CHECK (generation > 0),
    stamp_device INTEGER NOT NULL,
    stamp_inode TEXT NOT NULL,
    stamp_size INTEGER NOT NULL CHECK (stamp_size > 0),
    stamp_seconds INTEGER NOT NULL,
    stamp_nanos INTEGER NOT NULL,
    container TEXT NOT NULL,
    codec TEXT NOT NULL,
    duration REAL NOT NULL CHECK (duration > 0),
    decoded_frames INTEGER NOT NULL CHECK (decoded_frames > 0),
    sample_rate REAL NOT NULL CHECK (sample_rate > 0),
    channels INTEGER NOT NULL CHECK (channels > 0),
    bitrate REAL NOT NULL CHECK (bitrate > 0),
    bit_depth INTEGER,
    lossless INTEGER NOT NULL CHECK (lossless IN (0, 1)),
    complete INTEGER NOT NULL CHECK (complete IN (0, 1)),
    protected INTEGER NOT NULL CHECK (protected IN (0, 1))
) STRICT;

CREATE INDEX idx_ios_music_audio_files_song ON ios_music_audio_files(scope, song_id, last_played_at);
CREATE INDEX idx_ios_music_audio_files_age ON ios_music_audio_files(expires_at);
