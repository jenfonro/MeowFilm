-- module: cache/core/00_identity.sql
-- Core database identity and monotonic owner revisions (disposable rebuildable cache database).
CREATE TABLE storage_identity (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    schema_hash TEXT NOT NULL CHECK (length(schema_hash) = 64)
) STRICT;

CREATE TABLE owner_revisions (
    owner TEXT PRIMARY KEY NOT NULL,
    revision INTEGER NOT NULL CHECK (revision >= 1)
) STRICT;

-- module: cache/runtime/10_metadata_cache.sql
-- TMDB and Douban component-level metadata cache (dynamic external upstream responses stored as bounded typed value trees).
CREATE TABLE metadata_cache_entries (
    cache_key TEXT PRIMARY KEY NOT NULL,
    provider TEXT NOT NULL CHECK (provider IN ('tmdb', 'douban')),
    parent_signature TEXT,
    summary_parent_signature TEXT,
    updated_at_ms INTEGER NOT NULL,
    estimated_bytes INTEGER NOT NULL CHECK (estimated_bytes >= 0)
) STRICT;

CREATE INDEX idx_metadata_cache_provider ON metadata_cache_entries(provider);
CREATE INDEX idx_metadata_cache_updated ON metadata_cache_entries(updated_at_ms DESC, cache_key, estimated_bytes);

CREATE TABLE metadata_cache_components (
    cache_key TEXT NOT NULL REFERENCES metadata_cache_entries(cache_key) ON DELETE CASCADE,
    component_name TEXT NOT NULL,
    status TEXT NOT NULL CHECK (status IN ('ready', 'empty', 'missing', 'failed')),
    error_code TEXT,
    fetched_at_ms INTEGER,
    expires_at_ms INTEGER,
    PRIMARY KEY (cache_key, component_name)
) STRICT;

CREATE TABLE metadata_cache_value_nodes (
    cache_key TEXT NOT NULL REFERENCES metadata_cache_entries(cache_key) ON DELETE CASCADE,
    slot_name TEXT NOT NULL CHECK (slot_name IN ('base', 'summary')),
    node_id INTEGER NOT NULL CHECK (node_id >= 0),
    parent_id INTEGER,
    prop_key TEXT,
    item_index INTEGER,
    kind TEXT NOT NULL CHECK (kind IN ('null', 'boolean', 'integer', 'real', 'number_text', 'text', 'array', 'object')),
    bool_value INTEGER CHECK (bool_value IN (0, 1)),
    int_value INTEGER,
    real_value REAL,
    text_value TEXT,
    PRIMARY KEY (cache_key, slot_name, node_id)
) STRICT;

-- module: cache/shared/20_media_cache.sql
-- Shared rebuildable media presentation caches: images, online lyrics, and chorus timestamps (Windows & iOS).
CREATE TABLE cached_images (
    resource_key TEXT NOT NULL,
    filename TEXT NOT NULL,
    bytes INTEGER NOT NULL CHECK (bytes > 0),
    ready INTEGER NOT NULL DEFAULT 1 CHECK (ready IN (0, 1)),
    updated_at_ms INTEGER NOT NULL CHECK (updated_at_ms > 0),
    PRIMARY KEY (resource_key, filename)
) STRICT;

CREATE UNIQUE INDEX idx_cached_images_ready ON cached_images(resource_key) WHERE ready = 1;
CREATE INDEX idx_cached_images_filename ON cached_images(filename);
CREATE INDEX idx_cached_images_updated ON cached_images(ready, updated_at_ms, resource_key);

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

-- module: cache/ios/30_ios_cache.sql
-- iOS-specific rebuildable caches: song palettes, audio URLs, audio file index, source discovery, and music home display snapshot.
CREATE TABLE ios_music_song_palettes (
    song_key TEXT PRIMARY KEY NOT NULL,
    palette TEXT NOT NULL,
    used_at_ms INTEGER NOT NULL
) STRICT;

CREATE INDEX idx_ios_music_song_palettes_recent ON ios_music_song_palettes(used_at_ms DESC, song_key);

CREATE TABLE ios_music_palette_state (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    last_palette TEXT NOT NULL
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
    expires_at_ms INTEGER,
    license_expires_at_ms INTEGER,
    saved_at_ms INTEGER NOT NULL,
    PRIMARY KEY (scope, platform, native_id, quality)
) STRICT;

CREATE INDEX idx_ios_music_audio_urls_age ON ios_music_audio_urls(saved_at_ms);
CREATE INDEX idx_ios_music_audio_urls_expiry ON ios_music_audio_urls(expires_at_ms) WHERE expires_at_ms IS NOT NULL;

CREATE TABLE ios_music_audio_files (
    cache_key TEXT PRIMARY KEY NOT NULL CHECK (length(cache_key) = 64),
    file TEXT NOT NULL UNIQUE,
    quality TEXT NOT NULL,
    last_played_at_ms INTEGER NOT NULL,
    license_expires_at_ms INTEGER,
    expires_at_ms INTEGER NOT NULL,
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
    lossless INTEGER NOT NULL CHECK (lossless IN (0, 1))
) STRICT;

-- Disposable source discovery cache (logical song_id reference; never holds cross-DB FK into permanent data DB).
CREATE TABLE ios_music_source_discovery (
    song_id TEXT PRIMARY KEY NOT NULL,
    stored_bytes INTEGER NOT NULL CHECK (stored_bytes >= 0)
) STRICT;

CREATE TABLE ios_music_source_discovery_platforms (
    song_id TEXT NOT NULL REFERENCES ios_music_source_discovery(song_id) ON DELETE CASCADE,
    platform TEXT NOT NULL,
    checked_at_ms INTEGER NOT NULL,
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
    started_at_ms INTEGER NOT NULL,
    sort_order INTEGER NOT NULL,
    checked_at_ms INTEGER NOT NULL,
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
