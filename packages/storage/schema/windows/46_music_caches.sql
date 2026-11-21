-- Windows rebuildable caches: images, chorus, playback sources, lyrics, audio index, source match, and home snapshot.
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
    rec_songmid_kind TEXT CHECK (rec_songmid_kind IS NULL OR rec_songmid_kind IN ('text', 'number')),
    rec_songmid_value TEXT,
    rec_mid_kind TEXT CHECK (rec_mid_kind IS NULL OR rec_mid_kind IN ('text', 'number')),
    rec_mid_value TEXT,
    rec_id_kind TEXT CHECK (rec_id_kind IS NULL OR rec_id_kind IN ('text', 'number')),
    rec_id_value TEXT,
    rec_song_id_kind TEXT CHECK (rec_song_id_kind IS NULL OR rec_song_id_kind IN ('text', 'number')),
    rec_song_id_value TEXT,
    rec_qq_song_id_kind TEXT CHECK (rec_qq_song_id_kind IS NULL OR rec_qq_song_id_kind IN ('text', 'number')),
    rec_qq_song_id_value TEXT,
    rec_song_type_kind TEXT CHECK (rec_song_type_kind IS NULL OR rec_song_type_kind IN ('text', 'number')),
    rec_song_type_value TEXT,
    rec_type_kind TEXT CHECK (rec_type_kind IS NULL OR rec_type_kind IN ('text', 'number')),
    rec_type_value TEXT,
    rec_copyright_id_kind TEXT CHECK (rec_copyright_id_kind IS NULL OR rec_copyright_id_kind IN ('text', 'number')),
    rec_copyright_id_value TEXT,
    rec_str_media_mid_kind TEXT CHECK (rec_str_media_mid_kind IS NULL OR rec_str_media_mid_kind IN ('text', 'number')),
    rec_str_media_mid_value TEXT,
    rec_hash TEXT,
    rec_album_id_kind TEXT CHECK (rec_album_id_kind IS NULL OR rec_album_id_kind IN ('text', 'number')),
    rec_album_id_value TEXT,
    rec_album_id_snake_kind TEXT CHECK (rec_album_id_snake_kind IS NULL OR rec_album_id_snake_kind IN ('text', 'number')),
    rec_album_id_snake_value TEXT,
    rec_album_mid_kind TEXT CHECK (rec_album_mid_kind IS NULL OR rec_album_mid_kind IN ('text', 'number')),
    rec_album_mid_value TEXT,
    rec_album_audio_id_kind TEXT CHECK (rec_album_audio_id_kind IS NULL OR rec_album_audio_id_kind IN ('text', 'number')),
    rec_album_audio_id_value TEXT,
    rec_mixsongid_kind TEXT CHECK (rec_mixsongid_kind IS NULL OR rec_mixsongid_kind IN ('text', 'number')),
    rec_mixsongid_value TEXT,
    rec_fileid_kind TEXT CHECK (rec_fileid_kind IS NULL OR rec_fileid_kind IN ('text', 'number')),
    rec_fileid_value TEXT,
    rec_file_id_camel_kind TEXT CHECK (rec_file_id_camel_kind IS NULL OR rec_file_id_camel_kind IN ('text', 'number')),
    rec_file_id_camel_value TEXT,
    rec_content_id_kind TEXT CHECK (rec_content_id_kind IS NULL OR rec_content_id_kind IN ('text', 'number')),
    rec_content_id_value TEXT,
    rec_md5 TEXT,
    rec_android_md5 TEXT,
    rec_name TEXT,
    rec_singer TEXT,
    rec_album_name TEXT,
    rec_duration REAL,
    rec_size_kind TEXT CHECK (rec_size_kind IS NULL OR rec_size_kind IN ('text', 'number')),
    rec_size_value TEXT,
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

CREATE TABLE windows_music_playback_source_rec_artists (
    account_scope TEXT NOT NULL,
    song_id TEXT NOT NULL,
    entry_kind TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    name TEXT NOT NULL,
    PRIMARY KEY (account_scope, song_id, entry_kind, position),
    FOREIGN KEY (account_scope, song_id, entry_kind)
        REFERENCES windows_music_playback_sources(account_scope, song_id, entry_kind) ON DELETE CASCADE
) STRICT;

CREATE TABLE windows_music_playback_source_rec_types (
    account_scope TEXT NOT NULL,
    song_id TEXT NOT NULL,
    entry_kind TEXT NOT NULL,
    list_kind TEXT NOT NULL CHECK (list_kind IN ('types', '_types')),
    entry_key TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    type_kind TEXT CHECK (type_kind IS NULL OR type_kind IN ('text', 'number')),
    type_value TEXT,
    hash TEXT,
    size_kind TEXT CHECK (size_kind IS NULL OR size_kind IN ('text', 'number')),
    size_value TEXT,
    PRIMARY KEY (account_scope, song_id, entry_kind, list_kind, entry_key),
    FOREIGN KEY (account_scope, song_id, entry_kind)
        REFERENCES windows_music_playback_sources(account_scope, song_id, entry_kind) ON DELETE CASCADE
) STRICT;

CREATE TABLE windows_music_playback_source_rec_formats (
    account_scope TEXT NOT NULL,
    song_id TEXT NOT NULL,
    entry_kind TEXT NOT NULL,
    list_kind TEXT NOT NULL CHECK (list_kind IN ('audioFormats', 'newRateFormats')),
    position INTEGER NOT NULL CHECK (position >= 0),
    format_type_kind TEXT CHECK (format_type_kind IS NULL OR format_type_kind IN ('text', 'number')),
    format_type_value TEXT,
    resource_type_kind TEXT CHECK (resource_type_kind IS NULL OR resource_type_kind IN ('text', 'number')),
    resource_type_value TEXT,
    size_kind TEXT CHECK (size_kind IS NULL OR size_kind IN ('text', 'number')),
    size_value TEXT,
    android_size_kind TEXT CHECK (android_size_kind IS NULL OR android_size_kind IN ('text', 'number')),
    android_size_value TEXT,
    ios_size_kind TEXT CHECK (ios_size_kind IS NULL OR ios_size_kind IN ('text', 'number')),
    ios_size_value TEXT,
    asize_kind TEXT CHECK (asize_kind IS NULL OR asize_kind IN ('text', 'number')),
    asize_value TEXT,
    isize_kind TEXT CHECK (isize_kind IS NULL OR isize_kind IN ('text', 'number')),
    isize_value TEXT,
    format_kind TEXT CHECK (format_kind IS NULL OR format_kind IN ('text', 'number')),
    format_value TEXT,
    android_format_kind TEXT CHECK (android_format_kind IS NULL OR android_format_kind IN ('text', 'number')),
    android_format_value TEXT,
    ios_format_kind TEXT CHECK (ios_format_kind IS NULL OR ios_format_kind IN ('text', 'number')),
    ios_format_value TEXT,
    aformat_kind TEXT CHECK (aformat_kind IS NULL OR aformat_kind IN ('text', 'number')),
    aformat_value TEXT,
    iformat_kind TEXT CHECK (iformat_kind IS NULL OR iformat_kind IN ('text', 'number')),
    iformat_value TEXT,
    bitrate_kind TEXT CHECK (bitrate_kind IS NULL OR bitrate_kind IN ('text', 'number')),
    bitrate_value TEXT,
    sample_rate_kind TEXT CHECK (sample_rate_kind IS NULL OR sample_rate_kind IN ('text', 'number')),
    sample_rate_value TEXT,
    bit_depth_kind TEXT CHECK (bit_depth_kind IS NULL OR bit_depth_kind IN ('text', 'number')),
    bit_depth_value TEXT,
    file_type_kind TEXT CHECK (file_type_kind IS NULL OR file_type_kind IN ('text', 'number')),
    file_type_value TEXT,
    android_file_type_kind TEXT CHECK (android_file_type_kind IS NULL OR android_file_type_kind IN ('text', 'number')),
    android_file_type_value TEXT,
    ios_file_type_kind TEXT CHECK (ios_file_type_kind IS NULL OR ios_file_type_kind IN ('text', 'number')),
    ios_file_type_value TEXT,
    ios_accuracy_level_kind TEXT CHECK (ios_accuracy_level_kind IS NULL OR ios_accuracy_level_kind IN ('text', 'number')),
    ios_accuracy_level_value TEXT,
    android_accuracy_level_kind TEXT CHECK (android_accuracy_level_kind IS NULL OR android_accuracy_level_kind IN ('text', 'number')),
    android_accuracy_level_value TEXT,
    hash TEXT,
    md5 TEXT,
    android_md5 TEXT,
    file_id_kind TEXT CHECK (file_id_kind IS NULL OR file_id_kind IN ('text', 'number')),
    file_id_value TEXT,
    content_id_kind TEXT CHECK (content_id_kind IS NULL OR content_id_kind IN ('text', 'number')),
    content_id_value TEXT,
    PRIMARY KEY (account_scope, song_id, entry_kind, list_kind, position),
    FOREIGN KEY (account_scope, song_id, entry_kind)
        REFERENCES windows_music_playback_sources(account_scope, song_id, entry_kind) ON DELETE CASCADE
) STRICT;

CREATE TABLE windows_music_lyrics (
    song_id TEXT PRIMARY KEY NOT NULL,
    source_key TEXT NOT NULL,
    updated_at INTEGER NOT NULL,
    stored_bytes INTEGER NOT NULL CHECK (stored_bytes >= 0)
) STRICT;

CREATE INDEX idx_windows_music_lyrics_updated ON windows_music_lyrics(updated_at);

CREATE TABLE windows_music_lyric_lines (
    song_id TEXT NOT NULL REFERENCES windows_music_lyrics(song_id) ON DELETE CASCADE,
    line_index INTEGER NOT NULL CHECK (line_index >= 0),
    time_sec REAL NOT NULL,
    text TEXT NOT NULL,
    translation TEXT,
    romanization TEXT,
    end_sec REAL,
    PRIMARY KEY (song_id, line_index)
) STRICT;

CREATE TABLE windows_music_lyric_words (
    song_id TEXT NOT NULL,
    line_index INTEGER NOT NULL,
    word_index INTEGER NOT NULL CHECK (word_index >= 0),
    time_sec REAL NOT NULL,
    text TEXT NOT NULL,
    duration_sec REAL NOT NULL,
    offset_sec REAL,
    PRIMARY KEY (song_id, line_index, word_index),
    FOREIGN KEY (song_id, line_index) REFERENCES windows_music_lyric_lines(song_id, line_index) ON DELETE CASCADE
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
    stored_bytes INTEGER NOT NULL CHECK (stored_bytes >= 0)
) STRICT;

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

-- Windows music home display snapshot (normalized flat tables, no shape_mask/null_mask).
CREATE TABLE windows_music_home_state (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    platform TEXT NOT NULL
) STRICT;

CREATE TABLE windows_music_home_pages (
    page_index INTEGER PRIMARY KEY CHECK (page_index >= 0),
    platform TEXT NOT NULL
) STRICT;

CREATE TABLE windows_music_home_sections (
    section_uid TEXT PRIMARY KEY NOT NULL,
    page_index INTEGER NOT NULL REFERENCES windows_music_home_pages(page_index) ON DELETE CASCADE,
    section_index INTEGER NOT NULL CHECK (section_index >= 0),
    id_kind TEXT CHECK (id_kind IS NULL OR id_kind IN ('text', 'number')),
    id_value TEXT,
    title TEXT,
    type TEXT,
    layout TEXT,
    available INTEGER CHECK (available IS NULL OR available IN (0, 1)),
    error_code TEXT,
    requires_account INTEGER CHECK (requires_account IS NULL OR requires_account IN (0, 1)),
    display INTEGER CHECK (display IS NULL OR display IN (0, 1)),
    selected_tab TEXT,
    selected_tag TEXT,
    action_type TEXT,
    action_id_kind TEXT CHECK (action_id_kind IS NULL OR action_id_kind IN ('text', 'number')),
    action_id_value TEXT,
    action_branch TEXT,
    action_page_code TEXT,
    action_provider TEXT,
    action_mode TEXT,
    action_submode TEXT,
    action_rule_id TEXT,
    action_tab_tag_id TEXT,
    action_source_type TEXT,
    action_source_name TEXT,
    action_entry TEXT,
    action_song_id TEXT,
    action_channel_id_kind TEXT CHECK (action_channel_id_kind IS NULL OR action_channel_id_kind IN ('text', 'number')),
    action_channel_id_value TEXT,
    action_channel_name TEXT,
    action_channel_page_code TEXT,
    action_channel_kind TEXT,
    action_channel_placement TEXT,
    action_loc_kind TEXT,
    action_loc_id_kind TEXT CHECK (action_loc_id_kind IS NULL OR action_loc_id_kind IN ('text', 'number')),
    action_loc_id_value TEXT,
    action_loc_playlist_id_kind TEXT CHECK (action_loc_playlist_id_kind IS NULL OR action_loc_playlist_id_kind IN ('text', 'number')),
    action_loc_playlist_id_value TEXT,
    action_loc_dir_id_kind TEXT CHECK (action_loc_dir_id_kind IS NULL OR action_loc_dir_id_kind IN ('text', 'number')),
    action_loc_dir_id_value TEXT,
    action_loc_list_id_kind TEXT CHECK (action_loc_list_id_kind IS NULL OR action_loc_list_id_kind IN ('text', 'number')),
    action_loc_list_id_value TEXT,
    action_loc_version_kind TEXT CHECK (action_loc_version_kind IS NULL OR action_loc_version_kind IN ('text', 'number')),
    action_loc_version_value TEXT,
    action_loc_total_version_kind TEXT CHECK (action_loc_total_version_kind IS NULL OR action_loc_total_version_kind IN ('text', 'number')),
    action_loc_total_version_value TEXT,
    action_loc_creator_id_kind TEXT CHECK (action_loc_creator_id_kind IS NULL OR action_loc_creator_id_kind IN ('text', 'number')),
    action_loc_creator_id_value TEXT,
    action_loc_creator_list_id_kind TEXT CHECK (action_loc_creator_list_id_kind IS NULL OR action_loc_creator_list_id_kind IN ('text', 'number')),
    action_loc_creator_list_id_value TEXT,
    UNIQUE (page_index, section_index)
) STRICT;

CREATE TABLE windows_music_home_tabs (
    tab_uid TEXT PRIMARY KEY NOT NULL,
    section_uid TEXT NOT NULL REFERENCES windows_music_home_sections(section_uid) ON DELETE CASCADE,
    tab_index INTEGER NOT NULL CHECK (tab_index >= 0),
    id_kind TEXT CHECK (id_kind IS NULL OR id_kind IN ('text', 'number')),
    id_value TEXT,
    name TEXT,
    UNIQUE (section_uid, tab_index)
) STRICT;

CREATE TABLE windows_music_home_tags (
    section_uid TEXT NOT NULL REFERENCES windows_music_home_sections(section_uid) ON DELETE CASCADE,
    tag_index INTEGER NOT NULL CHECK (tag_index >= 0),
    id_kind TEXT CHECK (id_kind IS NULL OR id_kind IN ('text', 'number')),
    id_value TEXT,
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
    id_kind TEXT CHECK (id_kind IS NULL OR id_kind IN ('text', 'number')),
    id_value TEXT,
    key_kind TEXT CHECK (key_kind IS NULL OR key_kind IN ('text', 'number')),
    key_value TEXT,
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
    cover_ref_id_kind TEXT CHECK (cover_ref_id_kind IS NULL OR cover_ref_id_kind IN ('text', 'number')),
    cover_ref_id_value TEXT,
    loc_kind TEXT,
    loc_id_kind TEXT CHECK (loc_id_kind IS NULL OR loc_id_kind IN ('text', 'number')),
    loc_id_value TEXT,
    loc_playlist_id_kind TEXT CHECK (loc_playlist_id_kind IS NULL OR loc_playlist_id_kind IN ('text', 'number')),
    loc_playlist_id_value TEXT,
    loc_dir_id_kind TEXT CHECK (loc_dir_id_kind IS NULL OR loc_dir_id_kind IN ('text', 'number')),
    loc_dir_id_value TEXT,
    loc_list_id_kind TEXT CHECK (loc_list_id_kind IS NULL OR loc_list_id_kind IN ('text', 'number')),
    loc_list_id_value TEXT,
    loc_version_kind TEXT CHECK (loc_version_kind IS NULL OR loc_version_kind IN ('text', 'number')),
    loc_version_value TEXT,
    loc_total_version_kind TEXT CHECK (loc_total_version_kind IS NULL OR loc_total_version_kind IN ('text', 'number')),
    loc_total_version_value TEXT,
    loc_creator_id_kind TEXT CHECK (loc_creator_id_kind IS NULL OR loc_creator_id_kind IN ('text', 'number')),
    loc_creator_id_value TEXT,
    loc_creator_list_id_kind TEXT CHECK (loc_creator_list_id_kind IS NULL OR loc_creator_list_id_kind IN ('text', 'number')),
    loc_creator_list_id_value TEXT,
    action_type TEXT,
    action_id_kind TEXT CHECK (action_id_kind IS NULL OR action_id_kind IN ('text', 'number')),
    action_id_value TEXT,
    action_branch TEXT,
    action_page_code TEXT,
    action_provider TEXT,
    action_mode TEXT,
    action_submode TEXT,
    action_rule_id TEXT,
    action_tab_tag_id TEXT,
    action_source_type TEXT,
    action_source_name TEXT,
    action_entry TEXT,
    action_song_id TEXT,
    action_channel_id_kind TEXT CHECK (action_channel_id_kind IS NULL OR action_channel_id_kind IN ('text', 'number')),
    action_channel_id_value TEXT,
    action_channel_name TEXT,
    action_channel_page_code TEXT,
    action_channel_kind TEXT,
    action_channel_placement TEXT,
    action_loc_kind TEXT,
    action_loc_id_kind TEXT CHECK (action_loc_id_kind IS NULL OR action_loc_id_kind IN ('text', 'number')),
    action_loc_id_value TEXT,
    action_loc_playlist_id_kind TEXT CHECK (action_loc_playlist_id_kind IS NULL OR action_loc_playlist_id_kind IN ('text', 'number')),
    action_loc_playlist_id_value TEXT,
    action_loc_dir_id_kind TEXT CHECK (action_loc_dir_id_kind IS NULL OR action_loc_dir_id_kind IN ('text', 'number')),
    action_loc_dir_id_value TEXT,
    action_loc_list_id_kind TEXT CHECK (action_loc_list_id_kind IS NULL OR action_loc_list_id_kind IN ('text', 'number')),
    action_loc_list_id_value TEXT,
    action_loc_version_kind TEXT CHECK (action_loc_version_kind IS NULL OR action_loc_version_kind IN ('text', 'number')),
    action_loc_version_value TEXT,
    action_loc_total_version_kind TEXT CHECK (action_loc_total_version_kind IS NULL OR action_loc_total_version_kind IN ('text', 'number')),
    action_loc_total_version_value TEXT,
    action_loc_creator_id_kind TEXT CHECK (action_loc_creator_id_kind IS NULL OR action_loc_creator_id_kind IN ('text', 'number')),
    action_loc_creator_id_value TEXT,
    action_loc_creator_list_id_kind TEXT CHECK (action_loc_creator_list_id_kind IS NULL OR action_loc_creator_list_id_kind IN ('text', 'number')),
    action_loc_creator_list_id_value TEXT,
    pb_type TEXT,
    pb_id_kind TEXT CHECK (pb_id_kind IS NULL OR pb_id_kind IN ('text', 'number')),
    pb_id_value TEXT,
    pb_branch TEXT,
    pb_page_code TEXT,
    pb_provider TEXT,
    pb_mode TEXT,
    pb_submode TEXT,
    pb_rule_id TEXT,
    pb_tab_tag_id TEXT,
    pb_source_type TEXT,
    pb_source_name TEXT,
    pb_entry TEXT,
    pb_song_id TEXT,
    pb_channel_id_kind TEXT CHECK (pb_channel_id_kind IS NULL OR pb_channel_id_kind IN ('text', 'number')),
    pb_channel_id_value TEXT,
    pb_channel_name TEXT,
    pb_channel_page_code TEXT,
    pb_channel_kind TEXT,
    pb_channel_placement TEXT,
    pb_loc_kind TEXT,
    pb_loc_id_kind TEXT CHECK (pb_loc_id_kind IS NULL OR pb_loc_id_kind IN ('text', 'number')),
    pb_loc_id_value TEXT,
    pb_loc_playlist_id_kind TEXT CHECK (pb_loc_playlist_id_kind IS NULL OR pb_loc_playlist_id_kind IN ('text', 'number')),
    pb_loc_playlist_id_value TEXT,
    pb_loc_dir_id_kind TEXT CHECK (pb_loc_dir_id_kind IS NULL OR pb_loc_dir_id_kind IN ('text', 'number')),
    pb_loc_dir_id_value TEXT,
    pb_loc_list_id_kind TEXT CHECK (pb_loc_list_id_kind IS NULL OR pb_loc_list_id_kind IN ('text', 'number')),
    pb_loc_list_id_value TEXT,
    pb_loc_version_kind TEXT CHECK (pb_loc_version_kind IS NULL OR pb_loc_version_kind IN ('text', 'number')),
    pb_loc_version_value TEXT,
    pb_loc_total_version_kind TEXT CHECK (pb_loc_total_version_kind IS NULL OR pb_loc_total_version_kind IN ('text', 'number')),
    pb_loc_total_version_value TEXT,
    pb_loc_creator_id_kind TEXT CHECK (pb_loc_creator_id_kind IS NULL OR pb_loc_creator_id_kind IN ('text', 'number')),
    pb_loc_creator_id_value TEXT,
    pb_loc_creator_list_id_kind TEXT CHECK (pb_loc_creator_list_id_kind IS NULL OR pb_loc_creator_list_id_kind IN ('text', 'number')),
    pb_loc_creator_list_id_value TEXT
) STRICT;

CREATE INDEX idx_windows_music_home_cards_parent ON windows_music_home_cards(section_uid, tab_uid, card_index);

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

CREATE TABLE windows_music_home_section_action_items (
    section_uid TEXT NOT NULL REFERENCES windows_music_home_sections(section_uid) ON DELETE CASCADE,
    list_kind TEXT NOT NULL CHECK (list_kind IN ('sourceIds', 'ids')),
    position INTEGER NOT NULL CHECK (position >= 0),
    item_value TEXT NOT NULL,
    PRIMARY KEY (section_uid, list_kind, position)
) STRICT;

CREATE TABLE windows_music_home_card_action_items (
    card_uid TEXT NOT NULL REFERENCES windows_music_home_cards(card_uid) ON DELETE CASCADE,
    target_kind TEXT NOT NULL CHECK (target_kind IN ('action', 'playback')),
    list_kind TEXT NOT NULL CHECK (list_kind IN ('sourceIds', 'ids')),
    position INTEGER NOT NULL CHECK (position >= 0),
    item_value TEXT NOT NULL,
    PRIMARY KEY (card_uid, target_kind, list_kind, position)
) STRICT;
