-- Film playback library, history, manual matches, blocks, preview targets and groups
CREATE TABLE film_preferences_state (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    ios_auto_prepare INTEGER NOT NULL CHECK (ios_auto_prepare IN (0, 1)),
    row_revision INTEGER NOT NULL CHECK (row_revision >= 1)
) STRICT;

CREATE TABLE film_preference_items (
    list_kind TEXT NOT NULL CHECK (list_kind IN ('ruleOrder', 'panOrder', 'keywords', 'siteCleanKeywords')),
    position INTEGER NOT NULL CHECK (position >= 0),
    item_value TEXT NOT NULL,
    PRIMARY KEY (list_kind, position)
) STRICT;

CREATE TABLE film_history (
    target_id TEXT PRIMARY KEY NOT NULL,
    title TEXT NOT NULL,
    source_key TEXT NOT NULL,
    detail_id_kind TEXT NOT NULL CHECK (detail_id_kind IN ('text', 'integer')),
    detail_id_text TEXT NOT NULL,
    line_id_kind TEXT NOT NULL CHECK (line_id_kind IN ('text', 'integer')),
    line_id_text TEXT NOT NULL,
    candidate_id TEXT NOT NULL,
    season INTEGER CHECK (season IS NULL OR season >= 0),
    episode INTEGER CHECK (episode IS NULL OR episode >= 0),
    global_episode INTEGER CHECK (global_episode IS NULL OR global_episode >= 0),
    has_episode INTEGER NOT NULL CHECK (has_episode IN (0, 1)),
    position_sec REAL NOT NULL CHECK (position_sec >= 0),
    duration_sec REAL NOT NULL CHECK (duration_sec >= 0),
    paused INTEGER NOT NULL CHECK (paused IN (0, 1)),
    ended INTEGER NOT NULL CHECK (ended IN (0, 1)),
    played_at_ms INTEGER,
    updated_at_ms INTEGER NOT NULL,
    target_kind TEXT NOT NULL CHECK (target_kind IN ('movie', 'tv', 'source', 'group')),
    target_title TEXT NOT NULL,
    target_year_kind TEXT CHECK (target_year_kind IS NULL OR target_year_kind IN ('text', 'integer')),
    target_year_text TEXT,
    target_metadata_provider TEXT CHECK (target_metadata_provider IS NULL OR target_metadata_provider IN ('tmdb', 'douban')),
    target_metadata_id_kind TEXT CHECK (target_metadata_id_kind IS NULL OR target_metadata_id_kind IN ('text', 'integer')),
    target_metadata_id_text TEXT,
    target_season INTEGER CHECK (target_season IS NULL OR target_season >= 0),
    target_episode INTEGER CHECK (target_episode IS NULL OR target_episode >= 0),
    target_global_episode INTEGER CHECK (target_global_episode IS NULL OR target_global_episode >= 0),
    target_has_members INTEGER NOT NULL CHECK (target_has_members IN (0, 1)),
    target_source_key TEXT,
    target_source_engine TEXT,
    target_source_installation_id TEXT,
    target_source_site_kind TEXT CHECK (target_source_site_kind IS NULL OR target_source_site_kind IN ('text', 'integer')),
    target_source_site_text TEXT,
    target_source_detail_kind TEXT CHECK (target_source_detail_kind IS NULL OR target_source_detail_kind IN ('text', 'integer')),
    target_source_detail_text TEXT,
    target_source_line_kind TEXT CHECK (target_source_line_kind IS NULL OR target_source_line_kind IN ('text', 'integer')),
    target_source_line_text TEXT,
    target_source_candidate_id TEXT,
    row_revision INTEGER NOT NULL CHECK (row_revision >= 1),
    CHECK ((target_year_kind IS NULL) = (target_year_text IS NULL)),
    CHECK ((target_metadata_id_kind IS NULL) = (target_metadata_id_text IS NULL)),
    CHECK ((target_source_site_kind IS NULL) = (target_source_site_text IS NULL)),
    CHECK ((target_source_detail_kind IS NULL) = (target_source_detail_text IS NULL)),
    CHECK ((target_source_line_kind IS NULL) = (target_source_line_text IS NULL))
) STRICT;

CREATE INDEX idx_film_history_order ON film_history(updated_at_ms DESC,target_id DESC);

CREATE TABLE film_history_target_members (
    target_id TEXT NOT NULL REFERENCES film_history(target_id) ON DELETE CASCADE,
    position INTEGER NOT NULL CHECK (position >= 0),
    source_key TEXT NOT NULL,
    detail_id_kind TEXT NOT NULL CHECK (detail_id_kind IN ('text', 'integer')),
    detail_id_text TEXT NOT NULL,
    PRIMARY KEY (target_id, position)
) STRICT;

CREATE TABLE film_manual_matches (
    match_id TEXT PRIMARY KEY NOT NULL,
    position_order INTEGER NOT NULL CHECK (position_order >= 0),
    target_id TEXT NOT NULL,
    match_kind TEXT,
    title TEXT,
    name TEXT,
    source_key TEXT,
    detail_id_kind TEXT CHECK (detail_id_kind IS NULL OR detail_id_kind IN ('text', 'integer')),
    detail_id_text TEXT,
    provider TEXT,
    share_url TEXT,
    share_password TEXT,
    season_hint INTEGER CHECK (season_hint IS NULL OR season_hint >= 0),
    has_enabled INTEGER NOT NULL CHECK (has_enabled IN (0, 1)),
    enabled INTEGER CHECK (enabled IS NULL OR enabled IN (0, 1)),
    has_auto_disable INTEGER NOT NULL CHECK (has_auto_disable IN (0, 1)),
    auto_disable INTEGER CHECK (auto_disable IS NULL OR auto_disable IN (0, 1)),
    failure_count INTEGER NOT NULL CHECK (failure_count >= 0),
    row_revision INTEGER NOT NULL CHECK (row_revision >= 1)
) STRICT;

CREATE INDEX idx_film_manual_target ON film_manual_matches(target_id, position_order);

CREATE TABLE film_blocked_sources (
    block_id TEXT PRIMARY KEY NOT NULL,
    position_order INTEGER NOT NULL CHECK (position_order >= 0),
    target_id TEXT NOT NULL,
    source_key TEXT NOT NULL,
    detail_id_kind TEXT NOT NULL CHECK (detail_id_kind IN ('text', 'integer')),
    detail_id_text TEXT NOT NULL,
    line_id_kind TEXT NOT NULL CHECK (line_id_kind IN ('text', 'integer')),
    line_id_text TEXT NOT NULL
) STRICT;

CREATE INDEX idx_film_blocked_target ON film_blocked_sources(target_id, position_order);

CREATE TABLE film_preview_targets (
    position_order INTEGER NOT NULL CHECK (position_order >= 0),
    target_id TEXT PRIMARY KEY NOT NULL
) STRICT;

CREATE TABLE film_groups (
    group_id TEXT PRIMARY KEY NOT NULL,
    position_order INTEGER NOT NULL CHECK (position_order >= 0)
) STRICT;

CREATE TABLE film_group_keys (
    group_id TEXT NOT NULL REFERENCES film_groups(group_id) ON DELETE CASCADE,
    position INTEGER NOT NULL CHECK (position >= 0),
    aggregation_key TEXT NOT NULL,
    PRIMARY KEY (group_id, position)
) STRICT;

CREATE INDEX idx_film_group_keys_lookup ON film_group_keys(aggregation_key);

CREATE TABLE film_group_members (
    group_id TEXT NOT NULL REFERENCES film_groups(group_id) ON DELETE CASCADE,
    position INTEGER NOT NULL CHECK (position >= 0),
    member_identity TEXT NOT NULL,
    PRIMARY KEY (group_id, position)
) STRICT;

CREATE INDEX idx_film_group_members_lookup ON film_group_members(member_identity);

-- Standalone film rules
CREATE TABLE film_standalone_rule_groups (
    group_name TEXT PRIMARY KEY NOT NULL CHECK (group_name IN ('episodeCleanRegexRules', 'episodeRules', 'movieRules', 'aggregateRegexRules')),
    row_revision INTEGER NOT NULL DEFAULT 0 CHECK (row_revision BETWEEN 0 AND 9007199254740991),
    overridden INTEGER NOT NULL DEFAULT 0 CHECK (overridden IN (0,1))
) STRICT;
INSERT INTO film_standalone_rule_groups(group_name) VALUES
    ('episodeCleanRegexRules'), ('episodeRules'), ('movieRules'), ('aggregateRegexRules');

CREATE TABLE film_standalone_rule_entries (
    group_name TEXT NOT NULL REFERENCES film_standalone_rule_groups(group_name),
    position INTEGER NOT NULL CHECK (position >= 0),
    pattern TEXT NOT NULL,
    replacement TEXT,
    flags TEXT,
    PRIMARY KEY (group_name, position),
    CHECK (group_name IN ('episodeRules','movieRules') OR (replacement IS NULL AND flags IS NULL))
) STRICT;

-- TMDB / Douban component-level metadata cache
CREATE TABLE metadata_cache_entries (
    cache_key TEXT PRIMARY KEY NOT NULL,
    provider TEXT NOT NULL CHECK (provider IN ('tmdb', 'douban')),
    request_path TEXT NOT NULL,
    entity_kind TEXT NOT NULL CHECK (entity_kind IN ('movie', 'tv', 'season', 'response')),
    settings_revision TEXT NOT NULL,
    image_size TEXT,
    parent_signature TEXT,
    summary_parent_signature TEXT,
    updated_at_ms INTEGER NOT NULL,
    expires_at_ms INTEGER NOT NULL,
    estimated_bytes INTEGER NOT NULL CHECK (estimated_bytes >= 0)
) STRICT;

CREATE INDEX idx_metadata_cache_provider ON metadata_cache_entries(provider, updated_at_ms);
CREATE INDEX idx_metadata_cache_expiry ON metadata_cache_entries(expires_at_ms);

CREATE TABLE metadata_cache_components (
    cache_key TEXT NOT NULL REFERENCES metadata_cache_entries(cache_key) ON DELETE CASCADE,
    component_name TEXT NOT NULL,
    status TEXT NOT NULL CHECK (status IN ('ready', 'empty', 'missing', 'failed')),
    error_code TEXT,
    fetched_at_ms INTEGER,
    expires_at_ms INTEGER,
    PRIMARY KEY (cache_key, component_name)
) STRICT;

-- Normalized provider entities; extensions do not contain the known fields below.
CREATE TABLE metadata_cache_entities (
    cache_key TEXT NOT NULL REFERENCES metadata_cache_entries(cache_key) ON DELETE CASCADE,
    slot_name TEXT NOT NULL CHECK (slot_name IN ('base','summary')),
    entity_id INTEGER NOT NULL CHECK (entity_id>=0),
    entity_kind TEXT NOT NULL CHECK (entity_kind IN ('object','array')),
    id_kind TEXT CHECK (id_kind IN ('null','text','number','number_text')),
    external_id TEXT,
    presence_low INTEGER NOT NULL,
    presence_high INTEGER NOT NULL,
    field_title TEXT,
    field_name TEXT,
    field_original_title TEXT,
    field_original_name TEXT,
    field_overview TEXT,
    field_intro TEXT,
    field_status TEXT,
    field_release_date TEXT,
    field_first_air_date TEXT,
    field_last_air_date TEXT,
    field_air_date TEXT,
    field_year TEXT,
    field_poster_path TEXT,
    field_backdrop_path TEXT,
    field_still_path TEXT,
    field_homepage TEXT,
    field_imdb_id TEXT,
    field_original_language TEXT,
    field_media_type TEXT,
    field_type TEXT,
    field_character TEXT,
    field_job TEXT,
    field_department TEXT,
    field_known_for_department TEXT,
    field_profile_path TEXT,
    field_gender_label TEXT,
    field_iso_639_1 TEXT,
    field_iso_3166_1 TEXT,
    field_english_name TEXT,
    field_file_path TEXT,
    field_key TEXT,
    field_site TEXT,
    field_size TEXT,
    field_aspect TEXT,
    field_uri TEXT,
    field_url TEXT,
    field_subtype TEXT,
    field_card_subtitle TEXT,
    field_description TEXT,
    field_subject_id TEXT,
    field_episode_type TEXT,
    field_production_code TEXT,
    field_credit_id TEXT,
    field_runtime INTEGER,
    field_season_number INTEGER,
    field_episode_number INTEGER,
    field_episode_count INTEGER,
    field_number_of_seasons INTEGER,
    field_number_of_episodes INTEGER,
    field_vote_count INTEGER,
    field_budget INTEGER,
    field_revenue INTEGER,
    field_order INTEGER,
    field_gender INTEGER,
    field_width INTEGER,
    field_height INTEGER,
    field_page INTEGER,
    field_total_pages INTEGER,
    field_total_results INTEGER,
    field_count INTEGER,
    field_max INTEGER,
    field_start INTEGER,
    field_limit INTEGER,
    field_vote_average REAL,
    field_popularity REAL,
    field_aspect_ratio REAL,
    field_value REAL,
    field_average REAL,
    field_rating REAL,
    field_adult INTEGER,
    field_video INTEGER,
    field_in_production INTEGER,
    field_official INTEGER,
    PRIMARY KEY (cache_key,slot_name,entity_id)
) STRICT;

CREATE TABLE metadata_cache_relations (
    cache_key TEXT NOT NULL,
    slot_name TEXT NOT NULL,
    parent_id INTEGER NOT NULL,
    relation_name TEXT NOT NULL,
    relation_kind TEXT NOT NULL CHECK (relation_kind IN ('null','object','array')),
    PRIMARY KEY (cache_key,slot_name,parent_id,relation_name),
    FOREIGN KEY (cache_key,slot_name,parent_id) REFERENCES metadata_cache_entities(cache_key,slot_name,entity_id) ON DELETE CASCADE
) STRICT;

CREATE TABLE metadata_cache_members (
    cache_key TEXT NOT NULL,
    slot_name TEXT NOT NULL,
    parent_id INTEGER NOT NULL,
    relation_name TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position>=0),
    child_id INTEGER,
    value_kind TEXT CHECK (value_kind IN ('null','text','boolean','real','number_text')),
    text_value TEXT,
    int_value INTEGER,
    real_value REAL,
    PRIMARY KEY (cache_key,slot_name,parent_id,relation_name,position),
    FOREIGN KEY (cache_key,slot_name,parent_id,relation_name) REFERENCES metadata_cache_relations(cache_key,slot_name,parent_id,relation_name) ON DELETE CASCADE,
    FOREIGN KEY (cache_key,slot_name,child_id) REFERENCES metadata_cache_entities(cache_key,slot_name,entity_id) ON DELETE CASCADE
) STRICT;

CREATE TABLE metadata_cache_extensions (
    cache_key TEXT NOT NULL,
    slot_name TEXT NOT NULL,
    entity_id INTEGER NOT NULL,
    node_id INTEGER NOT NULL CHECK (node_id>=0),
    parent_id INTEGER,
    prop_key TEXT,
    item_index INTEGER,
    kind TEXT NOT NULL CHECK (kind IN ('null','boolean','integer','real','number_text','text','array','object')),
    bool_value INTEGER CHECK (bool_value IN (0,1)),
    int_value INTEGER,
    real_value REAL,
    text_value TEXT,
    PRIMARY KEY (cache_key,slot_name,entity_id,node_id),
    FOREIGN KEY (cache_key,slot_name,entity_id) REFERENCES metadata_cache_entities(cache_key,slot_name,entity_id) ON DELETE CASCADE
) STRICT;
