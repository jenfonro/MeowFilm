-- module: core/00_identity.sql
-- Core database identity, monotonic owner revisions, and idempotency receipts.
CREATE TABLE storage_identity (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    schema_hash TEXT NOT NULL CHECK (length(schema_hash) = 64)
) STRICT;

CREATE TABLE owner_revisions (
    owner TEXT PRIMARY KEY NOT NULL,
    revision INTEGER NOT NULL CHECK (revision >= 1),
    receipt TEXT NOT NULL
) STRICT;

CREATE TABLE operation_receipts (
    owner TEXT NOT NULL REFERENCES owner_revisions(owner) ON DELETE CASCADE,
    operation_id TEXT NOT NULL,
    revision INTEGER NOT NULL CHECK (revision >= 1),
    PRIMARY KEY (owner, operation_id)
) STRICT;

-- module: runtime/10_music_scripts.sql
-- Music script catalog, declared capabilities, check outcomes, and per-key LX guest KV.
CREATE TABLE music_script_catalog_state (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    revision INTEGER NOT NULL CHECK (revision >= 0)
) STRICT;

CREATE TABLE music_script_installations (
    installation_id TEXT PRIMARY KEY NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    name TEXT NOT NULL,
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
    origin_kind TEXT NOT NULL CHECK (origin_kind IN ('file', 'network')),
    origin_url TEXT,
    current_hash TEXT NOT NULL,
    current_bytes INTEGER NOT NULL CHECK (current_bytes > 0),
    current_version TEXT NOT NULL,
    current_author TEXT NOT NULL,
    current_description TEXT NOT NULL,
    current_homepage TEXT NOT NULL,
    previous_hash TEXT,
    previous_bytes INTEGER CHECK (previous_bytes IS NULL OR previous_bytes > 0),
    previous_version TEXT,
    previous_author TEXT,
    previous_description TEXT,
    previous_homepage TEXT,
    declared_hash TEXT,
    check_hash TEXT,
    checked_at_ms INTEGER,
    check_failure TEXT
) STRICT;

CREATE INDEX idx_music_script_installations_position ON music_script_installations(position);

CREATE TABLE music_script_declared_sites (
    installation_id TEXT NOT NULL REFERENCES music_script_installations(installation_id) ON DELETE CASCADE,
    site_id TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    has_qualities INTEGER NOT NULL CHECK (has_qualities IN (0, 1)),
    PRIMARY KEY (installation_id, site_id)
) STRICT;

CREATE TABLE music_script_declared_actions (
    installation_id TEXT NOT NULL,
    site_id TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    action TEXT NOT NULL,
    PRIMARY KEY (installation_id, site_id, position),
    FOREIGN KEY (installation_id, site_id)
        REFERENCES music_script_declared_sites(installation_id, site_id) ON DELETE CASCADE
) STRICT;

CREATE TABLE music_script_declared_qualities (
    installation_id TEXT NOT NULL,
    site_id TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    quality TEXT NOT NULL,
    PRIMARY KEY (installation_id, site_id, position),
    FOREIGN KEY (installation_id, site_id)
        REFERENCES music_script_declared_sites(installation_id, site_id) ON DELETE CASCADE
) STRICT;

CREATE TABLE music_script_kv_entries (
    installation_id TEXT NOT NULL,
    site_id TEXT NOT NULL,
    entry_key TEXT NOT NULL,
    PRIMARY KEY (installation_id, site_id, entry_key)
) STRICT;

-- Third-party script declared extra capability fields (bounded typed value tree).
CREATE TABLE music_script_declared_nodes (
    installation_id TEXT NOT NULL,
    site_id TEXT NOT NULL,
    node_id INTEGER NOT NULL CHECK (node_id >= 0),
    parent_id INTEGER,
    prop_key TEXT,
    item_index INTEGER,
    kind TEXT NOT NULL CHECK (kind IN ('null', 'boolean', 'integer', 'real', 'number_text', 'text', 'array', 'object')),
    bool_value INTEGER CHECK (bool_value IN (0, 1)),
    int_value INTEGER,
    real_value REAL,
    text_value TEXT,
    PRIMARY KEY (installation_id, site_id, node_id),
    FOREIGN KEY (installation_id, site_id)
        REFERENCES music_script_declared_sites(installation_id, site_id) ON DELETE CASCADE
) STRICT;

-- Third-party LX guest script per-key dynamic KV values (bounded typed value tree).
CREATE TABLE music_script_kv_nodes (
    installation_id TEXT NOT NULL,
    site_id TEXT NOT NULL,
    entry_key TEXT NOT NULL,
    node_id INTEGER NOT NULL CHECK (node_id >= 0),
    parent_id INTEGER,
    prop_key TEXT,
    item_index INTEGER,
    kind TEXT NOT NULL CHECK (kind IN ('null', 'boolean', 'integer', 'real', 'number_text', 'text', 'array', 'object')),
    bool_value INTEGER CHECK (bool_value IN (0, 1)),
    int_value INTEGER,
    real_value REAL,
    text_value TEXT,
    PRIMARY KEY (installation_id, site_id, entry_key, node_id),
    FOREIGN KEY (installation_id, site_id, entry_key)
        REFERENCES music_script_kv_entries(installation_id, site_id, entry_key) ON DELETE CASCADE
) STRICT;

CREATE TABLE music_script_checks (
    installation_id TEXT NOT NULL REFERENCES music_script_installations(installation_id) ON DELETE CASCADE,
    site_id TEXT NOT NULL,
    state TEXT NOT NULL CHECK (state IN ('ok', 'failed', 'unsupported')),
    quality TEXT,
    sample TEXT,
    code TEXT,
    PRIMARY KEY (installation_id, site_id)
) STRICT;

-- module: runtime/11_source_runtime.sql
-- Source runtime installations, structured definitions, private cookies, and per-key KV.
CREATE TABLE source_installations (
    installation_id TEXT PRIMARY KEY NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    revision INTEGER NOT NULL CHECK (revision >= 1),
    name TEXT NOT NULL,
    source_url TEXT NOT NULL UNIQUE,
    source_group TEXT NOT NULL,
    sort_order REAL NOT NULL,
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
    enabled_explore INTEGER NOT NULL CHECK (enabled_explore IN (0, 1)),
    protocol TEXT NOT NULL,
    definition_kind TEXT NOT NULL CHECK (definition_kind IN ('object', 'html')),
    raw_html TEXT
) STRICT;

CREATE INDEX idx_source_installations_position ON source_installations(position);

CREATE TABLE source_installation_actions (
    installation_id TEXT NOT NULL REFERENCES source_installations(installation_id) ON DELETE CASCADE,
    position INTEGER NOT NULL CHECK (position >= 0),
    action TEXT NOT NULL,
    PRIMARY KEY (installation_id, position)
) STRICT;

CREATE TABLE source_definitions (
    installation_id TEXT PRIMARY KEY NOT NULL REFERENCES source_installations(installation_id) ON DELETE CASCADE,
    bookSourceName TEXT, bookSourceUrl TEXT, bookSourceGroup TEXT,
    bookSourceComment TEXT, bookUrlPattern TEXT, searchUrl TEXT, exploreUrl TEXT,
    loginUrl TEXT, loginCheckJs TEXT, jsLib TEXT, coverDecodeJs TEXT,
    concurrentRate TEXT, header TEXT, html TEXT,
    bookSourceType INTEGER, weight REAL, respondTime REAL, lastUpdateTime REAL,
    enabledCookieJar INTEGER CHECK (enabledCookieJar IN (0, 1))
) STRICT;

CREATE TABLE source_definition_rules (
    installation_id TEXT NOT NULL REFERENCES source_installations(installation_id) ON DELETE CASCADE,
    rule_group TEXT NOT NULL CHECK (rule_group IN ('ruleSearch', 'ruleExplore', 'ruleBookInfo', 'ruleToc', 'ruleContent')),
    encoded INTEGER NOT NULL CHECK (encoded IN (0, 1)),
    bookList TEXT, name TEXT, author TEXT, kind TEXT, wordCount TEXT, lastChapter TEXT,
    intro TEXT, coverUrl TEXT, bookUrl TEXT, tocUrl TEXT, init TEXT,
    chapterList TEXT, chapterName TEXT, chapterUrl TEXT, nextTocUrl TEXT,
    isReverse TEXT, reverseToc TEXT, updateTime TEXT, preUpdateJs TEXT,
    content TEXT, nextContentUrl TEXT, title TEXT, replaceRegex TEXT,
    sourceRegex TEXT, webJs TEXT, payAction TEXT, imageStyle TEXT,
    imageDecode TEXT, imageDecrypt TEXT,
    PRIMARY KEY (installation_id, rule_group)
) STRICT;

-- Third-party source definition unknown fields and variant recipe values (bounded typed value tree).
CREATE TABLE source_definition_nodes (
    installation_id TEXT NOT NULL REFERENCES source_installations(installation_id) ON DELETE CASCADE,
    field TEXT NOT NULL,
    node_id INTEGER NOT NULL CHECK (node_id >= 0),
    parent_id INTEGER,
    prop_key TEXT,
    item_index INTEGER,
    kind TEXT NOT NULL CHECK (kind IN ('null', 'boolean', 'integer', 'real', 'number_text', 'text', 'array', 'object')),
    bool_value INTEGER CHECK (bool_value IN (0, 1)),
    int_value INTEGER,
    real_value REAL,
    text_value TEXT,
    PRIMARY KEY (installation_id, field, node_id)
) STRICT;

CREATE TABLE source_private_profiles (
    installation_id TEXT NOT NULL,
    profile_suffix TEXT NOT NULL,
    PRIMARY KEY (installation_id, profile_suffix)
) STRICT;

CREATE TABLE source_private_cookies (
    installation_id TEXT NOT NULL,
    profile_suffix TEXT NOT NULL,
    domain TEXT NOT NULL,
    path TEXT NOT NULL,
    name TEXT NOT NULL,
    value TEXT NOT NULL,
    host_only INTEGER NOT NULL CHECK (host_only IN (0, 1)),
    secure INTEGER NOT NULL CHECK (secure IN (0, 1)),
    http_only INTEGER NOT NULL CHECK (http_only IN (0, 1)),
    same_site TEXT,
    expires_at_ms INTEGER,
    sequence INTEGER NOT NULL CHECK (sequence >= 0),
    PRIMARY KEY (installation_id, profile_suffix, domain, path, name),
    FOREIGN KEY (installation_id, profile_suffix)
        REFERENCES source_private_profiles(installation_id, profile_suffix) ON DELETE CASCADE
) STRICT;

CREATE INDEX idx_source_private_cookies_installation ON source_private_cookies(installation_id);

CREATE TABLE source_private_kv_entries (
    installation_id TEXT NOT NULL,
    profile_suffix TEXT NOT NULL,
    entry_key TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    PRIMARY KEY (installation_id, profile_suffix, entry_key),
    FOREIGN KEY (installation_id, profile_suffix)
        REFERENCES source_private_profiles(installation_id, profile_suffix) ON DELETE CASCADE
) STRICT;

CREATE INDEX idx_source_private_kv_installation ON source_private_kv_entries(installation_id);

-- Reader synchronous source.get/put scope (separate from asynchronous cache KV).
CREATE TABLE source_reader_state (
    installation_id TEXT PRIMARY KEY NOT NULL,
    revision INTEGER NOT NULL DEFAULT 0 CHECK (revision BETWEEN 0 AND 9007199254740991),
    login_header TEXT,
    login_info TEXT,
    variable TEXT
) STRICT;

CREATE TABLE source_reader_value_nodes (
    installation_id TEXT NOT NULL REFERENCES source_reader_state(installation_id) ON DELETE CASCADE,
    scope_name TEXT NOT NULL CHECK (scope_name IN ('values', 'books')),
    entry_key TEXT NOT NULL,
    node_id INTEGER NOT NULL CHECK (node_id >= 0),
    parent_id INTEGER,
    prop_key TEXT,
    item_index INTEGER,
    kind TEXT NOT NULL CHECK (kind IN ('null', 'boolean', 'integer', 'real', 'number_text', 'text', 'array', 'object')),
    bool_value INTEGER CHECK (bool_value IN (0, 1)),
    int_value INTEGER,
    real_value REAL,
    text_value TEXT,
    PRIMARY KEY (installation_id, scope_name, entry_key, node_id)
) STRICT;

CREATE TABLE source_private_kv_nodes (
    installation_id TEXT NOT NULL,
    profile_suffix TEXT NOT NULL,
    entry_key TEXT NOT NULL,
    node_id INTEGER NOT NULL CHECK (node_id >= 0),
    parent_id INTEGER,
    prop_key TEXT,
    item_index INTEGER,
    kind TEXT NOT NULL CHECK (kind IN ('null', 'boolean', 'integer', 'real', 'number_text', 'text', 'array', 'object')),
    bool_value INTEGER CHECK (bool_value IN (0, 1)),
    int_value INTEGER,
    real_value REAL,
    text_value TEXT,
    PRIMARY KEY (installation_id, profile_suffix, entry_key, node_id),
    FOREIGN KEY (installation_id, profile_suffix, entry_key)
        REFERENCES source_private_kv_entries(installation_id, profile_suffix, entry_key) ON DELETE CASCADE
) STRICT;

-- module: runtime/12_cat_runtime.sql
-- CatNode standalone gateway configuration and shared script installation state.
CREATE TABLE cat_gateway_settings (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    port INTEGER NOT NULL CHECK (port BETWEEN 0 AND 65535)
) STRICT;

CREATE TABLE cat_provider_entries (
    provider_id TEXT PRIMARY KEY NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1))
) STRICT;

CREATE TABLE cat_mappings (
    position INTEGER PRIMARY KEY CHECK (position >= 0),
    provider_id TEXT NOT NULL
) STRICT;

CREATE TABLE cat_mapping_aliases (
    position INTEGER NOT NULL REFERENCES cat_mappings(position) ON DELETE CASCADE,
    ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
    alias TEXT NOT NULL,
    PRIMARY KEY (position, ordinal)
) STRICT;

CREATE TABLE cat_installations (
    mode TEXT NOT NULL CHECK (mode IN ('standalone', 'native')),
    installation_id TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    name TEXT,
    source_type TEXT CHECK (source_type IS NULL OR source_type IN ('network', 'local')),
    source_url TEXT,
    local_path TEXT,
    local_hash TEXT,
    enabled INTEGER CHECK (enabled IS NULL OR enabled IN (0, 1)),
    input_revision INTEGER,
    source_hash TEXT,
    version TEXT NOT NULL,
    loaded_identity TEXT,
    imported_at_ms INTEGER,
    updated_at_ms INTEGER,
    update_state TEXT NOT NULL,
    update_error TEXT,
    PRIMARY KEY (mode, installation_id)
) STRICT;

CREATE INDEX idx_cat_installations_position ON cat_installations(mode, position);

CREATE TABLE cat_installation_config_nodes (
    mode TEXT NOT NULL,
    installation_id TEXT NOT NULL,
    node_id INTEGER NOT NULL CHECK (node_id >= 0),
    parent_id INTEGER,
    prop_key TEXT,
    item_index INTEGER,
    kind TEXT NOT NULL CHECK (kind IN ('null', 'boolean', 'integer', 'real', 'number_text', 'text', 'array', 'object')),
    bool_value INTEGER CHECK (bool_value IN (0, 1)),
    int_value INTEGER,
    real_value REAL,
    text_value TEXT,
    PRIMARY KEY (mode, installation_id, node_id),
    FOREIGN KEY (mode, installation_id)
        REFERENCES cat_installations(mode, installation_id) ON DELETE CASCADE
) STRICT;

CREATE TABLE cat_installation_sites (
    mode TEXT NOT NULL,
    installation_id TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    site_key TEXT NOT NULL,
    site_name TEXT NOT NULL,
    site_api TEXT NOT NULL,
    discovery TEXT NOT NULL,
    PRIMARY KEY (mode, installation_id, site_key),
    UNIQUE (mode, installation_id, position),
    FOREIGN KEY (mode, installation_id)
        REFERENCES cat_installations(mode, installation_id) ON DELETE CASCADE
) STRICT;

CREATE TABLE cat_site_actions (
    mode TEXT NOT NULL,
    installation_id TEXT NOT NULL,
    site_key TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    action TEXT NOT NULL,
    PRIMARY KEY (mode, installation_id, site_key, position),
    FOREIGN KEY (mode, installation_id, site_key)
        REFERENCES cat_installation_sites(mode, installation_id, site_key) ON DELETE CASCADE
) STRICT;

CREATE TABLE cat_site_methods (
    mode TEXT NOT NULL,
    installation_id TEXT NOT NULL,
    site_key TEXT NOT NULL,
    method_key TEXT NOT NULL,
    method_name TEXT NOT NULL,
    PRIMARY KEY (mode, installation_id, site_key, method_key),
    FOREIGN KEY (mode, installation_id, site_key)
        REFERENCES cat_installation_sites(mode, installation_id, site_key) ON DELETE CASCADE
) STRICT;

-- module: runtime/13_pan_standalone.sql
-- Standalone Pan accounts (plaintext typed credentials).
CREATE TABLE pan_standalone_accounts (
    provider TEXT PRIMARY KEY NOT NULL CHECK (provider IN ('baidu', 'quark', 'uc', 'tianyi', 'yidong')),
    revision INTEGER NOT NULL CHECK (revision BETWEEN 0 AND 9007199254740991),
    row_revision INTEGER NOT NULL CHECK (row_revision BETWEEN 0 AND 9007199254740991),
    status TEXT,
    cookie TEXT,
    username TEXT,
    password TEXT,
    authorization TEXT,
    refreshed_at_ms INTEGER CHECK (refreshed_at_ms IS NULL OR refreshed_at_ms >= 0)
) STRICT;

INSERT INTO pan_standalone_accounts(provider, revision, row_revision) VALUES
    ('baidu', 0, 0), ('quark', 0, 0), ('uc', 0, 0), ('tianyi', 0, 0), ('yidong', 0, 0);

-- module: runtime/14_metadata_standalone.sql
-- Standalone TMDB and Douban settings (plaintext typed credentials).
CREATE TABLE metadata_standalone_tmdb (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    api_base TEXT NOT NULL,
    image_base TEXT NOT NULL,
    api_key TEXT NOT NULL,
    access_token TEXT NOT NULL,
    language TEXT NOT NULL,
    region TEXT NOT NULL,
    include_adult INTEGER NOT NULL CHECK (include_adult IN (0, 1)),
    row_revision INTEGER NOT NULL CHECK (row_revision BETWEEN 1 AND 9007199254740991)
) STRICT;

CREATE TABLE metadata_standalone_douban (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    api_base TEXT NOT NULL,
    search_base TEXT NOT NULL,
    image_base TEXT NOT NULL,
    api_proxy TEXT NOT NULL,
    image_proxy TEXT NOT NULL,
    cookie TEXT NOT NULL,
    row_revision INTEGER NOT NULL CHECK (row_revision >= 1)
) STRICT;

-- module: runtime/15_music_anonymous.sql
-- Music platform anonymous client protocol state (wy/kg/tx).
CREATE TABLE music_anonymous_state (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    row_revision INTEGER NOT NULL CHECK (row_revision BETWEEN 1 AND 9007199254740991)
) STRICT;

CREATE TABLE music_anonymous_wy_cookies (
    cookie_name TEXT PRIMARY KEY NOT NULL,
    cookie_value TEXT NOT NULL,
    singleton INTEGER NOT NULL DEFAULT 1 CHECK (singleton = 1) REFERENCES music_anonymous_state(singleton) ON DELETE CASCADE
) STRICT;

CREATE TABLE music_anonymous_kg (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1) REFERENCES music_anonymous_state(singleton) ON DELETE CASCADE,
    seed TEXT NOT NULL,
    mid TEXT NOT NULL,
    dfid TEXT
) STRICT;

CREATE TABLE music_anonymous_tx (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1) REFERENCES music_anonymous_state(singleton) ON DELETE CASCADE,
    installation_id TEXT NOT NULL,
    android_id TEXT NOT NULL,
    open_udid TEXT NOT NULL,
    open_udid2 TEXT NOT NULL,
    imei TEXT NOT NULL,
    qimei_q36 TEXT,
    qimei_saved_at_ms INTEGER,
    session_uid TEXT,
    session_sid TEXT,
    session_saved_at_ms INTEGER,
    CHECK ((qimei_q36 IS NULL AND qimei_saved_at_ms IS NULL) OR
      (qimei_q36 IS NOT NULL AND qimei_saved_at_ms IS NOT NULL AND qimei_saved_at_ms BETWEEN 0 AND 9007199254740991)),
    CHECK ((session_uid IS NULL AND session_sid IS NULL AND session_saved_at_ms IS NULL) OR
      (session_uid IS NOT NULL AND session_sid IS NOT NULL AND session_saved_at_ms IS NOT NULL AND session_saved_at_ms BETWEEN 0 AND 9007199254740991))
) STRICT;

-- module: runtime/20_film_playback.sql
-- Film playback library: preferences, history, manual matches, blocked sources, preview targets, groups, and standalone rules.
CREATE TABLE film_preferences_state (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    ios_auto_prepare INTEGER NOT NULL CHECK (ios_auto_prepare IN (0, 1)),
    metadata_view_provider TEXT NOT NULL CHECK (metadata_view_provider IN ('tmdb', 'douban')),
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

CREATE INDEX idx_film_history_order ON film_history(updated_at_ms DESC, target_id DESC);

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
CREATE INDEX idx_film_manual_order ON film_manual_matches(position_order);

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

CREATE INDEX idx_film_preview_targets_order ON film_preview_targets(position_order);

CREATE TABLE film_groups (
    group_id TEXT PRIMARY KEY NOT NULL,
    position_order INTEGER NOT NULL CHECK (position_order >= 0)
) STRICT;

CREATE INDEX idx_film_groups_order ON film_groups(position_order);

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
    overridden INTEGER NOT NULL DEFAULT 0 CHECK (overridden IN (0, 1))
) STRICT;

INSERT INTO film_standalone_rule_groups(group_name) VALUES
    ('episodeCleanRegexRules'), ('episodeRules'), ('movieRules'), ('aggregateRegexRules');

CREATE TABLE film_standalone_rule_entries (
    group_name TEXT NOT NULL REFERENCES film_standalone_rule_groups(group_name) ON DELETE CASCADE,
    position INTEGER NOT NULL CHECK (position >= 0),
    pattern TEXT NOT NULL,
    replacement TEXT,
    flags TEXT,
    PRIMARY KEY (group_name, position),
    CHECK (group_name IN ('episodeRules', 'movieRules') OR (replacement IS NULL AND flags IS NULL))
) STRICT;

-- module: runtime/21_metadata_cache.sql
-- TMDB and Douban component-level metadata cache.
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

-- Normalized provider entities; unknown upstream fields use metadata_cache_extensions.
CREATE TABLE metadata_cache_entities (
    cache_key TEXT NOT NULL REFERENCES metadata_cache_entries(cache_key) ON DELETE CASCADE,
    slot_name TEXT NOT NULL CHECK (slot_name IN ('base', 'summary')),
    entity_id INTEGER NOT NULL CHECK (entity_id >= 0),
    entity_kind TEXT NOT NULL CHECK (entity_kind IN ('object', 'array')),
    id_kind TEXT CHECK (id_kind IN ('null', 'text', 'number', 'number_text')),
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
    field_adult INTEGER CHECK (field_adult IS NULL OR field_adult IN (0, 1)),
    field_video INTEGER CHECK (field_video IS NULL OR field_video IN (0, 1)),
    field_in_production INTEGER CHECK (field_in_production IS NULL OR field_in_production IN (0, 1)),
    field_official INTEGER CHECK (field_official IS NULL OR field_official IN (0, 1)),
    PRIMARY KEY (cache_key, slot_name, entity_id)
) STRICT;

CREATE TABLE metadata_cache_relations (
    cache_key TEXT NOT NULL,
    slot_name TEXT NOT NULL,
    parent_id INTEGER NOT NULL,
    relation_name TEXT NOT NULL,
    relation_kind TEXT NOT NULL CHECK (relation_kind IN ('null', 'object', 'array')),
    PRIMARY KEY (cache_key, slot_name, parent_id, relation_name),
    FOREIGN KEY (cache_key, slot_name, parent_id)
        REFERENCES metadata_cache_entities(cache_key, slot_name, entity_id) ON DELETE CASCADE
) STRICT;

CREATE TABLE metadata_cache_members (
    cache_key TEXT NOT NULL,
    slot_name TEXT NOT NULL,
    parent_id INTEGER NOT NULL,
    relation_name TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    child_id INTEGER,
    value_kind TEXT CHECK (value_kind IN ('null', 'text', 'boolean', 'real', 'number_text')),
    text_value TEXT,
    int_value INTEGER,
    real_value REAL,
    PRIMARY KEY (cache_key, slot_name, parent_id, relation_name, position),
    FOREIGN KEY (cache_key, slot_name, parent_id, relation_name)
        REFERENCES metadata_cache_relations(cache_key, slot_name, parent_id, relation_name) ON DELETE CASCADE,
    FOREIGN KEY (cache_key, slot_name, child_id)
        REFERENCES metadata_cache_entities(cache_key, slot_name, entity_id) ON DELETE CASCADE
) STRICT;

CREATE TABLE metadata_cache_extensions (
    cache_key TEXT NOT NULL,
    slot_name TEXT NOT NULL,
    entity_id INTEGER NOT NULL,
    node_id INTEGER NOT NULL CHECK (node_id >= 0),
    parent_id INTEGER,
    prop_key TEXT,
    item_index INTEGER,
    kind TEXT NOT NULL CHECK (kind IN ('null', 'boolean', 'integer', 'real', 'number_text', 'text', 'array', 'object')),
    bool_value INTEGER CHECK (bool_value IN (0, 1)),
    int_value INTEGER,
    real_value REAL,
    text_value TEXT,
    PRIMARY KEY (cache_key, slot_name, entity_id, node_id),
    FOREIGN KEY (cache_key, slot_name, entity_id)
        REFERENCES metadata_cache_entities(cache_key, slot_name, entity_id) ON DELETE CASCADE
) STRICT;

-- module: music/30_music_songs.sql
-- Shared native music owner state, permanent song identities, and platform source facts.
CREATE TABLE music_owner_state (
    scope TEXT PRIMARY KEY NOT NULL,
    revision INTEGER NOT NULL CHECK (revision BETWEEN 0 AND 9007199254740991),
    receipt TEXT NOT NULL
) STRICT;

CREATE TABLE music_songs (
    song_id TEXT PRIMARY KEY NOT NULL,
    title TEXT NOT NULL DEFAULT '',
    artist TEXT NOT NULL DEFAULT '',
    album TEXT NOT NULL DEFAULT '',
    duration REAL CHECK (duration IS NULL OR duration > 0),
    baseline_duration REAL CHECK (baseline_duration IS NULL OR baseline_duration > 0),
    cover_url TEXT NOT NULL DEFAULT '',
    local_file_id TEXT,
    artwork_source_key TEXT,
    artwork_source_image TEXT,
    lookup_key TEXT,
    search_query TEXT,
    fingerprint TEXT
) STRICT;

CREATE INDEX idx_music_songs_lookup ON music_songs(lookup_key);
CREATE INDEX idx_music_songs_fingerprint ON music_songs(fingerprint);
CREATE INDEX idx_music_songs_search ON music_songs(search_query);

CREATE TABLE music_song_artists (
    song_id TEXT NOT NULL REFERENCES music_songs(song_id) ON DELETE CASCADE,
    position INTEGER NOT NULL CHECK (position >= 0),
    name TEXT NOT NULL,
    PRIMARY KEY (song_id, position)
) STRICT;

-- Each external recording source belongs uniquely to one permanent song_id.
CREATE TABLE music_song_sources (
    song_id TEXT NOT NULL REFERENCES music_songs(song_id) ON DELETE CASCADE,
    source_key TEXT NOT NULL UNIQUE,
    position INTEGER NOT NULL CHECK (position >= 0),
    platform TEXT NOT NULL,
    info_source TEXT NOT NULL,
    songmid_kind TEXT CHECK (songmid_kind IS NULL OR songmid_kind IN ('text', 'number')),
    songmid_value TEXT,
    mid_kind TEXT CHECK (mid_kind IS NULL OR mid_kind IN ('text', 'number')),
    mid_value TEXT,
    id_kind TEXT CHECK (id_kind IS NULL OR id_kind IN ('text', 'number')),
    id_value TEXT,
    song_id_kind TEXT CHECK (song_id_kind IS NULL OR song_id_kind IN ('text', 'number')),
    song_id_value TEXT,
    qq_song_id_kind TEXT CHECK (qq_song_id_kind IS NULL OR qq_song_id_kind IN ('text', 'number')),
    qq_song_id_value TEXT,
    song_type_kind TEXT CHECK (song_type_kind IS NULL OR song_type_kind IN ('text', 'number')),
    song_type_value TEXT,
    type_kind TEXT CHECK (type_kind IS NULL OR type_kind IN ('text', 'number')),
    type_value TEXT,
    copyright_id_kind TEXT CHECK (copyright_id_kind IS NULL OR copyright_id_kind IN ('text', 'number')),
    copyright_id_value TEXT,
    str_media_mid_kind TEXT CHECK (str_media_mid_kind IS NULL OR str_media_mid_kind IN ('text', 'number')),
    str_media_mid_value TEXT,
    hash TEXT,
    album_id_kind TEXT CHECK (album_id_kind IS NULL OR album_id_kind IN ('text', 'number')),
    album_id_value TEXT,
    album_id_snake_kind TEXT CHECK (album_id_snake_kind IS NULL OR album_id_snake_kind IN ('text', 'number')),
    album_id_snake_value TEXT,
    album_mid_kind TEXT CHECK (album_mid_kind IS NULL OR album_mid_kind IN ('text', 'number')),
    album_mid_value TEXT,
    album_audio_id_kind TEXT CHECK (album_audio_id_kind IS NULL OR album_audio_id_kind IN ('text', 'number')),
    album_audio_id_value TEXT,
    mixsongid_kind TEXT CHECK (mixsongid_kind IS NULL OR mixsongid_kind IN ('text', 'number')),
    mixsongid_value TEXT,
    fileid_kind TEXT CHECK (fileid_kind IS NULL OR fileid_kind IN ('text', 'number')),
    fileid_value TEXT,
    file_id_camel_kind TEXT CHECK (file_id_camel_kind IS NULL OR file_id_camel_kind IN ('text', 'number')),
    file_id_camel_value TEXT,
    content_id_kind TEXT CHECK (content_id_kind IS NULL OR content_id_kind IN ('text', 'number')),
    content_id_value TEXT,
    md5 TEXT,
    android_md5 TEXT,
    name TEXT,
    singer TEXT,
    album_name TEXT,
    duration REAL CHECK (duration IS NULL OR duration > 0),
    size_kind TEXT CHECK (size_kind IS NULL OR size_kind IN ('text', 'number')),
    size_value TEXT,
    img TEXT,
    quality_known INTEGER CHECK (quality_known IS NULL OR quality_known IN (0, 1)),
    PRIMARY KEY (song_id, source_key),
    CHECK ((songmid_kind IS NULL) = (songmid_value IS NULL)),
    CHECK ((mid_kind IS NULL) = (mid_value IS NULL)),
    CHECK ((id_kind IS NULL) = (id_value IS NULL)),
    CHECK ((song_id_kind IS NULL) = (song_id_value IS NULL)),
    CHECK ((qq_song_id_kind IS NULL) = (qq_song_id_value IS NULL)),
    CHECK ((song_type_kind IS NULL) = (song_type_value IS NULL)),
    CHECK ((type_kind IS NULL) = (type_value IS NULL)),
    CHECK ((copyright_id_kind IS NULL) = (copyright_id_value IS NULL)),
    CHECK ((str_media_mid_kind IS NULL) = (str_media_mid_value IS NULL)),
    CHECK ((album_id_kind IS NULL) = (album_id_value IS NULL)),
    CHECK ((album_id_snake_kind IS NULL) = (album_id_snake_value IS NULL)),
    CHECK ((album_mid_kind IS NULL) = (album_mid_value IS NULL)),
    CHECK ((album_audio_id_kind IS NULL) = (album_audio_id_value IS NULL)),
    CHECK ((mixsongid_kind IS NULL) = (mixsongid_value IS NULL)),
    CHECK ((fileid_kind IS NULL) = (fileid_value IS NULL)),
    CHECK ((file_id_camel_kind IS NULL) = (file_id_camel_value IS NULL)),
    CHECK ((content_id_kind IS NULL) = (content_id_value IS NULL)),
    CHECK ((size_kind IS NULL) = (size_value IS NULL))
) STRICT;

CREATE INDEX idx_music_song_sources_order ON music_song_sources(song_id, position);

CREATE TABLE music_song_source_artists (
    song_id TEXT NOT NULL,
    source_key TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    name TEXT NOT NULL,
    PRIMARY KEY (song_id, source_key, position),
    FOREIGN KEY (song_id, source_key) REFERENCES music_song_sources(song_id, source_key) ON DELETE CASCADE
) STRICT;

CREATE TABLE music_song_source_types (
    song_id TEXT NOT NULL,
    source_key TEXT NOT NULL,
    list_kind TEXT NOT NULL CHECK (list_kind IN ('types', '_types')),
    entry_key TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    type_kind TEXT CHECK (type_kind IS NULL OR type_kind IN ('text', 'number')),
    type_value TEXT,
    hash TEXT,
    size_kind TEXT CHECK (size_kind IS NULL OR size_kind IN ('text', 'number')),
    size_value TEXT,
    PRIMARY KEY (song_id, source_key, list_kind, entry_key),
    CHECK ((type_kind IS NULL) = (type_value IS NULL)),
    CHECK ((size_kind IS NULL) = (size_value IS NULL)),
    FOREIGN KEY (song_id, source_key) REFERENCES music_song_sources(song_id, source_key) ON DELETE CASCADE
) STRICT;

CREATE TABLE music_song_source_formats (
    song_id TEXT NOT NULL,
    source_key TEXT NOT NULL,
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
    PRIMARY KEY (song_id, source_key, list_kind, position),
    FOREIGN KEY (song_id, source_key) REFERENCES music_song_sources(song_id, source_key) ON DELETE CASCADE
) STRICT;

-- Bounded third-party source metadata extras for unknown upstream fields only.
CREATE TABLE music_song_source_extras (
    song_id TEXT NOT NULL,
    source_key TEXT NOT NULL,
    node_id INTEGER NOT NULL CHECK (node_id >= 0),
    parent_id INTEGER,
    prop_key TEXT,
    item_index INTEGER,
    kind TEXT NOT NULL CHECK (kind IN ('null', 'boolean', 'integer', 'real', 'number_text', 'text', 'array', 'object')),
    bool_value INTEGER CHECK (bool_value IN (0, 1)),
    int_value INTEGER,
    real_value REAL,
    text_value TEXT,
    PRIMARY KEY (song_id, source_key, node_id),
    FOREIGN KEY (song_id, source_key) REFERENCES music_song_sources(song_id, source_key) ON DELETE CASCADE
) STRICT;

-- module: music/31_music_collections.sql
-- Shared native music library state, user collections, song membership, bindings, and intents.
CREATE TABLE music_library_state (
    scope TEXT PRIMARY KEY NOT NULL,
    revision INTEGER NOT NULL DEFAULT 0 CHECK (revision BETWEEN 0 AND 9007199254740991),
    owner_id TEXT NOT NULL DEFAULT 'device',
    library_revision INTEGER NOT NULL DEFAULT 0 CHECK (library_revision BETWEEN 0 AND 9007199254740991),
    next_id INTEGER NOT NULL DEFAULT 1 CHECK (next_id BETWEEN 1 AND 9007199254740991),
    status_phase TEXT NOT NULL DEFAULT 'idle',
    status_at REAL NOT NULL DEFAULT 0,
    status_code TEXT
) STRICT;

CREATE TABLE music_library_accounts (
    scope TEXT NOT NULL REFERENCES music_library_state(scope) ON DELETE CASCADE,
    platform TEXT NOT NULL,
    account_id TEXT NOT NULL,
    PRIMARY KEY (scope, platform)
) STRICT;

CREATE TABLE music_library_status_issues (
    scope TEXT NOT NULL REFERENCES music_library_state(scope) ON DELETE CASCADE,
    position INTEGER NOT NULL CHECK (position >= 0),
    platform TEXT,
    collection_id TEXT,
    code TEXT NOT NULL,
    PRIMARY KEY (scope, position)
) STRICT;

CREATE TABLE music_collections (
    scope TEXT NOT NULL REFERENCES music_library_state(scope) ON DELETE CASCADE,
    collection_id TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    name TEXT NOT NULL,
    kind TEXT NOT NULL,
    revision INTEGER NOT NULL DEFAULT 0 CHECK (revision BETWEEN 0 AND 9007199254740991),
    manual_order INTEGER NOT NULL DEFAULT 0 CHECK (manual_order IN (0, 1)),
    deleted INTEGER NOT NULL DEFAULT 0 CHECK (deleted IN (0, 1)),
    locally_created INTEGER NOT NULL DEFAULT 0 CHECK (locally_created IN (0, 1)),
    PRIMARY KEY (scope, collection_id)
) STRICT;

CREATE INDEX idx_music_collections_order ON music_collections(scope, position);

CREATE TABLE music_collection_songs (
    scope TEXT NOT NULL,
    collection_id TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    song_id TEXT NOT NULL REFERENCES music_songs(song_id),
    PRIMARY KEY (scope, collection_id, position),
    UNIQUE (scope, collection_id, song_id),
    FOREIGN KEY (scope, collection_id) REFERENCES music_collections(scope, collection_id) ON DELETE CASCADE
) STRICT;

CREATE INDEX idx_music_collection_songs_song ON music_collection_songs(song_id);

CREATE TABLE music_collection_bindings (
    scope TEXT NOT NULL,
    collection_id TEXT NOT NULL,
    binding_index INTEGER NOT NULL CHECK (binding_index >= 0),
    platform TEXT NOT NULL,
    account_id TEXT NOT NULL,
    remote_id TEXT NOT NULL,
    writable INTEGER NOT NULL DEFAULT 0 CHECK (writable IN (0, 1)),
    name TEXT,
    PRIMARY KEY (scope, collection_id, binding_index),
    FOREIGN KEY (scope, collection_id) REFERENCES music_collections(scope, collection_id) ON DELETE CASCADE
) STRICT;

CREATE TABLE music_collection_binding_baseline (
    scope TEXT NOT NULL,
    collection_id TEXT NOT NULL,
    binding_index INTEGER NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    native_id TEXT NOT NULL,
    PRIMARY KEY (scope, collection_id, binding_index, position),
    FOREIGN KEY (scope, collection_id, binding_index)
        REFERENCES music_collection_bindings(scope, collection_id, binding_index) ON DELETE CASCADE
) STRICT;

CREATE TABLE music_collection_binding_map (
    scope TEXT NOT NULL,
    collection_id TEXT NOT NULL,
    binding_index INTEGER NOT NULL,
    song_id TEXT NOT NULL REFERENCES music_songs(song_id),
    remote_entry_id TEXT NOT NULL,
    PRIMARY KEY (scope, collection_id, binding_index, song_id),
    FOREIGN KEY (scope, collection_id, binding_index)
        REFERENCES music_collection_bindings(scope, collection_id, binding_index) ON DELETE CASCADE
) STRICT;

CREATE TABLE music_collection_intents (
    scope TEXT NOT NULL,
    collection_id TEXT NOT NULL,
    intent_key TEXT NOT NULL,
    present INTEGER NOT NULL CHECK (present IN (0, 1)),
    pending INTEGER NOT NULL CHECK (pending IN (0, 1)),
    origin TEXT,
    PRIMARY KEY (scope, collection_id, intent_key),
    FOREIGN KEY (scope, collection_id) REFERENCES music_collections(scope, collection_id) ON DELETE CASCADE
) STRICT;

CREATE TABLE music_collection_intent_acks (
    scope TEXT NOT NULL,
    collection_id TEXT NOT NULL,
    intent_key TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    ack_value TEXT NOT NULL,
    PRIMARY KEY (scope, collection_id, intent_key, position),
    FOREIGN KEY (scope, collection_id, intent_key)
        REFERENCES music_collection_intents(scope, collection_id, intent_key) ON DELETE CASCADE
) STRICT;

-- module: music/32_music_queue.sql
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

-- module: music/33_music_history.sql
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

-- module: ios/40_settings.sql
-- Native iOS settings blocks, TMDB, Douban, Pan credentials, Cat settings, film rules, and preferences.
CREATE TABLE ios_settings_blocks (
    block TEXT PRIMARY KEY CHECK (block IN ('cat.sources', 'pan.accounts', 'tmdb', 'douban', 'film.rules', 'film.preferences')),
    revision INTEGER NOT NULL CHECK (revision BETWEEN 0 AND 9007199254740991)
) STRICT;

INSERT INTO ios_settings_blocks VALUES
    ('cat.sources', 0), ('pan.accounts', 0), ('tmdb', 0), ('douban', 0), ('film.rules', 0), ('film.preferences', 0);

CREATE TABLE ios_tmdb_settings (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    apiBase TEXT NOT NULL,
    imageBase TEXT NOT NULL,
    apiKey TEXT NOT NULL,
    accessToken TEXT NOT NULL,
    language TEXT NOT NULL,
    region TEXT NOT NULL,
    includeAdult INTEGER NOT NULL CHECK (includeAdult IN (0, 1))
) STRICT;

CREATE TABLE ios_douban_settings (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    apiBase TEXT NOT NULL,
    searchBase TEXT NOT NULL,
    imageBase TEXT NOT NULL,
    apiProxy TEXT NOT NULL,
    imageProxy TEXT NOT NULL,
    cookie TEXT NOT NULL
) STRICT;

CREATE TABLE ios_pan_accounts (
    provider TEXT PRIMARY KEY CHECK (provider IN ('baidu', 'quark', 'uc', 'tianyi', 'yidong')),
    account_revision INTEGER NOT NULL CHECK (account_revision BETWEEN 0 AND 9007199254740991),
    row_revision INTEGER NOT NULL CHECK (row_revision BETWEEN 0 AND 9007199254740991),
    cookie TEXT,
    username TEXT,
    password TEXT,
    authorization TEXT,
    refreshedAt INTEGER,
    CHECK (refreshedAt IS NULL OR refreshedAt BETWEEN 0 AND 9007199254740991),
    CHECK (provider IN ('baidu', 'quark', 'uc', 'tianyi') OR cookie IS NULL),
    CHECK (provider = 'tianyi' OR (username IS NULL AND password IS NULL)),
    CHECK (provider = 'yidong' OR (authorization IS NULL AND refreshedAt IS NULL))
) STRICT;

INSERT INTO ios_pan_accounts(provider, account_revision, row_revision) VALUES
    ('baidu', 0, 0), ('quark', 0, 0), ('uc', 0, 0), ('tianyi', 0, 0), ('yidong', 0, 0);

CREATE TABLE ios_cat_settings (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    port INTEGER NOT NULL CHECK (port BETWEEN 0 AND 65535)
) STRICT;

CREATE TABLE ios_cat_providers (
    provider TEXT PRIMARY KEY,
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1))
) STRICT;

CREATE TABLE ios_cat_mappings (
    ordinal INTEGER PRIMARY KEY CHECK (ordinal >= 0),
    provider TEXT NOT NULL
) STRICT;

CREATE TABLE ios_cat_mapping_aliases (
    mapping_ordinal INTEGER NOT NULL REFERENCES ios_cat_mappings(ordinal) ON DELETE CASCADE,
    ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
    alias TEXT NOT NULL,
    PRIMARY KEY (mapping_ordinal, ordinal)
) STRICT;

CREATE TABLE ios_cat_inputs (
    installationId TEXT PRIMARY KEY,
    ordinal INTEGER NOT NULL,
    name TEXT NOT NULL,
    sourceType TEXT NOT NULL CHECK (sourceType IN ('local', 'network')),
    sourceUrl TEXT,
    localPath TEXT,
    localHash TEXT,
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
    inputRevision INTEGER NOT NULL CHECK (inputRevision BETWEEN 1 AND 9007199254740991),
    row_revision INTEGER NOT NULL DEFAULT 1 CHECK (row_revision BETWEEN 1 AND 9007199254740991)
) STRICT;

CREATE TABLE ios_film_rules (
    rule_group TEXT NOT NULL CHECK (rule_group IN ('episodeCleanRegexRules', 'episodeRules', 'movieRules', 'aggregateRegexRules')),
    ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
    pattern TEXT NOT NULL,
    replacement TEXT NOT NULL,
    flags TEXT NOT NULL,
    PRIMARY KEY (rule_group, ordinal)
) STRICT;

CREATE TABLE ios_film_preferences (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    iosAutoPrepare INTEGER NOT NULL CHECK (iosAutoPrepare IN (0, 1)),
    metadataViewProvider TEXT NOT NULL CHECK (metadataViewProvider IN ('tmdb', 'douban'))
) STRICT;

CREATE TABLE ios_film_preference_items (
    list TEXT NOT NULL CHECK (list IN ('ruleOrder', 'panOrder', 'keywords', 'siteCleanKeywords')),
    ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
    value TEXT NOT NULL,
    PRIMARY KEY (list, ordinal),
    UNIQUE (list, value)
) STRICT;

-- module: ios/41_film.sql
-- Native iOS film favorites, player preferences, source probes, video intents, and deletions.
CREATE TABLE ios_film_favorites (
    businessID TEXT PRIMARY KEY,
    sourceID TEXT NOT NULL,
    itemID TEXT NOT NULL,
    kind TEXT,
    target_kind TEXT NOT NULL CHECK (target_kind IN ('movie', 'tv', 'source', 'group')),
    target_title TEXT NOT NULL,
    target_year INTEGER,
    metadata_provider TEXT,
    metadata_id_kind TEXT,
    metadata_id_text TEXT,
    season INTEGER,
    episode INTEGER,
    global_episode INTEGER,
    source_key TEXT,
    source_engine TEXT,
    source_installation_id TEXT,
    source_site_kind TEXT,
    source_site_text TEXT,
    source_detail_kind TEXT,
    source_detail_text TEXT,
    source_line_kind TEXT,
    source_line_text TEXT,
    row_revision INTEGER NOT NULL CHECK (row_revision BETWEEN 1 AND 9007199254740991),
    added_at REAL NOT NULL,
    CHECK ((metadata_id_kind IS NULL AND metadata_id_text IS NULL) OR
           (metadata_id_kind IN ('text', 'integer') AND metadata_id_text IS NOT NULL)),
    CHECK ((source_site_kind IS NULL AND source_site_text IS NULL) OR
           (source_site_kind IN ('text', 'integer') AND source_site_text IS NOT NULL)),
    CHECK ((source_detail_kind IS NULL AND source_detail_text IS NULL) OR
           (source_detail_kind IN ('text', 'integer') AND source_detail_text IS NOT NULL)),
    CHECK ((source_line_kind IS NULL AND source_line_text IS NULL) OR
           (source_line_kind IN ('text', 'integer') AND source_line_text IS NOT NULL))
) STRICT;

CREATE INDEX ios_film_favorites_page ON ios_film_favorites(added_at DESC, businessID);

CREATE TABLE ios_film_favorite_members (
    businessID TEXT NOT NULL REFERENCES ios_film_favorites(businessID) ON DELETE CASCADE,
    ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
    source_key TEXT NOT NULL,
    detail_kind TEXT NOT NULL CHECK (detail_kind IN ('text', 'integer')),
    detail_text TEXT NOT NULL,
    PRIMARY KEY (businessID, ordinal)
) STRICT;

CREATE TABLE ios_film_player_preferences (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    engine TEXT NOT NULL,
    defaultRate REAL NOT NULL,
    holdRate REAL NOT NULL,
    seekStep REAL NOT NULL,
    showsControlCenter INTEGER NOT NULL CHECK (showsControlCenter IN (0, 1)),
    recognizesChapters INTEGER NOT NULL CHECK (recognizesChapters IN (0, 1)),
    singleTapCenter TEXT NOT NULL,
    singleTapSides TEXT NOT NULL,
    doubleTapCenter TEXT NOT NULL,
    doubleTapSides TEXT NOT NULL,
    swipeVolume INTEGER NOT NULL CHECK (swipeVolume IN (0, 1)),
    swipeBrightness INTEGER NOT NULL CHECK (swipeBrightness IN (0, 1)),
    swipeSeek INTEGER NOT NULL CHECK (swipeSeek IN (0, 1)),
    swipeEpisodes INTEGER NOT NULL CHECK (swipeEpisodes IN (0, 1)),
    hapticHold INTEGER NOT NULL CHECK (hapticHold IN (0, 1)),
    hapticBrightness INTEGER NOT NULL CHECK (hapticBrightness IN (0, 1)),
    hapticVolume INTEGER NOT NULL CHECK (hapticVolume IN (0, 1)),
    hapticSeek INTEGER NOT NULL CHECK (hapticSeek IN (0, 1)),
    row_revision INTEGER NOT NULL CHECK (row_revision BETWEEN 1 AND 9007199254740991)
) STRICT;

CREATE TABLE ios_film_source_state (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    revision INTEGER NOT NULL CHECK (revision BETWEEN 0 AND 9007199254740991)
) STRICT;

INSERT INTO ios_film_source_state VALUES (1, 0);

CREATE TABLE ios_film_source_probes (
    installationId TEXT PRIMARY KEY,
    inputRevision INTEGER NOT NULL,
    importedAt INTEGER,
    updatedAt INTEGER,
    updateState TEXT NOT NULL,
    updateError TEXT,
    sourceHash TEXT
) STRICT;

CREATE TABLE ios_film_source_sites (
    installationId TEXT NOT NULL REFERENCES ios_film_source_probes(installationId) ON DELETE CASCADE,
    siteKey TEXT NOT NULL,
    ordinal INTEGER NOT NULL,
    name TEXT NOT NULL,
    api TEXT NOT NULL,
    PRIMARY KEY (installationId, siteKey)
) STRICT;

CREATE TABLE ios_film_source_actions (
    installationId TEXT NOT NULL,
    siteKey TEXT NOT NULL,
    ordinal INTEGER NOT NULL,
    action TEXT NOT NULL,
    PRIMARY KEY (installationId, siteKey, ordinal),
    FOREIGN KEY (installationId, siteKey) REFERENCES ios_film_source_sites(installationId, siteKey) ON DELETE CASCADE
) STRICT;

CREATE TABLE ios_film_source_methods (
    installationId TEXT NOT NULL,
    siteKey TEXT NOT NULL,
    action TEXT NOT NULL,
    method TEXT NOT NULL,
    PRIMARY KEY (installationId, siteKey, action),
    FOREIGN KEY (installationId, siteKey) REFERENCES ios_film_source_sites(installationId, siteKey) ON DELETE CASCADE
) STRICT;

CREATE TABLE ios_film_video_intents (
    installationId TEXT NOT NULL,
    siteKey TEXT NOT NULL,
    ordinal INTEGER NOT NULL,
    name TEXT NOT NULL,
    api TEXT NOT NULL,
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
    homeVisible INTEGER NOT NULL CHECK (homeVisible IN (0, 1)),
    searchEnabled INTEGER NOT NULL CHECK (searchEnabled IN (0, 1)),
    aggregateImages INTEGER NOT NULL CHECK (aggregateImages IN (0, 1)),
    PRIMARY KEY (installationId, siteKey)
) STRICT;

CREATE UNIQUE INDEX ios_film_one_aggregate_images ON ios_film_video_intents(aggregateImages) WHERE aggregateImages = 1;

CREATE TABLE ios_film_video_actions (
    installationId TEXT NOT NULL,
    siteKey TEXT NOT NULL,
    ordinal INTEGER NOT NULL,
    action TEXT NOT NULL,
    PRIMARY KEY (installationId, siteKey, ordinal),
    FOREIGN KEY (installationId, siteKey) REFERENCES ios_film_video_intents(installationId, siteKey) ON DELETE CASCADE
) STRICT;

CREATE TABLE ios_film_video_methods (
    installationId TEXT NOT NULL,
    siteKey TEXT NOT NULL,
    action TEXT NOT NULL,
    method TEXT NOT NULL,
    PRIMARY KEY (installationId, siteKey, action),
    FOREIGN KEY (installationId, siteKey) REFERENCES ios_film_video_intents(installationId, siteKey) ON DELETE CASCADE
) STRICT;

CREATE TABLE ios_film_video_detections (
    installationId TEXT NOT NULL,
    siteKey TEXT NOT NULL,
    result TEXT NOT NULL,
    checkedAt INTEGER NOT NULL CHECK (checkedAt BETWEEN 0 AND 9007199254740991),
    playbackResolved INTEGER NOT NULL CHECK (playbackResolved IN (0, 1)),
    PRIMARY KEY (installationId, siteKey),
    FOREIGN KEY (installationId, siteKey) REFERENCES ios_film_video_intents(installationId, siteKey) ON DELETE CASCADE
) STRICT;

CREATE TABLE ios_film_video_probe_steps (
    installationId TEXT NOT NULL,
    siteKey TEXT NOT NULL,
    ordinal INTEGER NOT NULL,
    operation TEXT NOT NULL,
    state TEXT NOT NULL,
    error TEXT,
    PRIMARY KEY (installationId, siteKey, ordinal),
    FOREIGN KEY (installationId, siteKey) REFERENCES ios_film_video_detections(installationId, siteKey) ON DELETE CASCADE
) STRICT;

CREATE TABLE ios_film_source_deletions (
    installationId TEXT NOT NULL,
    siteKey TEXT NOT NULL,
    PRIMARY KEY (installationId, siteKey)
) STRICT;

-- module: ios/42_novel.sql
-- Native iOS novel shelf, reading progress, dynamic source variables, reading settings, and replacement rules.
CREATE TABLE ios_novel_books (
    id TEXT PRIMARY KEY,
    installationId TEXT NOT NULL,
    bookUrl TEXT NOT NULL,
    identity TEXT NOT NULL,
    name TEXT NOT NULL,
    author TEXT NOT NULL,
    tocUrl TEXT NOT NULL,
    coverUrl TEXT NOT NULL,
    intro TEXT NOT NULL,
    latestChapterTitle TEXT NOT NULL,
    onShelf INTEGER NOT NULL CHECK (onShelf IN (0, 1)),
    pinnedAt REAL,
    addedAt REAL NOT NULL,
    row_revision INTEGER NOT NULL CHECK (row_revision BETWEEN 1 AND 9007199254740991),
    UNIQUE (installationId, bookUrl)
) STRICT;

CREATE UNIQUE INDEX ios_novel_book_identity ON ios_novel_books(identity) WHERE identity <> '';
CREATE INDEX ios_novel_shelf ON ios_novel_books(onShelf, pinnedAt DESC, addedAt DESC, id);

CREATE TABLE ios_novel_progress (
    book_id TEXT PRIMARY KEY REFERENCES ios_novel_books(id) ON DELETE CASCADE,
    chapterUrl TEXT NOT NULL,
    chapterTitle TEXT NOT NULL,
    chapterIndex INTEGER NOT NULL CHECK (chapterIndex >= 0),
    blockIndex INTEGER NOT NULL CHECK (blockIndex >= 0),
    utf16Offset INTEGER NOT NULL CHECK (utf16Offset >= 0),
    ratio REAL NOT NULL CHECK (ratio BETWEEN 0 AND 1),
    readAt REAL NOT NULL,
    row_revision INTEGER NOT NULL CHECK (row_revision BETWEEN 1 AND 9007199254740991)
) STRICT;

CREATE INDEX ios_novel_history ON ios_novel_progress(readAt DESC, book_id);

-- Third-party book source dynamic variable tree.
CREATE TABLE ios_novel_variable_nodes (
    book_id TEXT NOT NULL REFERENCES ios_novel_books(id) ON DELETE CASCADE,
    node_id INTEGER NOT NULL CHECK (node_id >= 0),
    parent_id INTEGER,
    prop_key TEXT,
    item_index INTEGER,
    kind TEXT NOT NULL CHECK (kind IN ('null', 'boolean', 'integer', 'real', 'number_text', 'text', 'array', 'object')),
    bool_value INTEGER CHECK (bool_value IN (0, 1)),
    int_value INTEGER,
    real_value REAL,
    text_value TEXT,
    PRIMARY KEY (book_id, node_id)
) STRICT;

CREATE TABLE ios_novel_reading_settings (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    mode TEXT NOT NULL,
    fontSize REAL NOT NULL,
    fontWeight REAL NOT NULL,
    fontName TEXT NOT NULL,
    lineSpacing REAL NOT NULL,
    paragraphSpacing REAL NOT NULL,
    margin REAL NOT NULL,
    contentWidth REAL NOT NULL,
    conversion TEXT NOT NULL,
    theme INTEGER NOT NULL,
    foreground TEXT NOT NULL,
    background TEXT NOT NULL,
    background_image BLOB,
    automaticSpeed REAL NOT NULL,
    row_revision INTEGER NOT NULL CHECK (row_revision BETWEEN 1 AND 9007199254740991)
) STRICT;

CREATE TABLE ios_novel_rules (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    "group" TEXT NOT NULL,
    isEnabled INTEGER NOT NULL CHECK (isEnabled IN (0, 1)),
    "order" INTEGER NOT NULL,
    timeoutMillisecond INTEGER NOT NULL CHECK (timeoutMillisecond > 0),
    pattern TEXT NOT NULL,
    replacement TEXT NOT NULL,
    isRegex INTEGER NOT NULL CHECK (isRegex IN (0, 1)),
    scope TEXT NOT NULL,
    excludeScope TEXT NOT NULL,
    scopeTitle INTEGER NOT NULL CHECK (scopeTitle IN (0, 1)),
    scopeContent INTEGER NOT NULL CHECK (scopeContent IN (0, 1))
) STRICT;

-- module: ios/43_music_domain.sql
-- Native iOS music preferences, accounts, network references, sync state, playback prefs, source discovery, and home snapshot.
CREATE TABLE ios_search_history (
    text TEXT PRIMARY KEY,
    used_at REAL NOT NULL
) STRICT;

CREATE INDEX ios_search_history_recent ON ios_search_history(used_at DESC, text);

CREATE TABLE ios_music_search_preferences (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    searchPlaylists INTEGER NOT NULL CHECK (searchPlaylists IN (0, 1)),
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
    ordinal INTEGER NOT NULL,
    platform TEXT NOT NULL,
    PRIMARY KEY (kind, ordinal),
    UNIQUE (kind, platform)
) STRICT;

CREATE TABLE ios_music_appearance (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    playlistView TEXT NOT NULL,
    playerTheme TEXT NOT NULL,
    colorMode TEXT NOT NULL,
    colorPalette TEXT NOT NULL,
    lockLyric INTEGER NOT NULL CHECK (lockLyric IN (0, 1)),
    lyricTranslation INTEGER NOT NULL CHECK (lyricTranslation IN (0, 1)),
    lyricBackground INTEGER NOT NULL CHECK (lyricBackground IN (0, 1)),
    row_revision INTEGER NOT NULL CHECK (row_revision BETWEEN 1 AND 9007199254740991)
) STRICT;

CREATE TABLE ios_music_song_palettes (
    song_key TEXT PRIMARY KEY,
    palette TEXT NOT NULL,
    used_at REAL NOT NULL
) STRICT;

CREATE INDEX ios_music_song_palettes_recent ON ios_music_song_palettes(used_at DESC, song_key);

CREATE TABLE ios_music_palette_state (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    last_palette TEXT NOT NULL
) STRICT;

CREATE TABLE ios_music_channel_selection (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    channel_key TEXT NOT NULL
) STRICT;

CREATE TABLE ios_playback_volume (
    owner TEXT PRIMARY KEY,
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

CREATE TABLE ios_music_catalog_source (
    scope TEXT PRIMARY KEY NOT NULL,
    revision INTEGER NOT NULL DEFAULT 0 CHECK (revision BETWEEN 0 AND 9007199254740991),
    platform TEXT NOT NULL DEFAULT 'wy'
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

-- iOS music library sync state, automatic sync selections/bindings, and mutation fences.
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

-- iOS music playback preferences, quality/official/metadata lists, and source exclusions.
CREATE TABLE ios_music_playback_settings (
    scope TEXT PRIMARY KEY NOT NULL,
    revision INTEGER NOT NULL DEFAULT 0 CHECK (revision BETWEEN 0 AND 9007199254740991),
    mode INTEGER NOT NULL DEFAULT 1,
    lyrics_enabled INTEGER NOT NULL DEFAULT 1 CHECK (lyrics_enabled IN (0, 1)),
    show_translation INTEGER NOT NULL DEFAULT 1 CHECK (show_translation IN (0, 1)),
    quality_request TEXT,
    position_occurrence_id TEXT,
    position_seconds REAL NOT NULL DEFAULT 0
) STRICT;

CREATE TABLE ios_music_playback_lists (
    scope TEXT NOT NULL REFERENCES ios_music_playback_settings(scope) ON DELETE CASCADE,
    list_kind TEXT NOT NULL CHECK (list_kind IN (
        'qualityOrder', 'qualityEnabled', 'spatialOrder', 'spatialEnabled',
        'officialSources', 'metadataPlatforms', 'metadataEnabled'
    )),
    position INTEGER NOT NULL CHECK (position >= 0),
    item_value TEXT NOT NULL,
    PRIMARY KEY (scope, list_kind, position)
) STRICT;

CREATE TABLE ios_music_source_exclusions (
    scope TEXT NOT NULL REFERENCES ios_music_playback_settings(scope) ON DELETE CASCADE,
    exclusion_kind TEXT NOT NULL CHECK (exclusion_kind IN ('source', 'recording')),
    exclusion_id TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    song_id TEXT NOT NULL REFERENCES music_songs(song_id),
    title TEXT NOT NULL DEFAULT '',
    artist TEXT NOT NULL DEFAULT '',
    provider TEXT NOT NULL DEFAULT '',
    platform TEXT NOT NULL DEFAULT '',
    source_key TEXT,
    PRIMARY KEY (scope, exclusion_kind, exclusion_id)
) STRICT;

CREATE INDEX idx_ios_music_source_exclusions_order ON ios_music_source_exclusions(scope, exclusion_kind, position);

-- iOS music source discovery cache (replaces music_source_discovery_* generated trees).
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

-- iOS music home display snapshot (normalized flat tables, no shape_mask/null_mask).
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

-- module: ios/44_music_resources.sql
-- Native iOS music resource caches, playback feedback outbox, lyrics, and audio file index.
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

CREATE INDEX ios_music_audio_urls_age ON ios_music_audio_urls(saved_at);

CREATE TABLE ios_music_chorus (
    scope TEXT NOT NULL,
    platform TEXT NOT NULL,
    native_id TEXT NOT NULL,
    recording_duration REAL NOT NULL CHECK (recording_duration > 0),
    start REAL NOT NULL CHECK (start >= 0),
    end REAL NOT NULL CHECK (end >= start),
    expires_at REAL NOT NULL,
    PRIMARY KEY (scope, platform, native_id, recording_duration)
) STRICT;

CREATE TABLE ios_music_feedback_outbox (
    id TEXT PRIMARY KEY NOT NULL,
    instance TEXT NOT NULL,
    scope TEXT NOT NULL,
    generation INTEGER NOT NULL CHECK (generation >= 0),
    phase TEXT NOT NULL CHECK (phase IN ('start', 'end')),
    nativeID TEXT NOT NULL,
    source TEXT NOT NULL,
    sourceID TEXT NOT NULL,
    algorithm TEXT NOT NULL,
    heartbeat INTEGER NOT NULL CHECK (heartbeat IN (0, 1)),
    listened REAL NOT NULL CHECK (listened >= 0),
    media REAL NOT NULL CHECK (media >= 0),
    rate REAL NOT NULL CHECK (rate > 0),
    occurredAt REAL NOT NULL CHECK (occurredAt >= 0),
    reason TEXT,
    state TEXT NOT NULL CHECK (state IN ('pending', 'inflight')),
    startConfirmed INTEGER NOT NULL CHECK (startConfirmed IN (0, 1))
) STRICT;

CREATE INDEX ios_music_feedback_dispatch ON ios_music_feedback_outbox(scope, state, occurredAt);

CREATE TABLE ios_music_images (
    resource TEXT NOT NULL,
    filename TEXT PRIMARY KEY NOT NULL,
    updated_at REAL NOT NULL,
    bytes INTEGER NOT NULL CHECK (bytes > 0),
    ready INTEGER NOT NULL CHECK (ready IN (0, 1))
) STRICT;

CREATE UNIQUE INDEX ios_music_images_ready ON ios_music_images(resource) WHERE ready = 1;
CREATE INDEX ios_music_images_age ON ios_music_images(updated_at);

CREATE TABLE ios_music_lyrics (
    song_id TEXT PRIMARY KEY NOT NULL,
    source_key TEXT NOT NULL,
    updated_at REAL NOT NULL,
    bytes INTEGER NOT NULL CHECK (bytes > 0)
) STRICT;

CREATE TABLE ios_music_lyric_lines (
    song_id TEXT NOT NULL REFERENCES ios_music_lyrics(song_id) ON DELETE CASCADE,
    position INTEGER NOT NULL CHECK (position >= 0),
    line_index INTEGER NOT NULL,
    time REAL NOT NULL CHECK (time >= 0),
    end REAL NOT NULL CHECK (end >= time),
    text TEXT NOT NULL,
    translation TEXT NOT NULL,
    PRIMARY KEY (song_id, position)
) STRICT;

CREATE TABLE ios_music_lyric_words (
    song_id TEXT NOT NULL,
    line_position INTEGER NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    time REAL NOT NULL,
    duration REAL NOT NULL CHECK (duration >= 0),
    text TEXT NOT NULL,
    PRIMARY KEY (song_id, line_position, position),
    FOREIGN KEY (song_id, line_position) REFERENCES ios_music_lyric_lines(song_id, position) ON DELETE CASCADE
) STRICT;

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

CREATE INDEX ios_music_audio_files_song ON ios_music_audio_files(scope, song_id, last_played_at);
CREATE INDEX ios_music_audio_files_age ON ios_music_audio_files(expires_at);
