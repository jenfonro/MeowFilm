-- module: 00_core.sql
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

-- module: 10_runtime_scripts.sql
-- Music script catalog and per-key LX guest storage
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

CREATE TABLE music_script_declared_sites (
    installation_id TEXT NOT NULL REFERENCES music_script_installations(installation_id) ON DELETE CASCADE,
    site_id TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    has_qualities INTEGER NOT NULL CHECK (has_qualities IN (0,1)),
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
    state TEXT NOT NULL CHECK (state IN ('ok','failed','unsupported')),
    quality TEXT,
    sample TEXT,
    code TEXT,
    PRIMARY KEY (installation_id,site_id)
) STRICT;

-- Standalone Pan accounts (plaintext typed credentials)
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
INSERT INTO pan_standalone_accounts(provider,revision,row_revision) VALUES
 ('baidu',0,0),('quark',0,0),('uc',0,0),('tianyi',0,0),('yidong',0,0);

-- Standalone metadata settings (plaintext typed credentials)
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

-- Music platform anonymous protocol state
CREATE TABLE music_anonymous_state (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    row_revision INTEGER NOT NULL CHECK (row_revision BETWEEN 1 AND 9007199254740991)
) STRICT;

CREATE TABLE music_anonymous_wy_cookies (
    cookie_name TEXT PRIMARY KEY NOT NULL,
    cookie_value TEXT NOT NULL,
    singleton INTEGER NOT NULL DEFAULT 1 CHECK(singleton=1) REFERENCES music_anonymous_state(singleton) ON DELETE CASCADE
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

-- module: 11_runtime_source.sql
-- Source runtime installations, structured definitions, cookies and per-key KV
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
    enabledCookieJar INTEGER CHECK(enabledCookieJar IN (0,1))
) STRICT;

CREATE TABLE source_definition_rules (
    installation_id TEXT NOT NULL REFERENCES source_installations(installation_id) ON DELETE CASCADE,
    rule_group TEXT NOT NULL CHECK(rule_group IN ('ruleSearch','ruleExplore','ruleBookInfo','ruleToc','ruleContent')),
    encoded INTEGER NOT NULL CHECK(encoded IN (0,1)),
    bookList TEXT, name TEXT, author TEXT, kind TEXT, wordCount TEXT, lastChapter TEXT,
    intro TEXT, coverUrl TEXT, bookUrl TEXT, tocUrl TEXT, init TEXT,
    chapterList TEXT, chapterName TEXT, chapterUrl TEXT, nextTocUrl TEXT,
    isReverse TEXT, reverseToc TEXT, updateTime TEXT, preUpdateJs TEXT,
    content TEXT, nextContentUrl TEXT, title TEXT, replaceRegex TEXT,
    sourceRegex TEXT, webJs TEXT, payAction TEXT, imageStyle TEXT,
    imageDecode TEXT, imageDecrypt TEXT,
    PRIMARY KEY(installation_id,rule_group)
) STRICT;

-- Only externally defined extension fields/variant recipe values use nodes.
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
    PRIMARY KEY(installation_id,profile_suffix)
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
    FOREIGN KEY(installation_id,profile_suffix)
        REFERENCES source_private_profiles(installation_id,profile_suffix) ON DELETE CASCADE
) STRICT;

CREATE INDEX idx_source_private_cookies_installation ON source_private_cookies(installation_id);

CREATE TABLE source_private_kv_entries (
    installation_id TEXT NOT NULL,
    profile_suffix TEXT NOT NULL,
    entry_key TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    PRIMARY KEY (installation_id, profile_suffix, entry_key),
    FOREIGN KEY(installation_id,profile_suffix)
        REFERENCES source_private_profiles(installation_id,profile_suffix) ON DELETE CASCADE
) STRICT;

CREATE INDEX idx_source_private_kv_installation ON source_private_kv_entries(installation_id);

-- Reader's synchronous source.get/put scope is separate from the asynchronous cache KV.
CREATE TABLE source_reader_state (
    installation_id TEXT PRIMARY KEY NOT NULL,
    revision INTEGER NOT NULL DEFAULT 0 CHECK (revision BETWEEN 0 AND 9007199254740991),
    variable BLOB CHECK (length(variable) % 2 = 0)
) STRICT;

CREATE TABLE source_reader_value_nodes (
    installation_id TEXT NOT NULL REFERENCES source_reader_state(installation_id) ON DELETE CASCADE,
    entry_key BLOB NOT NULL CHECK (length(entry_key) % 2 = 0),
    node_id INTEGER NOT NULL CHECK (node_id >= 0),
    parent_id INTEGER,
    prop_key BLOB CHECK (length(prop_key) % 2 = 0),
    item_index INTEGER CHECK (item_index >= 0),
    kind TEXT NOT NULL CHECK (kind IN ('null','boolean','integer','real','number_text','text','array','object')),
    bool_value INTEGER CHECK (bool_value IN (0,1)),
    int_value INTEGER,
    real_value REAL,
    text_value ANY,
    CHECK ((kind='boolean')=(bool_value IS NOT NULL) AND (kind='integer')=(int_value IS NOT NULL)
        AND (kind='real')=(real_value IS NOT NULL)),
    CHECK (CASE kind WHEN 'text' THEN typeof(text_value)='blob' AND length(text_value)%2=0
        WHEN 'number_text' THEN typeof(text_value)='text' ELSE text_value IS NULL END),
    CHECK (CASE WHEN node_id=0 THEN parent_id IS NULL AND prop_key IS NULL AND item_index IS NULL
        ELSE parent_id IS NOT NULL AND parent_id>=0 AND parent_id<node_id AND (prop_key IS NULL)<>(item_index IS NULL) END),
    PRIMARY KEY(installation_id,entry_key,node_id),
    FOREIGN KEY(installation_id,entry_key,parent_id) REFERENCES source_reader_value_nodes(installation_id,entry_key,node_id)
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

-- module: 12_runtime_cat.sql
-- Cat runtime gateway settings and installation facts
CREATE TABLE cat_gateway_settings (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    port INTEGER NOT NULL CHECK (port BETWEEN 0 AND 65535)
) STRICT;

CREATE TABLE cat_provider_entries (
    provider_id TEXT PRIMARY KEY NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    enabled INTEGER NOT NULL CHECK(enabled IN (0,1))
) STRICT;

CREATE TABLE cat_mappings (
    position INTEGER PRIMARY KEY CHECK (position >= 0),
    provider_id TEXT NOT NULL
) STRICT;

CREATE TABLE cat_mapping_aliases (
    position INTEGER NOT NULL REFERENCES cat_mappings(position) ON DELETE CASCADE,
    ordinal INTEGER NOT NULL CHECK(ordinal >= 0),
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

-- module: 20_runtime_film.sql
-- Film playback library, history, manual matches, blocks, preview targets and groups
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

-- module: 30_native_music.sql
-- Generated closed columns/relations; no runtime payload columns.

CREATE TABLE music_owner_state (scope TEXT PRIMARY KEY NOT NULL, revision INTEGER NOT NULL CHECK(revision BETWEEN 0 AND 9007199254740991), receipt TEXT NOT NULL) STRICT;

CREATE TABLE "music_songs" (
    "song_id" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 2047),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "title" TEXT,
    "artist" TEXT,
    "album" TEXT,
    "duration_kind" TEXT CHECK("duration_kind" IN ('text','number')),
    "duration_value" TEXT,
    "baselineDuration_kind" TEXT CHECK("baselineDuration_kind" IN ('text','number')),
    "baselineDuration_value" TEXT,
    "cover_url" TEXT,
    "localFileId" TEXT,
    "artworkSourceKey" TEXT,
    "artworkSourceImage" TEXT,
    lookup_key TEXT,
    search_query TEXT,
    fingerprint TEXT,
    baseline_duration REAL CHECK(baseline_duration>0),
    CHECK(("title" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("artist" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("album" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("duration_kind" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("duration_kind" IS NULL)=("duration_value" IS NULL)),
    CHECK(("baselineDuration_kind" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("baselineDuration_kind" IS NULL)=("baselineDuration_value" IS NULL)),
    CHECK(("cover_url" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("localFileId" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("artworkSourceKey" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("artworkSourceImage" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    PRIMARY KEY("song_id")
) STRICT;

CREATE TABLE "music_songs_extensions" (
    "song_id" TEXT NOT NULL,
    node_id INTEGER NOT NULL CHECK(node_id>=0),
    parent_id INTEGER,
    prop_key TEXT,
    item_index INTEGER,
    kind TEXT NOT NULL CHECK(kind IN ('null','boolean','integer','real','number_text','text','array','object')),
    bool_value INTEGER CHECK(bool_value IN (0,1)),
    int_value INTEGER,
    real_value REAL,
    text_value TEXT,
    PRIMARY KEY("song_id","node_id"),
    FOREIGN KEY("song_id") REFERENCES "music_songs"("song_id") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_songs_artistNames" (
    "song_id" TEXT NOT NULL,
    "artistNames_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("song_id","artistNames_key"),
    FOREIGN KEY("song_id") REFERENCES "music_songs"("song_id") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_song_sources" (
    "song_id" TEXT NOT NULL,
    "sources_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "source" TEXT,
    CHECK(("source" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("song_id","sources_key"),
    FOREIGN KEY("song_id") REFERENCES "music_songs"("song_id") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_song_sources_extensions" (
    "song_id" TEXT NOT NULL,
    "sources_key" TEXT NOT NULL,
    node_id INTEGER NOT NULL CHECK(node_id>=0),
    parent_id INTEGER,
    prop_key TEXT,
    item_index INTEGER,
    kind TEXT NOT NULL CHECK(kind IN ('null','boolean','integer','real','number_text','text','array','object')),
    bool_value INTEGER CHECK(bool_value IN (0,1)),
    int_value INTEGER,
    real_value REAL,
    text_value TEXT,
    PRIMARY KEY("song_id","sources_key","node_id"),
    FOREIGN KEY("song_id","sources_key") REFERENCES "music_song_sources"("song_id","sources_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_source_info" (
    "song_id" TEXT NOT NULL,
    "sources_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 4294967295),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "source" TEXT,
    "songmid_kind" TEXT CHECK("songmid_kind" IN ('text','number')),
    "songmid_value" TEXT,
    "mid_kind" TEXT CHECK("mid_kind" IN ('text','number')),
    "mid_value" TEXT,
    "id_kind" TEXT CHECK("id_kind" IN ('text','number')),
    "id_value" TEXT,
    "songId_kind" TEXT CHECK("songId_kind" IN ('text','number')),
    "songId_value" TEXT,
    "qqSongId_kind" TEXT CHECK("qqSongId_kind" IN ('text','number')),
    "qqSongId_value" TEXT,
    "songType_kind" TEXT CHECK("songType_kind" IN ('text','number')),
    "songType_value" TEXT,
    "type_kind" TEXT CHECK("type_kind" IN ('text','number')),
    "type_value" TEXT,
    "copyrightId_kind" TEXT CHECK("copyrightId_kind" IN ('text','number')),
    "copyrightId_value" TEXT,
    "strMediaMid_kind" TEXT CHECK("strMediaMid_kind" IN ('text','number')),
    "strMediaMid_value" TEXT,
    "hash" TEXT,
    "albumId_kind" TEXT CHECK("albumId_kind" IN ('text','number')),
    "albumId_value" TEXT,
    "album_id_kind" TEXT CHECK("album_id_kind" IN ('text','number')),
    "album_id_value" TEXT,
    "albumMid_kind" TEXT CHECK("albumMid_kind" IN ('text','number')),
    "albumMid_value" TEXT,
    "albumAudioId_kind" TEXT CHECK("albumAudioId_kind" IN ('text','number')),
    "albumAudioId_value" TEXT,
    "mixsongid_kind" TEXT CHECK("mixsongid_kind" IN ('text','number')),
    "mixsongid_value" TEXT,
    "fileid_kind" TEXT CHECK("fileid_kind" IN ('text','number')),
    "fileid_value" TEXT,
    "fileId_camel_kind" TEXT CHECK("fileId_camel_kind" IN ('text','number')),
    "fileId_camel_value" TEXT,
    "name" TEXT,
    "singer" TEXT,
    "albumName" TEXT,
    "duration_kind" TEXT CHECK("duration_kind" IN ('text','number')),
    "duration_value" TEXT,
    "size_kind" TEXT CHECK("size_kind" IN ('text','number')),
    "size_value" TEXT,
    "contentId_kind" TEXT CHECK("contentId_kind" IN ('text','number')),
    "contentId_value" TEXT,
    "md5" TEXT,
    "img" TEXT,
    "qualityKnown" INTEGER CHECK("qualityKnown" IN (0,1)),
    CHECK(("source" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("songmid_kind" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("songmid_kind" IS NULL)=("songmid_value" IS NULL)),
    CHECK(("mid_kind" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("mid_kind" IS NULL)=("mid_value" IS NULL)),
    CHECK(("id_kind" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("id_kind" IS NULL)=("id_value" IS NULL)),
    CHECK(("songId_kind" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("songId_kind" IS NULL)=("songId_value" IS NULL)),
    CHECK(("qqSongId_kind" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("qqSongId_kind" IS NULL)=("qqSongId_value" IS NULL)),
    CHECK(("songType_kind" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("songType_kind" IS NULL)=("songType_value" IS NULL)),
    CHECK(("type_kind" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("type_kind" IS NULL)=("type_value" IS NULL)),
    CHECK(("copyrightId_kind" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("copyrightId_kind" IS NULL)=("copyrightId_value" IS NULL)),
    CHECK(("strMediaMid_kind" IS NULL)=((shape_mask & 512)=0 OR (null_mask & 512)<>0)),
    CHECK(("strMediaMid_kind" IS NULL)=("strMediaMid_value" IS NULL)),
    CHECK(("hash" IS NULL)=((shape_mask & 1024)=0 OR (null_mask & 1024)<>0)),
    CHECK(("albumId_kind" IS NULL)=((shape_mask & 2048)=0 OR (null_mask & 2048)<>0)),
    CHECK(("albumId_kind" IS NULL)=("albumId_value" IS NULL)),
    CHECK(("album_id_kind" IS NULL)=((shape_mask & 4096)=0 OR (null_mask & 4096)<>0)),
    CHECK(("album_id_kind" IS NULL)=("album_id_value" IS NULL)),
    CHECK(("albumMid_kind" IS NULL)=((shape_mask & 8192)=0 OR (null_mask & 8192)<>0)),
    CHECK(("albumMid_kind" IS NULL)=("albumMid_value" IS NULL)),
    CHECK(("albumAudioId_kind" IS NULL)=((shape_mask & 16384)=0 OR (null_mask & 16384)<>0)),
    CHECK(("albumAudioId_kind" IS NULL)=("albumAudioId_value" IS NULL)),
    CHECK(("mixsongid_kind" IS NULL)=((shape_mask & 32768)=0 OR (null_mask & 32768)<>0)),
    CHECK(("mixsongid_kind" IS NULL)=("mixsongid_value" IS NULL)),
    CHECK(("fileid_kind" IS NULL)=((shape_mask & 65536)=0 OR (null_mask & 65536)<>0)),
    CHECK(("fileid_kind" IS NULL)=("fileid_value" IS NULL)),
    CHECK(("fileId_camel_kind" IS NULL)=((shape_mask & 131072)=0 OR (null_mask & 131072)<>0)),
    CHECK(("fileId_camel_kind" IS NULL)=("fileId_camel_value" IS NULL)),
    CHECK(("name" IS NULL)=((shape_mask & 262144)=0 OR (null_mask & 262144)<>0)),
    CHECK(("singer" IS NULL)=((shape_mask & 524288)=0 OR (null_mask & 524288)<>0)),
    CHECK(("albumName" IS NULL)=((shape_mask & 1048576)=0 OR (null_mask & 1048576)<>0)),
    CHECK(("duration_kind" IS NULL)=((shape_mask & 2097152)=0 OR (null_mask & 2097152)<>0)),
    CHECK(("duration_kind" IS NULL)=("duration_value" IS NULL)),
    CHECK(("size_kind" IS NULL)=((shape_mask & 4194304)=0 OR (null_mask & 4194304)<>0)),
    CHECK(("size_kind" IS NULL)=("size_value" IS NULL)),
    CHECK(("contentId_kind" IS NULL)=((shape_mask & 8388608)=0 OR (null_mask & 8388608)<>0)),
    CHECK(("contentId_kind" IS NULL)=("contentId_value" IS NULL)),
    CHECK(("md5" IS NULL)=((shape_mask & 16777216)=0 OR (null_mask & 16777216)<>0)),
    CHECK(("img" IS NULL)=((shape_mask & 33554432)=0 OR (null_mask & 33554432)<>0)),
    CHECK(("qualityKnown" IS NULL)=((shape_mask & 67108864)=0 OR (null_mask & 67108864)<>0)),
    PRIMARY KEY("song_id","sources_key"),
    FOREIGN KEY("song_id","sources_key") REFERENCES "music_song_sources"("song_id","sources_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_source_info_extensions" (
    "song_id" TEXT NOT NULL,
    "sources_key" TEXT NOT NULL,
    node_id INTEGER NOT NULL CHECK(node_id>=0),
    parent_id INTEGER,
    prop_key TEXT,
    item_index INTEGER,
    kind TEXT NOT NULL CHECK(kind IN ('null','boolean','integer','real','number_text','text','array','object')),
    bool_value INTEGER CHECK(bool_value IN (0,1)),
    int_value INTEGER,
    real_value REAL,
    text_value TEXT,
    PRIMARY KEY("song_id","sources_key","node_id"),
    FOREIGN KEY("song_id","sources_key") REFERENCES "music_source_info"("song_id","sources_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_source_info_artistNames" (
    "song_id" TEXT NOT NULL,
    "sources_key" TEXT NOT NULL,
    "artistNames_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("song_id","sources_key","artistNames_key"),
    FOREIGN KEY("song_id","sources_key") REFERENCES "music_source_info"("song_id","sources_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_source_info_types" (
    "song_id" TEXT NOT NULL,
    "sources_key" TEXT NOT NULL,
    "types_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "type_kind" TEXT CHECK("type_kind" IN ('text','number')),
    "type_value" TEXT,
    "hash" TEXT,
    "size_kind" TEXT CHECK("size_kind" IN ('text','number')),
    "size_value" TEXT,
    CHECK(("type_kind" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("type_kind" IS NULL)=("type_value" IS NULL)),
    CHECK(("hash" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("size_kind" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("size_kind" IS NULL)=("size_value" IS NULL)),
    PRIMARY KEY("song_id","sources_key","types_key"),
    FOREIGN KEY("song_id","sources_key") REFERENCES "music_source_info"("song_id","sources_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_source_info__types" (
    "song_id" TEXT NOT NULL,
    "sources_key" TEXT NOT NULL,
    "_types_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "hash" TEXT,
    "size_kind" TEXT CHECK("size_kind" IN ('text','number')),
    "size_value" TEXT,
    CHECK(("hash" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("size_kind" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("size_kind" IS NULL)=("size_value" IS NULL)),
    PRIMARY KEY("song_id","sources_key","_types_key"),
    FOREIGN KEY("song_id","sources_key") REFERENCES "music_source_info"("song_id","sources_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_source_info_audioFormats" (
    "song_id" TEXT NOT NULL,
    "sources_key" TEXT NOT NULL,
    "audioFormats_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 33554431),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "formatType_kind" TEXT CHECK("formatType_kind" IN ('text','number')),
    "formatType_value" TEXT,
    "resourceType_kind" TEXT CHECK("resourceType_kind" IN ('text','number')),
    "resourceType_value" TEXT,
    "size_kind" TEXT CHECK("size_kind" IN ('text','number')),
    "size_value" TEXT,
    "androidSize_kind" TEXT CHECK("androidSize_kind" IN ('text','number')),
    "androidSize_value" TEXT,
    "iosSize_kind" TEXT CHECK("iosSize_kind" IN ('text','number')),
    "iosSize_value" TEXT,
    "asize_kind" TEXT CHECK("asize_kind" IN ('text','number')),
    "asize_value" TEXT,
    "isize_kind" TEXT CHECK("isize_kind" IN ('text','number')),
    "isize_value" TEXT,
    "format_kind" TEXT CHECK("format_kind" IN ('text','number')),
    "format_value" TEXT,
    "androidFormat_kind" TEXT CHECK("androidFormat_kind" IN ('text','number')),
    "androidFormat_value" TEXT,
    "iosFormat_kind" TEXT CHECK("iosFormat_kind" IN ('text','number')),
    "iosFormat_value" TEXT,
    "aformat_kind" TEXT CHECK("aformat_kind" IN ('text','number')),
    "aformat_value" TEXT,
    "iformat_kind" TEXT CHECK("iformat_kind" IN ('text','number')),
    "iformat_value" TEXT,
    "bitrate_kind" TEXT CHECK("bitrate_kind" IN ('text','number')),
    "bitrate_value" TEXT,
    "sampleRate_kind" TEXT CHECK("sampleRate_kind" IN ('text','number')),
    "sampleRate_value" TEXT,
    "bitDepth_kind" TEXT CHECK("bitDepth_kind" IN ('text','number')),
    "bitDepth_value" TEXT,
    "fileType_kind" TEXT CHECK("fileType_kind" IN ('text','number')),
    "fileType_value" TEXT,
    "androidFileType_kind" TEXT CHECK("androidFileType_kind" IN ('text','number')),
    "androidFileType_value" TEXT,
    "iosFileType_kind" TEXT CHECK("iosFileType_kind" IN ('text','number')),
    "iosFileType_value" TEXT,
    "iosAccuracyLevel_kind" TEXT CHECK("iosAccuracyLevel_kind" IN ('text','number')),
    "iosAccuracyLevel_value" TEXT,
    "androidAccuracyLevel_kind" TEXT CHECK("androidAccuracyLevel_kind" IN ('text','number')),
    "androidAccuracyLevel_value" TEXT,
    "hash" TEXT,
    "md5" TEXT,
    "androidMd5" TEXT,
    "fileId_kind" TEXT CHECK("fileId_kind" IN ('text','number')),
    "fileId_value" TEXT,
    "contentId_kind" TEXT CHECK("contentId_kind" IN ('text','number')),
    "contentId_value" TEXT,
    CHECK(("formatType_kind" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("formatType_kind" IS NULL)=("formatType_value" IS NULL)),
    CHECK(("resourceType_kind" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("resourceType_kind" IS NULL)=("resourceType_value" IS NULL)),
    CHECK(("size_kind" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("size_kind" IS NULL)=("size_value" IS NULL)),
    CHECK(("androidSize_kind" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("androidSize_kind" IS NULL)=("androidSize_value" IS NULL)),
    CHECK(("iosSize_kind" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("iosSize_kind" IS NULL)=("iosSize_value" IS NULL)),
    CHECK(("asize_kind" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("asize_kind" IS NULL)=("asize_value" IS NULL)),
    CHECK(("isize_kind" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("isize_kind" IS NULL)=("isize_value" IS NULL)),
    CHECK(("format_kind" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("format_kind" IS NULL)=("format_value" IS NULL)),
    CHECK(("androidFormat_kind" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("androidFormat_kind" IS NULL)=("androidFormat_value" IS NULL)),
    CHECK(("iosFormat_kind" IS NULL)=((shape_mask & 512)=0 OR (null_mask & 512)<>0)),
    CHECK(("iosFormat_kind" IS NULL)=("iosFormat_value" IS NULL)),
    CHECK(("aformat_kind" IS NULL)=((shape_mask & 1024)=0 OR (null_mask & 1024)<>0)),
    CHECK(("aformat_kind" IS NULL)=("aformat_value" IS NULL)),
    CHECK(("iformat_kind" IS NULL)=((shape_mask & 2048)=0 OR (null_mask & 2048)<>0)),
    CHECK(("iformat_kind" IS NULL)=("iformat_value" IS NULL)),
    CHECK(("bitrate_kind" IS NULL)=((shape_mask & 4096)=0 OR (null_mask & 4096)<>0)),
    CHECK(("bitrate_kind" IS NULL)=("bitrate_value" IS NULL)),
    CHECK(("sampleRate_kind" IS NULL)=((shape_mask & 8192)=0 OR (null_mask & 8192)<>0)),
    CHECK(("sampleRate_kind" IS NULL)=("sampleRate_value" IS NULL)),
    CHECK(("bitDepth_kind" IS NULL)=((shape_mask & 16384)=0 OR (null_mask & 16384)<>0)),
    CHECK(("bitDepth_kind" IS NULL)=("bitDepth_value" IS NULL)),
    CHECK(("fileType_kind" IS NULL)=((shape_mask & 32768)=0 OR (null_mask & 32768)<>0)),
    CHECK(("fileType_kind" IS NULL)=("fileType_value" IS NULL)),
    CHECK(("androidFileType_kind" IS NULL)=((shape_mask & 65536)=0 OR (null_mask & 65536)<>0)),
    CHECK(("androidFileType_kind" IS NULL)=("androidFileType_value" IS NULL)),
    CHECK(("iosFileType_kind" IS NULL)=((shape_mask & 131072)=0 OR (null_mask & 131072)<>0)),
    CHECK(("iosFileType_kind" IS NULL)=("iosFileType_value" IS NULL)),
    CHECK(("iosAccuracyLevel_kind" IS NULL)=((shape_mask & 262144)=0 OR (null_mask & 262144)<>0)),
    CHECK(("iosAccuracyLevel_kind" IS NULL)=("iosAccuracyLevel_value" IS NULL)),
    CHECK(("androidAccuracyLevel_kind" IS NULL)=((shape_mask & 524288)=0 OR (null_mask & 524288)<>0)),
    CHECK(("androidAccuracyLevel_kind" IS NULL)=("androidAccuracyLevel_value" IS NULL)),
    CHECK(("hash" IS NULL)=((shape_mask & 1048576)=0 OR (null_mask & 1048576)<>0)),
    CHECK(("md5" IS NULL)=((shape_mask & 2097152)=0 OR (null_mask & 2097152)<>0)),
    CHECK(("androidMd5" IS NULL)=((shape_mask & 4194304)=0 OR (null_mask & 4194304)<>0)),
    CHECK(("fileId_kind" IS NULL)=((shape_mask & 8388608)=0 OR (null_mask & 8388608)<>0)),
    CHECK(("fileId_kind" IS NULL)=("fileId_value" IS NULL)),
    CHECK(("contentId_kind" IS NULL)=((shape_mask & 16777216)=0 OR (null_mask & 16777216)<>0)),
    CHECK(("contentId_kind" IS NULL)=("contentId_value" IS NULL)),
    PRIMARY KEY("song_id","sources_key","audioFormats_key"),
    FOREIGN KEY("song_id","sources_key") REFERENCES "music_source_info"("song_id","sources_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_source_info_newRateFormats" (
    "song_id" TEXT NOT NULL,
    "sources_key" TEXT NOT NULL,
    "newRateFormats_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 33554431),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "formatType_kind" TEXT CHECK("formatType_kind" IN ('text','number')),
    "formatType_value" TEXT,
    "resourceType_kind" TEXT CHECK("resourceType_kind" IN ('text','number')),
    "resourceType_value" TEXT,
    "size_kind" TEXT CHECK("size_kind" IN ('text','number')),
    "size_value" TEXT,
    "androidSize_kind" TEXT CHECK("androidSize_kind" IN ('text','number')),
    "androidSize_value" TEXT,
    "iosSize_kind" TEXT CHECK("iosSize_kind" IN ('text','number')),
    "iosSize_value" TEXT,
    "asize_kind" TEXT CHECK("asize_kind" IN ('text','number')),
    "asize_value" TEXT,
    "isize_kind" TEXT CHECK("isize_kind" IN ('text','number')),
    "isize_value" TEXT,
    "format_kind" TEXT CHECK("format_kind" IN ('text','number')),
    "format_value" TEXT,
    "androidFormat_kind" TEXT CHECK("androidFormat_kind" IN ('text','number')),
    "androidFormat_value" TEXT,
    "iosFormat_kind" TEXT CHECK("iosFormat_kind" IN ('text','number')),
    "iosFormat_value" TEXT,
    "aformat_kind" TEXT CHECK("aformat_kind" IN ('text','number')),
    "aformat_value" TEXT,
    "iformat_kind" TEXT CHECK("iformat_kind" IN ('text','number')),
    "iformat_value" TEXT,
    "bitrate_kind" TEXT CHECK("bitrate_kind" IN ('text','number')),
    "bitrate_value" TEXT,
    "sampleRate_kind" TEXT CHECK("sampleRate_kind" IN ('text','number')),
    "sampleRate_value" TEXT,
    "bitDepth_kind" TEXT CHECK("bitDepth_kind" IN ('text','number')),
    "bitDepth_value" TEXT,
    "fileType_kind" TEXT CHECK("fileType_kind" IN ('text','number')),
    "fileType_value" TEXT,
    "androidFileType_kind" TEXT CHECK("androidFileType_kind" IN ('text','number')),
    "androidFileType_value" TEXT,
    "iosFileType_kind" TEXT CHECK("iosFileType_kind" IN ('text','number')),
    "iosFileType_value" TEXT,
    "iosAccuracyLevel_kind" TEXT CHECK("iosAccuracyLevel_kind" IN ('text','number')),
    "iosAccuracyLevel_value" TEXT,
    "androidAccuracyLevel_kind" TEXT CHECK("androidAccuracyLevel_kind" IN ('text','number')),
    "androidAccuracyLevel_value" TEXT,
    "hash" TEXT,
    "md5" TEXT,
    "androidMd5" TEXT,
    "fileId_kind" TEXT CHECK("fileId_kind" IN ('text','number')),
    "fileId_value" TEXT,
    "contentId_kind" TEXT CHECK("contentId_kind" IN ('text','number')),
    "contentId_value" TEXT,
    CHECK(("formatType_kind" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("formatType_kind" IS NULL)=("formatType_value" IS NULL)),
    CHECK(("resourceType_kind" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("resourceType_kind" IS NULL)=("resourceType_value" IS NULL)),
    CHECK(("size_kind" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("size_kind" IS NULL)=("size_value" IS NULL)),
    CHECK(("androidSize_kind" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("androidSize_kind" IS NULL)=("androidSize_value" IS NULL)),
    CHECK(("iosSize_kind" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("iosSize_kind" IS NULL)=("iosSize_value" IS NULL)),
    CHECK(("asize_kind" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("asize_kind" IS NULL)=("asize_value" IS NULL)),
    CHECK(("isize_kind" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("isize_kind" IS NULL)=("isize_value" IS NULL)),
    CHECK(("format_kind" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("format_kind" IS NULL)=("format_value" IS NULL)),
    CHECK(("androidFormat_kind" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("androidFormat_kind" IS NULL)=("androidFormat_value" IS NULL)),
    CHECK(("iosFormat_kind" IS NULL)=((shape_mask & 512)=0 OR (null_mask & 512)<>0)),
    CHECK(("iosFormat_kind" IS NULL)=("iosFormat_value" IS NULL)),
    CHECK(("aformat_kind" IS NULL)=((shape_mask & 1024)=0 OR (null_mask & 1024)<>0)),
    CHECK(("aformat_kind" IS NULL)=("aformat_value" IS NULL)),
    CHECK(("iformat_kind" IS NULL)=((shape_mask & 2048)=0 OR (null_mask & 2048)<>0)),
    CHECK(("iformat_kind" IS NULL)=("iformat_value" IS NULL)),
    CHECK(("bitrate_kind" IS NULL)=((shape_mask & 4096)=0 OR (null_mask & 4096)<>0)),
    CHECK(("bitrate_kind" IS NULL)=("bitrate_value" IS NULL)),
    CHECK(("sampleRate_kind" IS NULL)=((shape_mask & 8192)=0 OR (null_mask & 8192)<>0)),
    CHECK(("sampleRate_kind" IS NULL)=("sampleRate_value" IS NULL)),
    CHECK(("bitDepth_kind" IS NULL)=((shape_mask & 16384)=0 OR (null_mask & 16384)<>0)),
    CHECK(("bitDepth_kind" IS NULL)=("bitDepth_value" IS NULL)),
    CHECK(("fileType_kind" IS NULL)=((shape_mask & 32768)=0 OR (null_mask & 32768)<>0)),
    CHECK(("fileType_kind" IS NULL)=("fileType_value" IS NULL)),
    CHECK(("androidFileType_kind" IS NULL)=((shape_mask & 65536)=0 OR (null_mask & 65536)<>0)),
    CHECK(("androidFileType_kind" IS NULL)=("androidFileType_value" IS NULL)),
    CHECK(("iosFileType_kind" IS NULL)=((shape_mask & 131072)=0 OR (null_mask & 131072)<>0)),
    CHECK(("iosFileType_kind" IS NULL)=("iosFileType_value" IS NULL)),
    CHECK(("iosAccuracyLevel_kind" IS NULL)=((shape_mask & 262144)=0 OR (null_mask & 262144)<>0)),
    CHECK(("iosAccuracyLevel_kind" IS NULL)=("iosAccuracyLevel_value" IS NULL)),
    CHECK(("androidAccuracyLevel_kind" IS NULL)=((shape_mask & 524288)=0 OR (null_mask & 524288)<>0)),
    CHECK(("androidAccuracyLevel_kind" IS NULL)=("androidAccuracyLevel_value" IS NULL)),
    CHECK(("hash" IS NULL)=((shape_mask & 1048576)=0 OR (null_mask & 1048576)<>0)),
    CHECK(("md5" IS NULL)=((shape_mask & 2097152)=0 OR (null_mask & 2097152)<>0)),
    CHECK(("androidMd5" IS NULL)=((shape_mask & 4194304)=0 OR (null_mask & 4194304)<>0)),
    CHECK(("fileId_kind" IS NULL)=((shape_mask & 8388608)=0 OR (null_mask & 8388608)<>0)),
    CHECK(("fileId_kind" IS NULL)=("fileId_value" IS NULL)),
    CHECK(("contentId_kind" IS NULL)=((shape_mask & 16777216)=0 OR (null_mask & 16777216)<>0)),
    CHECK(("contentId_kind" IS NULL)=("contentId_value" IS NULL)),
    PRIMARY KEY("song_id","sources_key","newRateFormats_key"),
    FOREIGN KEY("song_id","sources_key") REFERENCES "music_source_info"("song_id","sources_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_library" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 31),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "schema" INTEGER,
    "scope" TEXT,
    "revision" INTEGER,
    CHECK(("schema" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("scope" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("revision" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "music_library_library" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 511),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "version" INTEGER,
    "ownerId" TEXT,
    "revision" INTEGER,
    "nextId" INTEGER,
    CHECK(("version" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("ownerId" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("revision" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("nextId" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    PRIMARY KEY("scope_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_library"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_collections" (
    "scope_key" TEXT NOT NULL,
    "collections_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 511),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "name" TEXT,
    "kind" TEXT,
    "revision" INTEGER,
    "manualOrder" INTEGER CHECK("manualOrder" IN (0,1)),
    "deleted" INTEGER CHECK("deleted" IN (0,1)),
    "locallyCreated" INTEGER CHECK("locallyCreated" IN (0,1)),
    CHECK(("name" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("kind" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("revision" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("manualOrder" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("deleted" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("locallyCreated" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    PRIMARY KEY("scope_key","collections_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_library_library"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_collection_members" (
    "scope_key" TEXT NOT NULL,
    "collections_key" TEXT NOT NULL,
    "order_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    FOREIGN KEY(value) REFERENCES music_songs(song_id),
    PRIMARY KEY("scope_key","collections_key","order_key"),
    FOREIGN KEY("scope_key","collections_key") REFERENCES "music_collections"("scope_key","collections_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_collections_bindings" (
    "scope_key" TEXT NOT NULL,
    "collections_key" TEXT NOT NULL,
    "bindings_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 127),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "platform" TEXT,
    "accountId" TEXT,
    "id" TEXT,
    "writable" INTEGER CHECK("writable" IN (0,1)),
    "name" TEXT,
    CHECK(("platform" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("accountId" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("id" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("writable" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("name" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    PRIMARY KEY("scope_key","collections_key","bindings_key"),
    FOREIGN KEY("scope_key","collections_key") REFERENCES "music_collections"("scope_key","collections_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_collections_bindings_baseline" (
    "scope_key" TEXT NOT NULL,
    "collections_key" TEXT NOT NULL,
    "bindings_key" TEXT NOT NULL,
    "baseline_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","collections_key","bindings_key","baseline_key"),
    FOREIGN KEY("scope_key","collections_key","bindings_key") REFERENCES "music_collections_bindings"("scope_key","collections_key","bindings_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_collections_bindings_map" (
    "scope_key" TEXT NOT NULL,
    "collections_key" TEXT NOT NULL,
    "bindings_key" TEXT NOT NULL,
    "map_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    FOREIGN KEY(value) REFERENCES music_songs(song_id),
    PRIMARY KEY("scope_key","collections_key","bindings_key","map_key"),
    FOREIGN KEY("scope_key","collections_key","bindings_key") REFERENCES "music_collections_bindings"("scope_key","collections_key","bindings_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_collection_intents" (
    "scope_key" TEXT NOT NULL,
    "collections_key" TEXT NOT NULL,
    "intents_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 15),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "present" INTEGER CHECK("present" IN (0,1)),
    "pending" INTEGER CHECK("pending" IN (0,1)),
    "origin" TEXT,
    CHECK(("present" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("pending" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("origin" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    FOREIGN KEY(intents_key) REFERENCES music_songs(song_id),
    PRIMARY KEY("scope_key","collections_key","intents_key"),
    FOREIGN KEY("scope_key","collections_key") REFERENCES "music_collections"("scope_key","collections_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_collection_intents_acknowledged" (
    "scope_key" TEXT NOT NULL,
    "collections_key" TEXT NOT NULL,
    "intents_key" TEXT NOT NULL,
    "acknowledged_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","collections_key","intents_key","acknowledged_key"),
    FOREIGN KEY("scope_key","collections_key","intents_key") REFERENCES "music_collection_intents"("scope_key","collections_key","intents_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_library_library_accounts" (
    "scope_key" TEXT NOT NULL,
    "accounts_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","accounts_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_library_library"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_library_library_status" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 15),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "phase" TEXT,
    "at" REAL,
    "code" TEXT,
    CHECK(("phase" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("at" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("code" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    PRIMARY KEY("scope_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_library_library"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_library_library_status_issues" (
    "scope_key" TEXT NOT NULL,
    "issues_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 15),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "platform" TEXT,
    "collectionID" TEXT,
    "collectionId_camel" TEXT,
    "code" TEXT,
    CHECK(("platform" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("collectionID" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("collectionId_camel" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("code" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    PRIMARY KEY("scope_key","issues_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_library_library_status"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_library_library_networkReferences" (
    "scope_key" TEXT NOT NULL,
    "networkReferences_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 4095),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "platform" TEXT,
    "remoteID" TEXT,
    "remoteId_camel" TEXT,
    "name" TEXT,
    "creator" TEXT,
    "artwork" TEXT,
    "cover" TEXT,
    "count" INTEGER,
    "revision" INTEGER,
    "present" INTEGER CHECK("present" IN (0,1)),
    "pending" INTEGER CHECK("pending" IN (0,1)),
    CHECK(("platform" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("remoteID" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("remoteId_camel" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("name" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("creator" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("artwork" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("cover" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("count" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("revision" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("present" IS NULL)=((shape_mask & 512)=0 OR (null_mask & 512)<>0)),
    CHECK(("pending" IS NULL)=((shape_mask & 1024)=0 OR (null_mask & 1024)<>0)),
    PRIMARY KEY("scope_key","networkReferences_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_library_library"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_library_library_networkReferences_relation" (
    "scope_key" TEXT NOT NULL,
    "networkReferences_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 15),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "accountId" TEXT,
    "baseline" INTEGER CHECK("baseline" IN (0,1)),
    "relationID" TEXT,
    "locator" TEXT,
    CHECK(("accountId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("baseline" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("relationID" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("locator" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    PRIMARY KEY("scope_key","networkReferences_key"),
    FOREIGN KEY("scope_key","networkReferences_key") REFERENCES "music_library_library_networkReferences"("scope_key","networkReferences_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_library_library_networkPlaylists" (
    "scope_key" TEXT NOT NULL,
    "networkPlaylists_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 511),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "platform" TEXT,
    "remoteId" TEXT,
    "name" TEXT,
    "cover" TEXT,
    "count" INTEGER,
    "revision" INTEGER,
    "present" INTEGER CHECK("present" IN (0,1)),
    CHECK(("platform" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("remoteId" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("name" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("cover" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("count" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("revision" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("present" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    PRIMARY KEY("scope_key","networkPlaylists_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_library_library"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_library_library_networkPlaylists_subscription" (
    "scope_key" TEXT NOT NULL,
    "networkPlaylists_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "accountId" TEXT,
    "present" INTEGER CHECK("present" IN (0,1)),
    CHECK(("accountId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("present" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    PRIMARY KEY("scope_key","networkPlaylists_key"),
    FOREIGN KEY("scope_key","networkPlaylists_key") REFERENCES "music_library_library_networkPlaylists"("scope_key","networkPlaylists_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_library_library_networkPlaylists_subscription_locator" (
    "scope_key" TEXT NOT NULL,
    "networkPlaylists_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "listId" TEXT,
    "creatorId" TEXT,
    "creatorListId" TEXT,
    CHECK(("listId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("creatorId" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("creatorListId" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    PRIMARY KEY("scope_key","networkPlaylists_key"),
    FOREIGN KEY("scope_key","networkPlaylists_key") REFERENCES "music_library_library_networkPlaylists_subscription"("scope_key","networkPlaylists_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_library_library_networkPlaylists_intent" (
    "scope_key" TEXT NOT NULL,
    "networkPlaylists_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "accountId" TEXT,
    "present" INTEGER CHECK("present" IN (0,1)),
    CHECK(("accountId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("present" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    PRIMARY KEY("scope_key","networkPlaylists_key"),
    FOREIGN KEY("scope_key","networkPlaylists_key") REFERENCES "music_library_library_networkPlaylists"("scope_key","networkPlaylists_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_library_sync" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 127),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "schema" INTEGER,
    "phase" TEXT,
    CHECK(("schema" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("phase" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    PRIMARY KEY("scope_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_library"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_library_sync_selections" (
    "scope_key" TEXT NOT NULL,
    "selections_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "platform" TEXT,
    "favorites" INTEGER CHECK("favorites" IN (0,1)),
    "playlists" INTEGER CHECK("playlists" IN (0,1)),
    CHECK(("platform" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("favorites" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("playlists" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    PRIMARY KEY("scope_key","selections_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_library_sync"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_library_sync_scopes" (
    "scope_key" TEXT NOT NULL,
    "scopes_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","scopes_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_library_sync"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_library_sync_issues" (
    "scope_key" TEXT NOT NULL,
    "issues_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "platform" TEXT,
    "collectionID" TEXT,
    "code" TEXT,
    CHECK(("platform" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("collectionID" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("code" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    PRIMARY KEY("scope_key","issues_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_library_sync"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_library_sync_automatic" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 127),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "revision" INTEGER,
    "intentRevision" INTEGER,
    "settledIntentRevision" INTEGER,
    "orderOnly" TEXT,
    CHECK(("revision" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("intentRevision" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("settledIntentRevision" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("orderOnly" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    PRIMARY KEY("scope_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_library_sync"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_library_sync_automatic_selections" (
    "scope_key" TEXT NOT NULL,
    "selections_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "platform" TEXT,
    "favorites" INTEGER CHECK("favorites" IN (0,1)),
    "playlists" INTEGER CHECK("playlists" IN (0,1)),
    CHECK(("platform" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("favorites" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("playlists" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    PRIMARY KEY("scope_key","selections_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_library_sync_automatic"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_library_sync_automatic_bindings" (
    "scope_key" TEXT NOT NULL,
    "bindings_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "accountID" TEXT,
    "scope" TEXT,
    CHECK(("accountID" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("scope" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    PRIMARY KEY("scope_key","bindings_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_library_sync_automatic"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_library_sync_automatic_relationshipPlatforms" (
    "scope_key" TEXT NOT NULL,
    "relationshipPlatforms_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","relationshipPlatforms_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_library_sync_automatic"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_library_sync_fences" (
    "scope_key" TEXT NOT NULL,
    "fences_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 255),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "platform" TEXT,
    "accountID" TEXT,
    "scope" TEXT,
    "localID" TEXT,
    "localRevision" INTEGER,
    "acknowledgedID" TEXT,
    "acknowledgedName" TEXT,
    CHECK(("platform" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("accountID" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("scope" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("localID" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("localRevision" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("acknowledgedID" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("acknowledgedName" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    PRIMARY KEY("scope_key","fences_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_library_sync"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_library_sync_fences_mutation" (
    "scope_key" TEXT NOT NULL,
    "fences_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 255),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "id" TEXT,
    "action" TEXT,
    "collectionID" TEXT,
    "name" TEXT,
    "directoryRevision" TEXT,
    CHECK(("id" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("action" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("collectionID" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("name" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("directoryRevision" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    PRIMARY KEY("scope_key","fences_key"),
    FOREIGN KEY("scope_key","fences_key") REFERENCES "music_library_sync_fences"("scope_key","fences_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_library_sync_fences_mutation_members" (
    "scope_key" TEXT NOT NULL,
    "fences_key" TEXT NOT NULL,
    "members_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "nativeID" TEXT,
    "occurrenceID" TEXT,
    CHECK(("nativeID" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("occurrenceID" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    PRIMARY KEY("scope_key","fences_key","members_key"),
    FOREIGN KEY("scope_key","fences_key") REFERENCES "music_library_sync_fences_mutation"("scope_key","fences_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_library_sync_fences_mutation_before" (
    "scope_key" TEXT NOT NULL,
    "fences_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 511),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "id" TEXT,
    "name" TEXT,
    "kind" TEXT,
    "writable" INTEGER CHECK("writable" IN (0,1)),
    "canRename" INTEGER CHECK("canRename" IN (0,1)),
    "canDelete" INTEGER CHECK("canDelete" IN (0,1)),
    "canOrder" INTEGER CHECK("canOrder" IN (0,1)),
    "revision" TEXT,
    CHECK(("id" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("name" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("kind" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("writable" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("canRename" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("canDelete" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("canOrder" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("revision" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    PRIMARY KEY("scope_key","fences_key"),
    FOREIGN KEY("scope_key","fences_key") REFERENCES "music_library_sync_fences_mutation"("scope_key","fences_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_library_sync_fences_mutation_before_members" (
    "scope_key" TEXT NOT NULL,
    "fences_key" TEXT NOT NULL,
    "members_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "nativeID" TEXT,
    "occurrenceID" TEXT,
    CHECK(("nativeID" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("occurrenceID" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    PRIMARY KEY("scope_key","fences_key","members_key"),
    FOREIGN KEY("scope_key","fences_key") REFERENCES "music_library_sync_fences_mutation_before"("scope_key","fences_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_library_sync_fences_mutation_beforeSubscription" (
    "scope_key" TEXT NOT NULL,
    "fences_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 63),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "remoteID" TEXT,
    "relationID" TEXT,
    "name" TEXT,
    "creator" TEXT,
    "artwork" TEXT,
    "count" INTEGER,
    CHECK(("remoteID" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("relationID" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("name" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("creator" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("artwork" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("count" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    PRIMARY KEY("scope_key","fences_key"),
    FOREIGN KEY("scope_key","fences_key") REFERENCES "music_library_sync_fences_mutation"("scope_key","fences_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_queue" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 2047),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "schema" INTEGER,
    "currentId" TEXT,
    "serial" INTEGER,
    "revision" INTEGER,
    "runId" TEXT,
    CHECK(("schema" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("currentId" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("serial" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("revision" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("runId" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "music_queue_entries" (
    "scope_key" TEXT NOT NULL,
    "entries_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "songId" TEXT,
    CHECK(("songId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    FOREIGN KEY("songId") REFERENCES music_songs(song_id),
    PRIMARY KEY("scope_key","entries_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_queue"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_queue_entries_context" (
    "scope_key" TEXT NOT NULL,
    "entries_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 131071),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "collectionId" TEXT,
    "playlistId" TEXT,
    "sourceTitle" TEXT,
    "playlist" TEXT,
    "sourceType" TEXT,
    "sourceId" TEXT,
    "sourcePlatform" TEXT,
    "platform" TEXT,
    "sourceLabel" TEXT,
    "searchKeyword" TEXT,
    "dailyBranch" TEXT,
    "heartbeatPlaylistId" TEXT,
    "heartbeatAccountId" TEXT,
    "provider" TEXT,
    "remoteSession" TEXT,
    CHECK(("collectionId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("playlistId" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("sourceTitle" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("playlist" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("sourceType" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("sourceId" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("sourcePlatform" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("platform" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("sourceLabel" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("searchKeyword" IS NULL)=((shape_mask & 512)=0 OR (null_mask & 512)<>0)),
    CHECK(("dailyBranch" IS NULL)=((shape_mask & 1024)=0 OR (null_mask & 1024)<>0)),
    CHECK(("heartbeatPlaylistId" IS NULL)=((shape_mask & 2048)=0 OR (null_mask & 2048)<>0)),
    CHECK(("heartbeatAccountId" IS NULL)=((shape_mask & 4096)=0 OR (null_mask & 4096)<>0)),
    CHECK(("provider" IS NULL)=((shape_mask & 8192)=0 OR (null_mask & 8192)<>0)),
    CHECK(("remoteSession" IS NULL)=((shape_mask & 16384)=0 OR (null_mask & 16384)<>0)),
    PRIMARY KEY("scope_key","entries_key"),
    FOREIGN KEY("scope_key","entries_key") REFERENCES "music_queue_entries"("scope_key","entries_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_queue_entries_context_queueSource" (
    "scope_key" TEXT NOT NULL,
    "entries_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 15),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "id" TEXT,
    "title" TEXT,
    "kind" TEXT,
    "platform" TEXT,
    CHECK(("id" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("title" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("kind" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("platform" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    PRIMARY KEY("scope_key","entries_key"),
    FOREIGN KEY("scope_key","entries_key") REFERENCES "music_queue_entries_context"("scope_key","entries_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_queue_entries_context_similar" (
    "scope_key" TEXT NOT NULL,
    "entries_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "batchId" TEXT,
    "basedOnSongId" TEXT,
    "provider" TEXT,
    CHECK(("batchId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("basedOnSongId" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("provider" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    FOREIGN KEY("basedOnSongId") REFERENCES music_songs(song_id),
    PRIMARY KEY("scope_key","entries_key"),
    FOREIGN KEY("scope_key","entries_key") REFERENCES "music_queue_entries_context"("scope_key","entries_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_queue_priority" (
    "scope_key" TEXT NOT NULL,
    "priority_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","priority_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_queue"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_queue_history" (
    "scope_key" TEXT NOT NULL,
    "history_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","history_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_queue"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_queue_source" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 4095),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "id" TEXT,
    "title" TEXT,
    "kind" TEXT,
    "platform" TEXT,
    "sourceType" TEXT,
    "sourceTitle" TEXT,
    "sourceId" TEXT,
    "playlist" TEXT,
    "seedEntryId" TEXT,
    "remoteSession" TEXT,
    "dailyBranch" TEXT,
    "searchKeyword" TEXT,
    CHECK(("id" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("title" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("kind" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("platform" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("sourceType" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("sourceTitle" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("sourceId" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("playlist" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("seedEntryId" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("remoteSession" IS NULL)=((shape_mask & 512)=0 OR (null_mask & 512)<>0)),
    CHECK(("dailyBranch" IS NULL)=((shape_mask & 1024)=0 OR (null_mask & 1024)<>0)),
    CHECK(("searchKeyword" IS NULL)=((shape_mask & 2048)=0 OR (null_mask & 2048)<>0)),
    PRIMARY KEY("scope_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_queue"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_queue_failed" (
    "scope_key" TEXT NOT NULL,
    "failed_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","failed_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_queue"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_queue_similar" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 65535),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "schema" INTEGER,
    "seedSongId" TEXT,
    "seedTitle" TEXT,
    "seedEntryId" TEXT,
    "fromHome" INTEGER CHECK("fromHome" IN (0,1)),
    "provider" TEXT,
    "continue" INTEGER CHECK("continue" IN (0,1)),
    "offset" INTEGER,
    "exhausted" INTEGER CHECK("exhausted" IN (0,1)),
    "scope" TEXT,
    "status" TEXT,
    CHECK(("schema" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("seedSongId" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("seedTitle" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("seedEntryId" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("fromHome" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("provider" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("continue" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("offset" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("exhausted" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("scope" IS NULL)=((shape_mask & 512)=0 OR (null_mask & 512)<>0)),
    CHECK(("status" IS NULL)=((shape_mask & 1024)=0 OR (null_mask & 1024)<>0)),
    FOREIGN KEY("seedSongId") REFERENCES music_songs(song_id),
    PRIMARY KEY("scope_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_queue"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_queue_similar_returnQueue" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 31),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "currentId" TEXT,
    CHECK(("currentId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_queue_similar"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_queue_similar_returnQueue_entries" (
    "scope_key" TEXT NOT NULL,
    "entries_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "songId" TEXT,
    CHECK(("songId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    FOREIGN KEY("songId") REFERENCES music_songs(song_id),
    PRIMARY KEY("scope_key","entries_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_queue_similar_returnQueue"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_queue_similar_returnQueue_entries_context" (
    "scope_key" TEXT NOT NULL,
    "entries_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 131071),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "collectionId" TEXT,
    "playlistId" TEXT,
    "sourceTitle" TEXT,
    "playlist" TEXT,
    "sourceType" TEXT,
    "sourceId" TEXT,
    "sourcePlatform" TEXT,
    "platform" TEXT,
    "sourceLabel" TEXT,
    "searchKeyword" TEXT,
    "dailyBranch" TEXT,
    "heartbeatPlaylistId" TEXT,
    "heartbeatAccountId" TEXT,
    "provider" TEXT,
    "remoteSession" TEXT,
    CHECK(("collectionId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("playlistId" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("sourceTitle" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("playlist" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("sourceType" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("sourceId" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("sourcePlatform" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("platform" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("sourceLabel" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("searchKeyword" IS NULL)=((shape_mask & 512)=0 OR (null_mask & 512)<>0)),
    CHECK(("dailyBranch" IS NULL)=((shape_mask & 1024)=0 OR (null_mask & 1024)<>0)),
    CHECK(("heartbeatPlaylistId" IS NULL)=((shape_mask & 2048)=0 OR (null_mask & 2048)<>0)),
    CHECK(("heartbeatAccountId" IS NULL)=((shape_mask & 4096)=0 OR (null_mask & 4096)<>0)),
    CHECK(("provider" IS NULL)=((shape_mask & 8192)=0 OR (null_mask & 8192)<>0)),
    CHECK(("remoteSession" IS NULL)=((shape_mask & 16384)=0 OR (null_mask & 16384)<>0)),
    PRIMARY KEY("scope_key","entries_key"),
    FOREIGN KEY("scope_key","entries_key") REFERENCES "music_queue_similar_returnQueue_entries"("scope_key","entries_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_queue_similar_returnQueue_entries_context_queueSource" (
    "scope_key" TEXT NOT NULL,
    "entries_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 15),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "id" TEXT,
    "title" TEXT,
    "kind" TEXT,
    "platform" TEXT,
    CHECK(("id" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("title" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("kind" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("platform" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    PRIMARY KEY("scope_key","entries_key"),
    FOREIGN KEY("scope_key","entries_key") REFERENCES "music_queue_similar_returnQueue_entries_context"("scope_key","entries_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_queue_similar_returnQueue_entries_context_similar" (
    "scope_key" TEXT NOT NULL,
    "entries_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "batchId" TEXT,
    "basedOnSongId" TEXT,
    "provider" TEXT,
    CHECK(("batchId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("basedOnSongId" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("provider" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    FOREIGN KEY("basedOnSongId") REFERENCES music_songs(song_id),
    PRIMARY KEY("scope_key","entries_key"),
    FOREIGN KEY("scope_key","entries_key") REFERENCES "music_queue_similar_returnQueue_entries_context"("scope_key","entries_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_queue_similar_returnQueue_priority" (
    "scope_key" TEXT NOT NULL,
    "priority_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","priority_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_queue_similar_returnQueue"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_queue_similar_returnQueue_history" (
    "scope_key" TEXT NOT NULL,
    "history_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","history_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_queue_similar_returnQueue"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_queue_similar_returnQueue_source" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 4095),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "id" TEXT,
    "title" TEXT,
    "kind" TEXT,
    "platform" TEXT,
    "sourceType" TEXT,
    "sourceTitle" TEXT,
    "sourceId" TEXT,
    "playlist" TEXT,
    "seedEntryId" TEXT,
    "remoteSession" TEXT,
    "dailyBranch" TEXT,
    "searchKeyword" TEXT,
    CHECK(("id" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("title" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("kind" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("platform" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("sourceType" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("sourceTitle" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("sourceId" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("playlist" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("seedEntryId" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("remoteSession" IS NULL)=((shape_mask & 512)=0 OR (null_mask & 512)<>0)),
    CHECK(("dailyBranch" IS NULL)=((shape_mask & 1024)=0 OR (null_mask & 1024)<>0)),
    CHECK(("searchKeyword" IS NULL)=((shape_mask & 2048)=0 OR (null_mask & 2048)<>0)),
    PRIMARY KEY("scope_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_queue_similar_returnQueue"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_queue_similar_sourceIds" (
    "scope_key" TEXT NOT NULL,
    "sourceIds_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","sourceIds_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_queue_similar"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_queue_similar_attempted" (
    "scope_key" TEXT NOT NULL,
    "attempted_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","attempted_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_queue_similar"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_queue_similar_providerContext" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "pid_kind" TEXT CHECK("pid_kind" IN ('text','number')),
    "pid_value" TEXT,
    "sid_kind" TEXT CHECK("sid_kind" IN ('text','number')),
    "sid_value" TEXT,
    "count" INTEGER,
    CHECK(("pid_kind" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("pid_kind" IS NULL)=("pid_value" IS NULL)),
    CHECK(("sid_kind" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("sid_kind" IS NULL)=("sid_value" IS NULL)),
    CHECK(("count" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    PRIMARY KEY("scope_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_queue_similar"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_queue_similar_batches" (
    "scope_key" TEXT NOT NULL,
    "batches_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 15),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "basedOnSongId" TEXT,
    "basedOnTitle" TEXT,
    "provider" TEXT,
    CHECK(("basedOnSongId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("basedOnTitle" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("provider" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    FOREIGN KEY("basedOnSongId") REFERENCES music_songs(song_id),
    PRIMARY KEY("scope_key","batches_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_queue_similar"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_queue_similar_batches_entryIds" (
    "scope_key" TEXT NOT NULL,
    "batches_key" TEXT NOT NULL,
    "entryIds_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","batches_key","entryIds_key"),
    FOREIGN KEY("scope_key","batches_key") REFERENCES "music_queue_similar_batches"("scope_key","batches_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_history" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "schema" INTEGER,
    CHECK(("schema" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "music_history_playlists" (
    "scope_key" TEXT NOT NULL,
    "playlists_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 255),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "at" REAL,
    "title" TEXT,
    "currentId" TEXT,
    CHECK(("at" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("title" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("currentId" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    PRIMARY KEY("scope_key","playlists_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_history"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_history_playlists_entries" (
    "scope_key" TEXT NOT NULL,
    "playlists_key" TEXT NOT NULL,
    "entries_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "songId" TEXT,
    CHECK(("songId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    FOREIGN KEY("songId") REFERENCES music_songs(song_id),
    PRIMARY KEY("scope_key","playlists_key","entries_key"),
    FOREIGN KEY("scope_key","playlists_key") REFERENCES "music_history_playlists"("scope_key","playlists_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_history_playlists_entries_context" (
    "scope_key" TEXT NOT NULL,
    "playlists_key" TEXT NOT NULL,
    "entries_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 131071),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "collectionId" TEXT,
    "playlistId" TEXT,
    "sourceTitle" TEXT,
    "playlist" TEXT,
    "sourceType" TEXT,
    "sourceId" TEXT,
    "sourcePlatform" TEXT,
    "platform" TEXT,
    "sourceLabel" TEXT,
    "searchKeyword" TEXT,
    "dailyBranch" TEXT,
    "heartbeatPlaylistId" TEXT,
    "heartbeatAccountId" TEXT,
    "provider" TEXT,
    "remoteSession" TEXT,
    CHECK(("collectionId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("playlistId" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("sourceTitle" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("playlist" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("sourceType" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("sourceId" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("sourcePlatform" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("platform" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("sourceLabel" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("searchKeyword" IS NULL)=((shape_mask & 512)=0 OR (null_mask & 512)<>0)),
    CHECK(("dailyBranch" IS NULL)=((shape_mask & 1024)=0 OR (null_mask & 1024)<>0)),
    CHECK(("heartbeatPlaylistId" IS NULL)=((shape_mask & 2048)=0 OR (null_mask & 2048)<>0)),
    CHECK(("heartbeatAccountId" IS NULL)=((shape_mask & 4096)=0 OR (null_mask & 4096)<>0)),
    CHECK(("provider" IS NULL)=((shape_mask & 8192)=0 OR (null_mask & 8192)<>0)),
    CHECK(("remoteSession" IS NULL)=((shape_mask & 16384)=0 OR (null_mask & 16384)<>0)),
    PRIMARY KEY("scope_key","playlists_key","entries_key"),
    FOREIGN KEY("scope_key","playlists_key","entries_key") REFERENCES "music_history_playlists_entries"("scope_key","playlists_key","entries_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_history_playlists_entries_context_queueSource" (
    "scope_key" TEXT NOT NULL,
    "playlists_key" TEXT NOT NULL,
    "entries_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 15),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "id" TEXT,
    "title" TEXT,
    "kind" TEXT,
    "platform" TEXT,
    CHECK(("id" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("title" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("kind" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("platform" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    PRIMARY KEY("scope_key","playlists_key","entries_key"),
    FOREIGN KEY("scope_key","playlists_key","entries_key") REFERENCES "music_history_playlists_entries_context"("scope_key","playlists_key","entries_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_history_playlists_entries_context_similar" (
    "scope_key" TEXT NOT NULL,
    "playlists_key" TEXT NOT NULL,
    "entries_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "batchId" TEXT,
    "basedOnSongId" TEXT,
    "provider" TEXT,
    CHECK(("batchId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("basedOnSongId" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("provider" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    FOREIGN KEY("basedOnSongId") REFERENCES music_songs(song_id),
    PRIMARY KEY("scope_key","playlists_key","entries_key"),
    FOREIGN KEY("scope_key","playlists_key","entries_key") REFERENCES "music_history_playlists_entries_context"("scope_key","playlists_key","entries_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_history_playlists_priority" (
    "scope_key" TEXT NOT NULL,
    "playlists_key" TEXT NOT NULL,
    "priority_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","playlists_key","priority_key"),
    FOREIGN KEY("scope_key","playlists_key") REFERENCES "music_history_playlists"("scope_key","playlists_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_history_playlists_history" (
    "scope_key" TEXT NOT NULL,
    "playlists_key" TEXT NOT NULL,
    "history_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","playlists_key","history_key"),
    FOREIGN KEY("scope_key","playlists_key") REFERENCES "music_history_playlists"("scope_key","playlists_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_history_playlists_source" (
    "scope_key" TEXT NOT NULL,
    "playlists_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 4095),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "id" TEXT,
    "title" TEXT,
    "kind" TEXT,
    "platform" TEXT,
    "sourceType" TEXT,
    "sourceTitle" TEXT,
    "sourceId" TEXT,
    "playlist" TEXT,
    "seedEntryId" TEXT,
    "remoteSession" TEXT,
    "dailyBranch" TEXT,
    "searchKeyword" TEXT,
    CHECK(("id" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("title" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("kind" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("platform" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("sourceType" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("sourceTitle" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("sourceId" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("playlist" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("seedEntryId" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("remoteSession" IS NULL)=((shape_mask & 512)=0 OR (null_mask & 512)<>0)),
    CHECK(("dailyBranch" IS NULL)=((shape_mask & 1024)=0 OR (null_mask & 1024)<>0)),
    CHECK(("searchKeyword" IS NULL)=((shape_mask & 2048)=0 OR (null_mask & 2048)<>0)),
    PRIMARY KEY("scope_key","playlists_key"),
    FOREIGN KEY("scope_key","playlists_key") REFERENCES "music_history_playlists"("scope_key","playlists_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_history_playlists_context" (
    "scope_key" TEXT NOT NULL,
    "playlists_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 131071),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "collectionId" TEXT,
    "playlistId" TEXT,
    "sourceTitle" TEXT,
    "playlist" TEXT,
    "sourceType" TEXT,
    "sourceId" TEXT,
    "sourcePlatform" TEXT,
    "platform" TEXT,
    "sourceLabel" TEXT,
    "searchKeyword" TEXT,
    "dailyBranch" TEXT,
    "heartbeatPlaylistId" TEXT,
    "heartbeatAccountId" TEXT,
    "provider" TEXT,
    "remoteSession" TEXT,
    CHECK(("collectionId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("playlistId" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("sourceTitle" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("playlist" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("sourceType" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("sourceId" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("sourcePlatform" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("platform" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("sourceLabel" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("searchKeyword" IS NULL)=((shape_mask & 512)=0 OR (null_mask & 512)<>0)),
    CHECK(("dailyBranch" IS NULL)=((shape_mask & 1024)=0 OR (null_mask & 1024)<>0)),
    CHECK(("heartbeatPlaylistId" IS NULL)=((shape_mask & 2048)=0 OR (null_mask & 2048)<>0)),
    CHECK(("heartbeatAccountId" IS NULL)=((shape_mask & 4096)=0 OR (null_mask & 4096)<>0)),
    CHECK(("provider" IS NULL)=((shape_mask & 8192)=0 OR (null_mask & 8192)<>0)),
    CHECK(("remoteSession" IS NULL)=((shape_mask & 16384)=0 OR (null_mask & 16384)<>0)),
    PRIMARY KEY("scope_key","playlists_key"),
    FOREIGN KEY("scope_key","playlists_key") REFERENCES "music_history_playlists"("scope_key","playlists_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_history_playlists_context_queueSource" (
    "scope_key" TEXT NOT NULL,
    "playlists_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 15),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "id" TEXT,
    "title" TEXT,
    "kind" TEXT,
    "platform" TEXT,
    CHECK(("id" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("title" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("kind" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("platform" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    PRIMARY KEY("scope_key","playlists_key"),
    FOREIGN KEY("scope_key","playlists_key") REFERENCES "music_history_playlists_context"("scope_key","playlists_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_history_playlists_context_similar" (
    "scope_key" TEXT NOT NULL,
    "playlists_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "batchId" TEXT,
    "basedOnSongId" TEXT,
    "provider" TEXT,
    CHECK(("batchId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("basedOnSongId" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("provider" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    FOREIGN KEY("basedOnSongId") REFERENCES music_songs(song_id),
    PRIMARY KEY("scope_key","playlists_key"),
    FOREIGN KEY("scope_key","playlists_key") REFERENCES "music_history_playlists_context"("scope_key","playlists_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_history_days" (
    "scope_key" TEXT NOT NULL,
    "days_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 15),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "date" TEXT,
    "at" REAL,
    "id" TEXT,
    CHECK(("date" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("at" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("id" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    PRIMARY KEY("scope_key","days_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_history"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_history_days_songs" (
    "scope_key" TEXT NOT NULL,
    "days_key" TEXT NOT NULL,
    "songs_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "songId" TEXT,
    "at" REAL,
    CHECK(("songId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("at" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    FOREIGN KEY("songId") REFERENCES music_songs(song_id),
    PRIMARY KEY("scope_key","days_key","songs_key"),
    FOREIGN KEY("scope_key","days_key") REFERENCES "music_history_days"("scope_key","days_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_history_days_songs_context" (
    "scope_key" TEXT NOT NULL,
    "days_key" TEXT NOT NULL,
    "songs_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 131071),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "collectionId" TEXT,
    "playlistId" TEXT,
    "sourceTitle" TEXT,
    "playlist" TEXT,
    "sourceType" TEXT,
    "sourceId" TEXT,
    "sourcePlatform" TEXT,
    "platform" TEXT,
    "sourceLabel" TEXT,
    "searchKeyword" TEXT,
    "dailyBranch" TEXT,
    "heartbeatPlaylistId" TEXT,
    "heartbeatAccountId" TEXT,
    "provider" TEXT,
    "remoteSession" TEXT,
    CHECK(("collectionId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("playlistId" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("sourceTitle" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("playlist" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("sourceType" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("sourceId" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("sourcePlatform" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("platform" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("sourceLabel" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("searchKeyword" IS NULL)=((shape_mask & 512)=0 OR (null_mask & 512)<>0)),
    CHECK(("dailyBranch" IS NULL)=((shape_mask & 1024)=0 OR (null_mask & 1024)<>0)),
    CHECK(("heartbeatPlaylistId" IS NULL)=((shape_mask & 2048)=0 OR (null_mask & 2048)<>0)),
    CHECK(("heartbeatAccountId" IS NULL)=((shape_mask & 4096)=0 OR (null_mask & 4096)<>0)),
    CHECK(("provider" IS NULL)=((shape_mask & 8192)=0 OR (null_mask & 8192)<>0)),
    CHECK(("remoteSession" IS NULL)=((shape_mask & 16384)=0 OR (null_mask & 16384)<>0)),
    PRIMARY KEY("scope_key","days_key","songs_key"),
    FOREIGN KEY("scope_key","days_key","songs_key") REFERENCES "music_history_days_songs"("scope_key","days_key","songs_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_history_days_songs_context_queueSource" (
    "scope_key" TEXT NOT NULL,
    "days_key" TEXT NOT NULL,
    "songs_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 15),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "id" TEXT,
    "title" TEXT,
    "kind" TEXT,
    "platform" TEXT,
    CHECK(("id" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("title" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("kind" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("platform" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    PRIMARY KEY("scope_key","days_key","songs_key"),
    FOREIGN KEY("scope_key","days_key","songs_key") REFERENCES "music_history_days_songs_context"("scope_key","days_key","songs_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_history_days_songs_context_similar" (
    "scope_key" TEXT NOT NULL,
    "days_key" TEXT NOT NULL,
    "songs_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "batchId" TEXT,
    "basedOnSongId" TEXT,
    "provider" TEXT,
    CHECK(("batchId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("basedOnSongId" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("provider" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    FOREIGN KEY("basedOnSongId") REFERENCES music_songs(song_id),
    PRIMARY KEY("scope_key","days_key","songs_key"),
    FOREIGN KEY("scope_key","days_key","songs_key") REFERENCES "music_history_days_songs_context"("scope_key","days_key","songs_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_playback" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 4095),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "schema" INTEGER,
    "revision" INTEGER,
    "mode" INTEGER,
    "lyricsEnabled" INTEGER CHECK("lyricsEnabled" IN (0,1)),
    "showTranslation" INTEGER CHECK("showTranslation" IN (0,1)),
    CHECK(("schema" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("revision" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("mode" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("lyricsEnabled" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("showTranslation" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "music_playback_qualitySettings" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 31),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "request" TEXT,
    CHECK(("request" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_playback"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_playback_qualitySettings_qualityOrder" (
    "scope_key" TEXT NOT NULL,
    "qualityOrder_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","qualityOrder_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_playback_qualitySettings"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_playback_qualitySettings_qualityEnabled" (
    "scope_key" TEXT NOT NULL,
    "qualityEnabled_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","qualityEnabled_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_playback_qualitySettings"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_playback_qualitySettings_spatialOrder" (
    "scope_key" TEXT NOT NULL,
    "spatialOrder_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","spatialOrder_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_playback_qualitySettings"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_playback_qualitySettings_spatialEnabled" (
    "scope_key" TEXT NOT NULL,
    "spatialEnabled_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","spatialEnabled_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_playback_qualitySettings"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_playback_officialSources" (
    "scope_key" TEXT NOT NULL,
    "officialSources_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","officialSources_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_playback"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_playback_metadataPlatforms" (
    "scope_key" TEXT NOT NULL,
    "metadataPlatforms_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","metadataPlatforms_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_playback"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_playback_metadataEnabled" (
    "scope_key" TEXT NOT NULL,
    "metadataEnabled_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","metadataEnabled_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_playback"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_playback_position" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "occurrenceId" TEXT,
    "seconds" REAL,
    CHECK(("occurrenceId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("seconds" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    PRIMARY KEY("scope_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_playback"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_playback_sourceExclusions" (
    "scope_key" TEXT NOT NULL,
    "sourceExclusions_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 63),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "songID" TEXT,
    "title" TEXT,
    "artist" TEXT,
    "provider" TEXT,
    "platform" TEXT,
    "sourceKey" TEXT,
    CHECK(("songID" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("title" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("artist" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("provider" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("platform" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("sourceKey" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    FOREIGN KEY("songID") REFERENCES music_songs(song_id),
    PRIMARY KEY("scope_key","sourceExclusions_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_playback"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_playback_recordingSourceExclusions" (
    "scope_key" TEXT NOT NULL,
    "recordingSourceExclusions_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 63),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "songID" TEXT,
    "title" TEXT,
    "artist" TEXT,
    "provider" TEXT,
    "platform" TEXT,
    "sourceKey" TEXT,
    CHECK(("songID" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("title" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("artist" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("provider" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("platform" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("sourceKey" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    FOREIGN KEY("songID") REFERENCES music_songs(song_id),
    PRIMARY KEY("scope_key","recordingSourceExclusions_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_playback"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_catalog" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "schema" INTEGER,
    "revision" INTEGER,
    "platform" TEXT,
    CHECK(("schema" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("revision" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("platform" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "music_windows_playback_settings" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "schema" INTEGER,
    "mode" INTEGER,
    CHECK(("schema" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("mode" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "music_windows_online_settings" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "schema" INTEGER,
    "revision" INTEGER,
    "lyrics" INTEGER CHECK("lyrics" IN (0,1)),
    CHECK(("schema" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("revision" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("lyrics" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "music_windows_device_settings" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "schema" INTEGER,
    "revision" INTEGER,
    "volume" INTEGER,
    CHECK(("schema" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("revision" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("volume" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "music_windows_appearance" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 63),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "schema" INTEGER,
    "revision" INTEGER,
    "playlistView" TEXT,
    "playerTheme" TEXT,
    "colorMode" TEXT,
    "colorPalette" TEXT,
    CHECK(("schema" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("revision" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("playlistView" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("playerTheme" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("colorMode" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("colorPalette" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "music_windows_catalog" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "schema" INTEGER,
    "revision" INTEGER,
    "platform" TEXT,
    CHECK(("schema" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("revision" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("platform" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "music_windows_desktop_lyrics" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 63),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "schema" INTEGER,
    "revision" INTEGER,
    "layout" TEXT,
    "normalColor" TEXT,
    "highlightColor" TEXT,
    CHECK(("schema" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("revision" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("layout" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("normalColor" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("highlightColor" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "music_windows_desktop_lyrics_geometry" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 31),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "monitor" TEXT,
    "x" REAL,
    "y" REAL,
    "width" REAL,
    "height" REAL,
    CHECK(("monitor" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("x" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("y" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("width" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("height" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    PRIMARY KEY("scope_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_desktop_lyrics"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_quality" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 127),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "schema" INTEGER,
    "revision" INTEGER,
    "request" TEXT,
    CHECK(("schema" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("revision" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("request" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "music_windows_quality_qualityOrder" (
    "scope_key" TEXT NOT NULL,
    "qualityOrder_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","qualityOrder_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_quality"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_quality_qualityEnabled" (
    "scope_key" TEXT NOT NULL,
    "qualityEnabled_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","qualityEnabled_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_quality"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_quality_spatialOrder" (
    "scope_key" TEXT NOT NULL,
    "spatialOrder_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","spatialOrder_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_quality"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_quality_spatialEnabled" (
    "scope_key" TEXT NOT NULL,
    "spatialEnabled_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","spatialEnabled_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_quality"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_official_sources" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 15),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "schema" INTEGER,
    "revision" INTEGER,
    CHECK(("schema" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("revision" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "music_windows_official_sources_platforms" (
    "scope_key" TEXT NOT NULL,
    "platforms_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","platforms_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_official_sources"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_official_sources_enabled" (
    "scope_key" TEXT NOT NULL,
    "enabled_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","enabled_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_official_sources"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_metadata" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 15),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "schema" INTEGER,
    "revision" INTEGER,
    CHECK(("schema" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("revision" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "music_windows_metadata_platforms" (
    "scope_key" TEXT NOT NULL,
    "platforms_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","platforms_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_metadata"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_metadata_enabled" (
    "scope_key" TEXT NOT NULL,
    "enabled_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","enabled_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_metadata"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_search" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 15),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "schema" INTEGER,
    "revision" INTEGER,
    CHECK(("schema" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("revision" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "music_windows_search_songs" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 31),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "pages" INTEGER,
    "concurrency" INTEGER,
    "cacheMinutes" INTEGER,
    CHECK(("pages" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("concurrency" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("cacheMinutes" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    PRIMARY KEY("scope_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_search"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_search_songs_order" (
    "scope_key" TEXT NOT NULL,
    "order_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","order_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_search_songs"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_search_songs_enabled" (
    "scope_key" TEXT NOT NULL,
    "enabled_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","enabled_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_search_songs"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_search_playlists" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 31),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "pages" INTEGER,
    "concurrency" INTEGER,
    "cacheMinutes" INTEGER,
    CHECK(("pages" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("concurrency" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("cacheMinutes" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    PRIMARY KEY("scope_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_search"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_search_playlists_order" (
    "scope_key" TEXT NOT NULL,
    "order_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","order_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_search_playlists"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_search_playlists_enabled" (
    "scope_key" TEXT NOT NULL,
    "enabled_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","enabled_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_search_playlists"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_search_history" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "schema" INTEGER,
    "revision" INTEGER,
    CHECK(("schema" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("revision" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "music_windows_search_history_items" (
    "scope_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "text" TEXT,
    "kind" TEXT,
    CHECK(("text" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("kind" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    PRIMARY KEY("scope_key","items_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_search_history"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_exclusions" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "schema" INTEGER,
    "revision" INTEGER,
    CHECK(("schema" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("revision" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "music_windows_exclusions_items" (
    "scope_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 127),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "songId" TEXT,
    "provider" TEXT,
    "platform" TEXT,
    "providerName" TEXT,
    "title" TEXT,
    "artist" TEXT,
    "at" INTEGER,
    CHECK(("songId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("provider" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("platform" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("providerName" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("title" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("artist" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("at" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    FOREIGN KEY("songId") REFERENCES music_songs(song_id),
    PRIMARY KEY("scope_key","items_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_exclusions"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_local_lyrics" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "schema" INTEGER,
    CHECK(("schema" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "music_windows_local_lyrics_items" (
    "scope_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "songId" TEXT,
    "contentHash" TEXT,
    CHECK(("songId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("contentHash" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    FOREIGN KEY("songId") REFERENCES music_songs(song_id),
    PRIMARY KEY("scope_key","items_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_local_lyrics"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_local_lyrics_items_lines" (
    "scope_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    "lines_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 63),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "time" REAL,
    "text" TEXT,
    "translation" TEXT,
    "romanization" TEXT,
    "end" REAL,
    CHECK(("time" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("text" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("translation" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("romanization" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("end" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    PRIMARY KEY("scope_key","items_key","lines_key"),
    FOREIGN KEY("scope_key","items_key") REFERENCES "music_windows_local_lyrics_items"("scope_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_local_lyrics_items_lines_words" (
    "scope_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    "lines_key" TEXT NOT NULL,
    "words_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 15),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "time" REAL,
    "text" TEXT,
    "duration" REAL,
    "offset" REAL,
    CHECK(("time" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("text" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("duration" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("offset" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    PRIMARY KEY("scope_key","items_key","lines_key","words_key"),
    FOREIGN KEY("scope_key","items_key","lines_key") REFERENCES "music_windows_local_lyrics_items_lines"("scope_key","items_key","lines_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_sync" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 127),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "schema" INTEGER,
    "revision" INTEGER,
    CHECK(("schema" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("revision" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "music_windows_sync_platforms" (
    "scope_key" TEXT NOT NULL,
    "platforms_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "favorites" INTEGER CHECK("favorites" IN (0,1)),
    "playlists" INTEGER CHECK("playlists" IN (0,1)),
    "accountId" TEXT,
    CHECK(("favorites" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("playlists" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("accountId" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    PRIMARY KEY("scope_key","platforms_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_sync"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_sync_pending" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 8191),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "platform" TEXT,
    "accountId" TEXT,
    "collectionId" TEXT,
    "remoteId" TEXT,
    "operation" TEXT,
    "beforePresent" INTEGER CHECK("beforePresent" IN (0,1)),
    "desiredPresent" INTEGER CHECK("desiredPresent" IN (0,1)),
    "name" TEXT,
    CHECK(("platform" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("accountId" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("collectionId" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("remoteId" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("operation" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("beforePresent" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("desiredPresent" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("name" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    PRIMARY KEY("scope_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_sync"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_sync_pending_before" (
    "scope_key" TEXT NOT NULL,
    "before_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","before_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_sync_pending"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_sync_pending_desired" (
    "scope_key" TEXT NOT NULL,
    "desired_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","desired_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_sync_pending"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_sync_pending_ids" (
    "scope_key" TEXT NOT NULL,
    "ids_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","ids_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_sync_pending"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_sync_pending_directory" (
    "scope_key" TEXT NOT NULL,
    "directory_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","directory_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_sync_pending"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_sync_pending_locator" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "listId" TEXT,
    "creatorId" TEXT,
    "creatorListId" TEXT,
    CHECK(("listId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("creatorId" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("creatorListId" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    PRIMARY KEY("scope_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_sync_pending"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_sync_confirmation" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "token" TEXT,
    "code" TEXT,
    "count" INTEGER,
    CHECK(("token" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("code" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("count" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    PRIMARY KEY("scope_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_sync"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_sync_approved" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "token" TEXT,
    "code" TEXT,
    "count" INTEGER,
    CHECK(("token" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("code" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("count" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    PRIMARY KEY("scope_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_sync"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_sync_status" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 15),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "phase" TEXT,
    "at" REAL,
    "code" TEXT,
    CHECK(("phase" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("at" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("code" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    PRIMARY KEY("scope_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_sync"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_sync_status_issues" (
    "scope_key" TEXT NOT NULL,
    "issues_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 15),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "platform" TEXT,
    "collectionID" TEXT,
    "collectionId_camel" TEXT,
    "code" TEXT,
    CHECK(("platform" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("collectionID" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("collectionId_camel" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("code" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    PRIMARY KEY("scope_key","issues_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_sync_status"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_source_discovery" (
    "song_id" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "storedBytes" INTEGER,
    CHECK(("storedBytes" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    FOREIGN KEY(song_id) REFERENCES music_songs(song_id),
    PRIMARY KEY("song_id")
) STRICT;

CREATE TABLE "music_source_discovery_platforms" (
    "song_id" TEXT NOT NULL,
    "platforms_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "checkedAt" REAL,
    CHECK(("checkedAt" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("song_id","platforms_key"),
    FOREIGN KEY("song_id") REFERENCES "music_source_discovery"("song_id") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_source_discovery_platforms_sourceKeys" (
    "song_id" TEXT NOT NULL,
    "platforms_key" TEXT NOT NULL,
    "sourceKeys_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("song_id","platforms_key","sourceKeys_key"),
    FOREIGN KEY("song_id","platforms_key") REFERENCES "music_source_discovery_platforms"("song_id","platforms_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_source_discovery_platforms_observations" (
    "song_id" TEXT NOT NULL,
    "platforms_key" TEXT NOT NULL,
    "observations_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 4095),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "requestID" TEXT,
    "startedAt" REAL,
    "order" INTEGER,
    "checkedAt" REAL,
    "platform" TEXT,
    "query" TEXT,
    "context" TEXT,
    "page" INTEGER,
    "limit" INTEGER,
    "scope" TEXT,
    "outcome" TEXT,
    "failureCode" TEXT,
    CHECK(("requestID" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("startedAt" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("order" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("checkedAt" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("platform" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("query" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("context" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("page" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("limit" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("scope" IS NULL)=((shape_mask & 512)=0 OR (null_mask & 512)<>0)),
    CHECK(("outcome" IS NULL)=((shape_mask & 1024)=0 OR (null_mask & 1024)<>0)),
    CHECK(("failureCode" IS NULL)=((shape_mask & 2048)=0 OR (null_mask & 2048)<>0)),
    PRIMARY KEY("song_id","platforms_key","observations_key"),
    FOREIGN KEY("song_id","platforms_key") REFERENCES "music_source_discovery_platforms"("song_id","platforms_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display" (
    "singleton" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "source" TEXT,
    CHECK(("source" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("singleton")
) STRICT;

CREATE TABLE "music_home_display_sections" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 511),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "title" TEXT,
    "layout" TEXT,
    "selectedFilter" TEXT,
    "selectedTab" TEXT,
    CHECK(("title" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("layout" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("selectedFilter" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("selectedTab" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    PRIMARY KEY("singleton","sections_key"),
    FOREIGN KEY("singleton") REFERENCES "music_home_display"("singleton") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_avatars" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "avatars_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("singleton","sections_key","avatars_key"),
    FOREIGN KEY("singleton","sections_key") REFERENCES "music_home_display_sections"("singleton","sections_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_channel" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 31),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "id" TEXT,
    "title" TEXT,
    "kind" TEXT,
    "pageCode" TEXT,
    "platform" TEXT,
    CHECK(("id" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("title" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("kind" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("pageCode" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("platform" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    PRIMARY KEY("singleton","sections_key"),
    FOREIGN KEY("singleton","sections_key") REFERENCES "music_home_display_sections"("singleton","sections_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_filters" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "filters_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "title" TEXT,
    "group" TEXT,
    CHECK(("title" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("group" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    PRIMARY KEY("singleton","sections_key","filters_key"),
    FOREIGN KEY("singleton","sections_key") REFERENCES "music_home_display_sections"("singleton","sections_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_tabs" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "tabs_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "title" TEXT,
    CHECK(("title" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("singleton","sections_key","tabs_key"),
    FOREIGN KEY("singleton","sections_key") REFERENCES "music_home_display_sections"("singleton","sections_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_items" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 65535),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "title" TEXT,
    "subtitle" TEXT,
    "artwork" TEXT,
    "recommendationReason" TEXT,
    "coverLabel" TEXT,
    "requiresAccount" INTEGER CHECK("requiresAccount" IN (0,1)),
    "kind" TEXT,
    "label" TEXT,
    "contextText" TEXT,
    "playCount" INTEGER,
    "presentation" TEXT,
    CHECK(("title" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("subtitle" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("artwork" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("recommendationReason" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("coverLabel" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("requiresAccount" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("kind" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("label" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("contextText" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("playCount" IS NULL)=((shape_mask & 512)=0 OR (null_mask & 512)<>0)),
    CHECK(("presentation" IS NULL)=((shape_mask & 1024)=0 OR (null_mask & 1024)<>0)),
    PRIMARY KEY("singleton","sections_key","items_key"),
    FOREIGN KEY("singleton","sections_key") REFERENCES "music_home_display_sections"("singleton","sections_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_items_previewSongIDs" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    "previewSongIDs_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("singleton","sections_key","items_key","previewSongIDs_key"),
    FOREIGN KEY("singleton","sections_key","items_key") REFERENCES "music_home_display_sections_items"("singleton","sections_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_items_coverRef" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "platform" TEXT,
    "kind" TEXT,
    "id" TEXT,
    CHECK(("platform" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("kind" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("id" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    PRIMARY KEY("singleton","sections_key","items_key"),
    FOREIGN KEY("singleton","sections_key","items_key") REFERENCES "music_home_display_sections_items"("singleton","sections_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_items_displayOnlyPreviews" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    "displayOnlyPreviews_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 31),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "title" TEXT,
    "artist" TEXT,
    "album" TEXT,
    "artwork" TEXT,
    "duration" REAL,
    CHECK(("title" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("artist" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("album" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("artwork" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("duration" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    PRIMARY KEY("singleton","sections_key","items_key","displayOnlyPreviews_key"),
    FOREIGN KEY("singleton","sections_key","items_key") REFERENCES "music_home_display_sections_items"("singleton","sections_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_items_ranking" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "songArtwork" TEXT,
    "updateFrequency" TEXT,
    CHECK(("songArtwork" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("updateFrequency" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    PRIMARY KEY("singleton","sections_key","items_key"),
    FOREIGN KEY("singleton","sections_key","items_key") REFERENCES "music_home_display_sections_items"("singleton","sections_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_items_ranking_preview" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    "preview_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 63),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "id" TEXT,
    "title" TEXT,
    "artist" TEXT,
    "artwork" TEXT,
    "rank" INTEGER,
    "lastRank" INTEGER,
    CHECK(("id" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("title" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("artist" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("artwork" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("rank" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("lastRank" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    PRIMARY KEY("singleton","sections_key","items_key","preview_key"),
    FOREIGN KEY("singleton","sections_key","items_key") REFERENCES "music_home_display_sections_items_ranking"("singleton","sections_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_items_content" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 31),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "kind" TEXT,
    "record" TEXT,
    CHECK(("kind" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("record" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    PRIMARY KEY("singleton","sections_key","items_key"),
    FOREIGN KEY("singleton","sections_key","items_key") REFERENCES "music_home_display_sections_items"("singleton","sections_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_items_content_collection" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 255),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "platform" TEXT,
    "nativeID" TEXT,
    "kind" TEXT,
    "title" TEXT,
    "creator" TEXT,
    "artwork" TEXT,
    "count" INTEGER,
    "playCount" INTEGER,
    CHECK(("platform" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("nativeID" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("kind" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("title" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("creator" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("artwork" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("count" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("playCount" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    PRIMARY KEY("singleton","sections_key","items_key"),
    FOREIGN KEY("singleton","sections_key","items_key") REFERENCES "music_home_display_sections_items_content"("singleton","sections_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_items_content_channel" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 31),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "id" TEXT,
    "title" TEXT,
    "kind" TEXT,
    "pageCode" TEXT,
    "platform" TEXT,
    CHECK(("id" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("title" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("kind" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("pageCode" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("platform" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    PRIMARY KEY("singleton","sections_key","items_key"),
    FOREIGN KEY("singleton","sections_key","items_key") REFERENCES "music_home_display_sections_items_content"("singleton","sections_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_items_content_action" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 8191),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "type" TEXT,
    "provider" TEXT,
    "mode" TEXT,
    "submode" TEXT,
    "entry" TEXT,
    "ruleId" TEXT,
    "tabTagId" TEXT,
    "sourceName" TEXT,
    "sourceType" TEXT,
    "branch" TEXT,
    "songId" TEXT,
    CHECK(("type" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("provider" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("mode" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("submode" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("entry" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("ruleId" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("tabTagId" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("sourceName" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("sourceType" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("branch" IS NULL)=((shape_mask & 512)=0 OR (null_mask & 512)<>0)),
    CHECK(("songId" IS NULL)=((shape_mask & 1024)=0 OR (null_mask & 1024)<>0)),
    PRIMARY KEY("singleton","sections_key","items_key"),
    FOREIGN KEY("singleton","sections_key","items_key") REFERENCES "music_home_display_sections_items_content"("singleton","sections_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_items_content_action_sourceIds" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    "sourceIds_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("singleton","sections_key","items_key","sourceIds_key"),
    FOREIGN KEY("singleton","sections_key","items_key") REFERENCES "music_home_display_sections_items_content_action"("singleton","sections_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_items_content_action_ids" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    "ids_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("singleton","sections_key","items_key","ids_key"),
    FOREIGN KEY("singleton","sections_key","items_key") REFERENCES "music_home_display_sections_items_content_action"("singleton","sections_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_tabs_items" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "tabs_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 65535),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "title" TEXT,
    "subtitle" TEXT,
    "artwork" TEXT,
    "recommendationReason" TEXT,
    "coverLabel" TEXT,
    "requiresAccount" INTEGER CHECK("requiresAccount" IN (0,1)),
    "kind" TEXT,
    "label" TEXT,
    "contextText" TEXT,
    "playCount" INTEGER,
    "presentation" TEXT,
    CHECK(("title" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("subtitle" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("artwork" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("recommendationReason" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("coverLabel" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("requiresAccount" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("kind" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("label" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("contextText" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("playCount" IS NULL)=((shape_mask & 512)=0 OR (null_mask & 512)<>0)),
    CHECK(("presentation" IS NULL)=((shape_mask & 1024)=0 OR (null_mask & 1024)<>0)),
    PRIMARY KEY("singleton","sections_key","tabs_key","items_key"),
    FOREIGN KEY("singleton","sections_key","tabs_key") REFERENCES "music_home_display_sections_tabs"("singleton","sections_key","tabs_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_tabs_items_previewSongIDs" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "tabs_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    "previewSongIDs_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("singleton","sections_key","tabs_key","items_key","previewSongIDs_key"),
    FOREIGN KEY("singleton","sections_key","tabs_key","items_key") REFERENCES "music_home_display_sections_tabs_items"("singleton","sections_key","tabs_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_tabs_items_coverRef" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "tabs_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "platform" TEXT,
    "kind" TEXT,
    "id" TEXT,
    CHECK(("platform" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("kind" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("id" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    PRIMARY KEY("singleton","sections_key","tabs_key","items_key"),
    FOREIGN KEY("singleton","sections_key","tabs_key","items_key") REFERENCES "music_home_display_sections_tabs_items"("singleton","sections_key","tabs_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_tabs_items_displayOnlyPreviews" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "tabs_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    "displayOnlyPreviews_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 31),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "title" TEXT,
    "artist" TEXT,
    "album" TEXT,
    "artwork" TEXT,
    "duration" REAL,
    CHECK(("title" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("artist" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("album" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("artwork" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("duration" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    PRIMARY KEY("singleton","sections_key","tabs_key","items_key","displayOnlyPreviews_key"),
    FOREIGN KEY("singleton","sections_key","tabs_key","items_key") REFERENCES "music_home_display_sections_tabs_items"("singleton","sections_key","tabs_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_tabs_items_ranking" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "tabs_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "songArtwork" TEXT,
    "updateFrequency" TEXT,
    CHECK(("songArtwork" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("updateFrequency" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    PRIMARY KEY("singleton","sections_key","tabs_key","items_key"),
    FOREIGN KEY("singleton","sections_key","tabs_key","items_key") REFERENCES "music_home_display_sections_tabs_items"("singleton","sections_key","tabs_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_tabs_items_ranking_preview" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "tabs_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    "preview_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 63),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "id" TEXT,
    "title" TEXT,
    "artist" TEXT,
    "artwork" TEXT,
    "rank" INTEGER,
    "lastRank" INTEGER,
    CHECK(("id" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("title" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("artist" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("artwork" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("rank" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("lastRank" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    PRIMARY KEY("singleton","sections_key","tabs_key","items_key","preview_key"),
    FOREIGN KEY("singleton","sections_key","tabs_key","items_key") REFERENCES "music_home_display_sections_tabs_items_ranking"("singleton","sections_key","tabs_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_tabs_items_content" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "tabs_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 31),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "kind" TEXT,
    "record" TEXT,
    CHECK(("kind" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("record" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    PRIMARY KEY("singleton","sections_key","tabs_key","items_key"),
    FOREIGN KEY("singleton","sections_key","tabs_key","items_key") REFERENCES "music_home_display_sections_tabs_items"("singleton","sections_key","tabs_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_tabs_items_content_collection" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "tabs_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 255),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "platform" TEXT,
    "nativeID" TEXT,
    "kind" TEXT,
    "title" TEXT,
    "creator" TEXT,
    "artwork" TEXT,
    "count" INTEGER,
    "playCount" INTEGER,
    CHECK(("platform" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("nativeID" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("kind" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("title" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("creator" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("artwork" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("count" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("playCount" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    PRIMARY KEY("singleton","sections_key","tabs_key","items_key"),
    FOREIGN KEY("singleton","sections_key","tabs_key","items_key") REFERENCES "music_home_display_sections_tabs_items_content"("singleton","sections_key","tabs_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_tabs_items_content_channel" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "tabs_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 31),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "id" TEXT,
    "title" TEXT,
    "kind" TEXT,
    "pageCode" TEXT,
    "platform" TEXT,
    CHECK(("id" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("title" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("kind" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("pageCode" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("platform" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    PRIMARY KEY("singleton","sections_key","tabs_key","items_key"),
    FOREIGN KEY("singleton","sections_key","tabs_key","items_key") REFERENCES "music_home_display_sections_tabs_items_content"("singleton","sections_key","tabs_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_tabs_items_content_action" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "tabs_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 8191),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "type" TEXT,
    "provider" TEXT,
    "mode" TEXT,
    "submode" TEXT,
    "entry" TEXT,
    "ruleId" TEXT,
    "tabTagId" TEXT,
    "sourceName" TEXT,
    "sourceType" TEXT,
    "branch" TEXT,
    "songId" TEXT,
    CHECK(("type" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("provider" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("mode" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("submode" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("entry" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("ruleId" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("tabTagId" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("sourceName" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("sourceType" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("branch" IS NULL)=((shape_mask & 512)=0 OR (null_mask & 512)<>0)),
    CHECK(("songId" IS NULL)=((shape_mask & 1024)=0 OR (null_mask & 1024)<>0)),
    PRIMARY KEY("singleton","sections_key","tabs_key","items_key"),
    FOREIGN KEY("singleton","sections_key","tabs_key","items_key") REFERENCES "music_home_display_sections_tabs_items_content"("singleton","sections_key","tabs_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_tabs_items_content_action_sourceIds" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "tabs_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    "sourceIds_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("singleton","sections_key","tabs_key","items_key","sourceIds_key"),
    FOREIGN KEY("singleton","sections_key","tabs_key","items_key") REFERENCES "music_home_display_sections_tabs_items_content_action"("singleton","sections_key","tabs_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_tabs_items_content_action_ids" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "tabs_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    "ids_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("singleton","sections_key","tabs_key","items_key","ids_key"),
    FOREIGN KEY("singleton","sections_key","tabs_key","items_key") REFERENCES "music_home_display_sections_tabs_items_content_action"("singleton","sections_key","tabs_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE UNIQUE INDEX music_source_identity ON music_song_sources(sources_key);

CREATE INDEX music_song_lookup ON music_songs(lookup_key);

CREATE INDEX music_song_query ON music_songs(search_query);

CREATE INDEX music_song_fingerprint ON music_songs(fingerprint,baseline_duration);

CREATE UNIQUE INDEX music_local_file ON music_songs(localFileId) WHERE localFileId IS NOT NULL AND localFileId<>'';

CREATE INDEX music_song_artwork ON music_songs(cover_url);

CREATE TRIGGER music_baseline_immutable BEFORE UPDATE OF baselineDuration_kind,baselineDuration_value ON music_songs WHEN OLD.baselineDuration_value IS NOT NULL AND (OLD.baselineDuration_kind IS NOT NEW.baselineDuration_kind OR OLD.baselineDuration_value IS NOT NEW.baselineDuration_value) BEGIN SELECT RAISE(ABORT,'immutable recording baseline'); END;

-- module: 40_ios.sql
-- Native iOS owners. Media/source bytes remain in their owned file directories.
CREATE TABLE ios_settings_blocks (
    block TEXT PRIMARY KEY CHECK (block IN ('cat.sources','pan.accounts','tmdb','douban','film.rules','film.preferences')),
    revision INTEGER NOT NULL CHECK (revision BETWEEN 0 AND 9007199254740991)
) STRICT;
INSERT INTO ios_settings_blocks VALUES
 ('cat.sources',0),('pan.accounts',0),('tmdb',0),('douban',0),('film.rules',0),('film.preferences',0);

CREATE TABLE ios_tmdb_settings (
 singleton INTEGER PRIMARY KEY CHECK(singleton=1),
 apiBase TEXT NOT NULL, imageBase TEXT NOT NULL, apiKey TEXT NOT NULL, accessToken TEXT NOT NULL,
 language TEXT NOT NULL, region TEXT NOT NULL, includeAdult INTEGER NOT NULL CHECK(includeAdult IN (0,1))
) STRICT;
CREATE TABLE ios_douban_settings (
 singleton INTEGER PRIMARY KEY CHECK(singleton=1),
 apiBase TEXT NOT NULL, searchBase TEXT NOT NULL, imageBase TEXT NOT NULL,
 apiProxy TEXT NOT NULL, imageProxy TEXT NOT NULL, cookie TEXT NOT NULL
) STRICT;
CREATE TABLE ios_pan_accounts (
 provider TEXT PRIMARY KEY CHECK(provider IN ('baidu','quark','uc','tianyi','yidong')),
 account_revision INTEGER NOT NULL CHECK(account_revision BETWEEN 0 AND 9007199254740991),
 row_revision INTEGER NOT NULL CHECK(row_revision BETWEEN 0 AND 9007199254740991),
 cookie TEXT, username TEXT, password TEXT, authorization TEXT, refreshedAt INTEGER,
 CHECK(refreshedAt IS NULL OR refreshedAt BETWEEN 0 AND 9007199254740991),
 CHECK(provider IN ('baidu','quark','uc','tianyi') OR cookie IS NULL),
 CHECK(provider='tianyi' OR (username IS NULL AND password IS NULL)),
 CHECK(provider='yidong' OR (authorization IS NULL AND refreshedAt IS NULL))
) STRICT;
INSERT INTO ios_pan_accounts(provider,account_revision,row_revision) VALUES
 ('baidu',0,0),('quark',0,0),('uc',0,0),('tianyi',0,0),('yidong',0,0);
CREATE TABLE ios_cat_settings (singleton INTEGER PRIMARY KEY CHECK(singleton=1), port INTEGER NOT NULL CHECK(port BETWEEN 0 AND 65535)) STRICT;
CREATE TABLE ios_cat_providers (
 provider TEXT PRIMARY KEY, enabled INTEGER NOT NULL CHECK(enabled IN(0,1))
) STRICT;
CREATE TABLE ios_cat_mappings (
 ordinal INTEGER PRIMARY KEY CHECK(ordinal>=0), provider TEXT NOT NULL
) STRICT;
CREATE TABLE ios_cat_mapping_aliases (
 mapping_ordinal INTEGER NOT NULL REFERENCES ios_cat_mappings(ordinal) ON DELETE CASCADE,
 ordinal INTEGER NOT NULL CHECK(ordinal>=0), alias TEXT NOT NULL,
 PRIMARY KEY(mapping_ordinal,ordinal)
) STRICT;
CREATE TABLE ios_cat_inputs (
 installationId TEXT PRIMARY KEY, ordinal INTEGER NOT NULL,
 name TEXT NOT NULL, sourceType TEXT NOT NULL CHECK(sourceType IN ('local','network')),
 sourceUrl TEXT, localPath TEXT, localHash TEXT, enabled INTEGER NOT NULL CHECK(enabled IN(0,1)),
 inputRevision INTEGER NOT NULL CHECK(inputRevision BETWEEN 1 AND 9007199254740991),
 row_revision INTEGER NOT NULL DEFAULT 1 CHECK(row_revision BETWEEN 1 AND 9007199254740991)
) STRICT;
CREATE TABLE ios_film_rules (
 rule_group TEXT NOT NULL CHECK(rule_group IN ('episodeCleanRegexRules','episodeRules','movieRules','aggregateRegexRules')),
 ordinal INTEGER NOT NULL CHECK(ordinal>=0),
 pattern TEXT NOT NULL, replacement TEXT NOT NULL, flags TEXT NOT NULL,
 PRIMARY KEY(rule_group,ordinal)
) STRICT;
CREATE TABLE ios_film_preferences (
 singleton INTEGER PRIMARY KEY CHECK(singleton=1),
 iosAutoPrepare INTEGER NOT NULL CHECK(iosAutoPrepare IN(0,1)),
 metadataViewProvider TEXT NOT NULL CHECK(metadataViewProvider IN('tmdb','douban'))
) STRICT;
CREATE TABLE ios_film_preference_items (
 list TEXT NOT NULL CHECK(list IN ('ruleOrder','panOrder','keywords','siteCleanKeywords')),
 ordinal INTEGER NOT NULL CHECK(ordinal>=0), value TEXT NOT NULL,
 PRIMARY KEY(list,ordinal), UNIQUE(list,value)
) STRICT;
CREATE TABLE ios_film_favorites (
 businessID TEXT PRIMARY KEY, sourceID TEXT NOT NULL, itemID TEXT NOT NULL, kind TEXT,
 target_kind TEXT NOT NULL CHECK(target_kind IN('movie','tv','source','group')), target_title TEXT NOT NULL,
 target_year INTEGER, metadata_provider TEXT, metadata_id_kind TEXT, metadata_id_text TEXT,
 season INTEGER, episode INTEGER, global_episode INTEGER,
 source_key TEXT, source_engine TEXT, source_installation_id TEXT,
 source_site_kind TEXT, source_site_text TEXT,
 source_detail_kind TEXT, source_detail_text TEXT, source_line_kind TEXT, source_line_text TEXT,
 row_revision INTEGER NOT NULL CHECK(row_revision BETWEEN 1 AND 9007199254740991),
 added_at REAL NOT NULL,
 CHECK((metadata_id_kind IS NULL AND metadata_id_text IS NULL) OR
       (metadata_id_kind IN('text','integer') AND metadata_id_text IS NOT NULL)),
 CHECK((source_site_kind IS NULL AND source_site_text IS NULL) OR
       (source_site_kind IN('text','integer') AND source_site_text IS NOT NULL)),
 CHECK((source_detail_kind IS NULL AND source_detail_text IS NULL) OR
       (source_detail_kind IN('text','integer') AND source_detail_text IS NOT NULL)),
 CHECK((source_line_kind IS NULL AND source_line_text IS NULL) OR
       (source_line_kind IN('text','integer') AND source_line_text IS NOT NULL))
) STRICT;
CREATE INDEX ios_film_favorites_page ON ios_film_favorites(added_at DESC,businessID);
CREATE TABLE ios_film_favorite_members (
 businessID TEXT NOT NULL REFERENCES ios_film_favorites(businessID) ON DELETE CASCADE,
 ordinal INTEGER NOT NULL CHECK(ordinal>=0), source_key TEXT NOT NULL,
 detail_kind TEXT NOT NULL CHECK(detail_kind IN('text','integer')), detail_text TEXT NOT NULL,
 PRIMARY KEY(businessID,ordinal)
) STRICT;
CREATE TABLE ios_film_player_preferences (
 singleton INTEGER PRIMARY KEY CHECK(singleton=1),
 engine TEXT NOT NULL, defaultRate REAL NOT NULL, holdRate REAL NOT NULL, seekStep REAL NOT NULL,
 showsControlCenter INTEGER NOT NULL CHECK(showsControlCenter IN(0,1)),
 recognizesChapters INTEGER NOT NULL CHECK(recognizesChapters IN(0,1)),
 singleTapCenter TEXT NOT NULL, singleTapSides TEXT NOT NULL, doubleTapCenter TEXT NOT NULL, doubleTapSides TEXT NOT NULL,
 swipeVolume INTEGER NOT NULL CHECK(swipeVolume IN(0,1)), swipeBrightness INTEGER NOT NULL CHECK(swipeBrightness IN(0,1)),
 swipeSeek INTEGER NOT NULL CHECK(swipeSeek IN(0,1)), swipeEpisodes INTEGER NOT NULL CHECK(swipeEpisodes IN(0,1)),
 hapticHold INTEGER NOT NULL CHECK(hapticHold IN(0,1)), hapticBrightness INTEGER NOT NULL CHECK(hapticBrightness IN(0,1)),
 hapticVolume INTEGER NOT NULL CHECK(hapticVolume IN(0,1)), hapticSeek INTEGER NOT NULL CHECK(hapticSeek IN(0,1)),
 row_revision INTEGER NOT NULL CHECK(row_revision BETWEEN 1 AND 9007199254740991)
) STRICT;
CREATE TABLE ios_film_source_state (singleton INTEGER PRIMARY KEY CHECK(singleton=1), revision INTEGER NOT NULL CHECK(revision BETWEEN 0 AND 9007199254740991)) STRICT;
INSERT INTO ios_film_source_state VALUES(1,0);
CREATE TABLE ios_film_source_probes (
 installationId TEXT PRIMARY KEY, inputRevision INTEGER NOT NULL, importedAt INTEGER, updatedAt INTEGER,
 updateState TEXT NOT NULL, updateError TEXT, sourceHash TEXT
) STRICT;
CREATE TABLE ios_film_source_sites (
 installationId TEXT NOT NULL REFERENCES ios_film_source_probes(installationId) ON DELETE CASCADE,
    siteKey TEXT NOT NULL, ordinal INTEGER NOT NULL, name TEXT NOT NULL, api TEXT NOT NULL,
 PRIMARY KEY(installationId,siteKey)
) STRICT;
CREATE TABLE ios_film_source_actions (
    installationId TEXT NOT NULL, siteKey TEXT NOT NULL, ordinal INTEGER NOT NULL, action TEXT NOT NULL,
    PRIMARY KEY(installationId,siteKey,ordinal),
    FOREIGN KEY(installationId,siteKey) REFERENCES ios_film_source_sites ON DELETE CASCADE
) STRICT;
CREATE TABLE ios_film_source_methods (
    installationId TEXT NOT NULL, siteKey TEXT NOT NULL, action TEXT NOT NULL, method TEXT NOT NULL,
    PRIMARY KEY(installationId,siteKey,action),
    FOREIGN KEY(installationId,siteKey) REFERENCES ios_film_source_sites ON DELETE CASCADE
) STRICT;
CREATE TABLE ios_film_video_intents (
    installationId TEXT NOT NULL, siteKey TEXT NOT NULL, ordinal INTEGER NOT NULL, name TEXT NOT NULL, api TEXT NOT NULL,
    enabled INTEGER NOT NULL CHECK(enabled IN(0,1)), homeVisible INTEGER NOT NULL CHECK(homeVisible IN(0,1)),
    searchEnabled INTEGER NOT NULL CHECK(searchEnabled IN(0,1)),
 aggregateImages INTEGER NOT NULL CHECK(aggregateImages IN(0,1)),
 PRIMARY KEY(installationId,siteKey)
) STRICT;
CREATE UNIQUE INDEX ios_film_one_aggregate_images ON ios_film_video_intents(aggregateImages) WHERE aggregateImages=1;
CREATE TABLE ios_film_video_actions (
    installationId TEXT NOT NULL, siteKey TEXT NOT NULL, ordinal INTEGER NOT NULL, action TEXT NOT NULL,
    PRIMARY KEY(installationId,siteKey,ordinal),
    FOREIGN KEY(installationId,siteKey) REFERENCES ios_film_video_intents ON DELETE CASCADE
) STRICT;
CREATE TABLE ios_film_video_methods (
    installationId TEXT NOT NULL, siteKey TEXT NOT NULL, action TEXT NOT NULL, method TEXT NOT NULL,
    PRIMARY KEY(installationId,siteKey,action),
    FOREIGN KEY(installationId,siteKey) REFERENCES ios_film_video_intents ON DELETE CASCADE
) STRICT;
CREATE TABLE ios_film_video_detections (
    installationId TEXT NOT NULL, siteKey TEXT NOT NULL, result TEXT NOT NULL,
    checkedAt INTEGER NOT NULL CHECK(checkedAt BETWEEN 0 AND 9007199254740991),
    playbackResolved INTEGER NOT NULL CHECK(playbackResolved IN(0,1)),
    PRIMARY KEY(installationId,siteKey),
    FOREIGN KEY(installationId,siteKey) REFERENCES ios_film_video_intents ON DELETE CASCADE
) STRICT;
CREATE TABLE ios_film_video_probe_steps (
    installationId TEXT NOT NULL, siteKey TEXT NOT NULL, ordinal INTEGER NOT NULL,
    operation TEXT NOT NULL, state TEXT NOT NULL, error TEXT,
    PRIMARY KEY(installationId,siteKey,ordinal),
    FOREIGN KEY(installationId,siteKey) REFERENCES ios_film_video_detections ON DELETE CASCADE
) STRICT;
CREATE TABLE ios_film_source_deletions (
 installationId TEXT NOT NULL, siteKey TEXT NOT NULL, PRIMARY KEY(installationId,siteKey)
) STRICT;

CREATE TABLE ios_novel_books (
 id TEXT PRIMARY KEY, installationId TEXT NOT NULL, bookUrl TEXT NOT NULL, identity TEXT NOT NULL,
 name TEXT NOT NULL, author TEXT NOT NULL, tocUrl TEXT NOT NULL, coverUrl TEXT NOT NULL,
 intro TEXT NOT NULL, latestChapterTitle TEXT NOT NULL,
 onShelf INTEGER NOT NULL CHECK(onShelf IN(0,1)), pinnedAt REAL, addedAt REAL NOT NULL,
 row_revision INTEGER NOT NULL CHECK(row_revision BETWEEN 1 AND 9007199254740991),
 UNIQUE(installationId,bookUrl)
) STRICT;
CREATE UNIQUE INDEX ios_novel_book_identity ON ios_novel_books(identity) WHERE identity<>'';
CREATE INDEX ios_novel_shelf ON ios_novel_books(onShelf,pinnedAt DESC,addedAt DESC,id);
CREATE TABLE ios_novel_progress (
 book_id TEXT PRIMARY KEY REFERENCES ios_novel_books(id) ON DELETE CASCADE,
 chapterUrl TEXT NOT NULL, chapterTitle TEXT NOT NULL, chapterIndex INTEGER NOT NULL CHECK(chapterIndex>=0),
 blockIndex INTEGER NOT NULL CHECK(blockIndex>=0), utf16Offset INTEGER NOT NULL CHECK(utf16Offset>=0),
 ratio REAL NOT NULL CHECK(ratio BETWEEN 0 AND 1), readAt REAL NOT NULL,
 row_revision INTEGER NOT NULL CHECK(row_revision BETWEEN 1 AND 9007199254740991)
) STRICT;
CREATE INDEX ios_novel_history ON ios_novel_progress(readAt DESC,book_id);
-- The source rule's variable is the only genuinely dynamic book extension.
CREATE TABLE ios_novel_variable_nodes (
 book_id TEXT NOT NULL REFERENCES ios_novel_books(id) ON DELETE CASCADE,
 node_id INTEGER NOT NULL CHECK(node_id>=0), parent_id INTEGER, prop_key TEXT, item_index INTEGER,
 kind TEXT NOT NULL CHECK(kind IN('null','boolean','integer','real','number_text','text','array','object')),
 bool_value INTEGER CHECK(bool_value IN(0,1)), int_value INTEGER, real_value REAL, text_value TEXT,
 PRIMARY KEY(book_id,node_id)
) STRICT;
CREATE TABLE ios_novel_reading_settings (
 singleton INTEGER PRIMARY KEY CHECK(singleton=1),
 mode TEXT NOT NULL, fontSize REAL NOT NULL, fontWeight REAL NOT NULL, fontName TEXT NOT NULL,
 lineSpacing REAL NOT NULL, paragraphSpacing REAL NOT NULL, margin REAL NOT NULL, contentWidth REAL NOT NULL,
 conversion TEXT NOT NULL, theme INTEGER NOT NULL, foreground TEXT NOT NULL, background TEXT NOT NULL,
 background_image BLOB, automaticSpeed REAL NOT NULL,
 row_revision INTEGER NOT NULL CHECK(row_revision BETWEEN 1 AND 9007199254740991)
) STRICT;
CREATE TABLE ios_novel_rules (
 id TEXT PRIMARY KEY, name TEXT NOT NULL, "group" TEXT NOT NULL, isEnabled INTEGER NOT NULL CHECK(isEnabled IN(0,1)),
 "order" INTEGER NOT NULL, timeoutMillisecond INTEGER NOT NULL CHECK(timeoutMillisecond>0),
 pattern TEXT NOT NULL, replacement TEXT NOT NULL, isRegex INTEGER NOT NULL CHECK(isRegex IN(0,1)),
 scope TEXT NOT NULL, excludeScope TEXT NOT NULL, scopeTitle INTEGER NOT NULL CHECK(scopeTitle IN(0,1)),
 scopeContent INTEGER NOT NULL CHECK(scopeContent IN(0,1))
) STRICT;

CREATE TABLE ios_search_history (text TEXT PRIMARY KEY, used_at REAL NOT NULL) STRICT;
CREATE INDEX ios_search_history_recent ON ios_search_history(used_at DESC,text);
CREATE TABLE ios_music_search_preferences (
 singleton INTEGER PRIMARY KEY CHECK(singleton=1), searchPlaylists INTEGER NOT NULL CHECK(searchPlaylists IN(0,1)),
 song_pages INTEGER NOT NULL CHECK(song_pages>=1), song_concurrency INTEGER NOT NULL CHECK(song_concurrency>=1),
 song_cache_minutes INTEGER NOT NULL CHECK(song_cache_minutes BETWEEN 1 AND 1440),
 playlist_pages INTEGER NOT NULL CHECK(playlist_pages>=1), playlist_concurrency INTEGER NOT NULL CHECK(playlist_concurrency>=1),
 playlist_cache_minutes INTEGER NOT NULL CHECK(playlist_cache_minutes BETWEEN 1 AND 1440),
 row_revision INTEGER NOT NULL CHECK(row_revision BETWEEN 1 AND 9007199254740991)
) STRICT;
CREATE TABLE ios_music_search_platforms (
 kind TEXT NOT NULL CHECK(kind IN('song','playlist')), ordinal INTEGER NOT NULL, platform TEXT NOT NULL,
 PRIMARY KEY(kind,ordinal), UNIQUE(kind,platform)
) STRICT;
CREATE TABLE ios_music_appearance (
 singleton INTEGER PRIMARY KEY CHECK(singleton=1), playlistView TEXT NOT NULL, playerTheme TEXT NOT NULL,
 colorMode TEXT NOT NULL, colorPalette TEXT NOT NULL,
 lockLyric INTEGER NOT NULL CHECK(lockLyric IN(0,1)), lyricTranslation INTEGER NOT NULL CHECK(lyricTranslation IN(0,1)),
 lyricBackground INTEGER NOT NULL CHECK(lyricBackground IN(0,1)),
 row_revision INTEGER NOT NULL CHECK(row_revision BETWEEN 1 AND 9007199254740991)
) STRICT;
CREATE TABLE ios_music_song_palettes (song_key TEXT PRIMARY KEY, palette TEXT NOT NULL, used_at REAL NOT NULL) STRICT;
CREATE INDEX ios_music_song_palettes_recent ON ios_music_song_palettes(used_at DESC,song_key);
CREATE TABLE ios_music_palette_state (singleton INTEGER PRIMARY KEY CHECK(singleton=1), last_palette TEXT NOT NULL) STRICT;
CREATE TABLE ios_music_channel_selection (singleton INTEGER PRIMARY KEY CHECK(singleton=1), channel_key TEXT NOT NULL) STRICT;
CREATE TABLE ios_playback_volume (owner TEXT PRIMARY KEY, volume REAL NOT NULL CHECK(volume BETWEEN 0 AND 1)) STRICT;

CREATE TABLE ios_music_accounts (
 installation_id TEXT NOT NULL, site_key TEXT NOT NULL, account_id TEXT NOT NULL,
 platform TEXT NOT NULL, user_id TEXT NOT NULL, cookie TEXT NOT NULL, credential_revision TEXT NOT NULL,
 row_revision INTEGER NOT NULL CHECK(row_revision BETWEEN 1 AND 9007199254740991),
 PRIMARY KEY(installation_id,site_key,account_id)
) STRICT;

-- module: 41_ios_resources.sql
CREATE TABLE ios_music_audio_urls (
    scope TEXT NOT NULL, platform TEXT NOT NULL, native_id TEXT NOT NULL, quality TEXT NOT NULL,
    url TEXT NOT NULL, full INTEGER CHECK(full IN (0,1)), actual_quality TEXT,
    bitrate REAL, sample_rate REAL, duration REAL, expires_at REAL, license_expires_at REAL,
    saved_at REAL NOT NULL,
    PRIMARY KEY(scope,platform,native_id,quality)
) STRICT;
CREATE INDEX ios_music_audio_urls_age ON ios_music_audio_urls(saved_at);

CREATE TABLE ios_music_chorus (
    scope TEXT NOT NULL, platform TEXT NOT NULL, native_id TEXT NOT NULL,
    recording_duration REAL NOT NULL CHECK(recording_duration>0),
    start REAL NOT NULL CHECK(start>=0), end REAL NOT NULL CHECK(end>=start),
    expires_at REAL NOT NULL,
    PRIMARY KEY(scope,platform,native_id,recording_duration)
) STRICT;

CREATE TABLE ios_music_feedback_outbox (
    id TEXT PRIMARY KEY NOT NULL, instance TEXT NOT NULL, scope TEXT NOT NULL,
    generation INTEGER NOT NULL CHECK(generation>=0), phase TEXT NOT NULL CHECK(phase IN ('start','end')),
    nativeID TEXT NOT NULL, source TEXT NOT NULL, sourceID TEXT NOT NULL, algorithm TEXT NOT NULL,
    heartbeat INTEGER NOT NULL CHECK(heartbeat IN (0,1)),
    listened REAL NOT NULL CHECK(listened>=0), media REAL NOT NULL CHECK(media>=0),
    rate REAL NOT NULL CHECK(rate>0), occurredAt REAL NOT NULL CHECK(occurredAt>=0),
    reason TEXT, state TEXT NOT NULL CHECK(state IN ('pending','inflight')),
    startConfirmed INTEGER NOT NULL CHECK(startConfirmed IN (0,1))
) STRICT;
CREATE INDEX ios_music_feedback_dispatch ON ios_music_feedback_outbox(scope,state,occurredAt);

CREATE TABLE ios_music_images (
    resource TEXT NOT NULL, filename TEXT PRIMARY KEY NOT NULL,
    updated_at REAL NOT NULL, bytes INTEGER NOT NULL CHECK(bytes>0),
    ready INTEGER NOT NULL CHECK(ready IN (0,1))
) STRICT;
CREATE UNIQUE INDEX ios_music_images_ready ON ios_music_images(resource) WHERE ready=1;
CREATE INDEX ios_music_images_age ON ios_music_images(updated_at);
CREATE TABLE ios_music_lyrics (
    song_id TEXT PRIMARY KEY NOT NULL, source_key TEXT NOT NULL, updated_at REAL NOT NULL,
    bytes INTEGER NOT NULL CHECK(bytes>0)
) STRICT;
CREATE TABLE ios_music_lyric_lines (
    song_id TEXT NOT NULL REFERENCES ios_music_lyrics(song_id) ON DELETE CASCADE,
    position INTEGER NOT NULL CHECK(position>=0), line_index INTEGER NOT NULL,
    time REAL NOT NULL CHECK(time>=0), end REAL NOT NULL CHECK(end>=time),
    text TEXT NOT NULL, translation TEXT NOT NULL,
    PRIMARY KEY(song_id,position)
) STRICT;
CREATE TABLE ios_music_lyric_words (
    song_id TEXT NOT NULL, line_position INTEGER NOT NULL, position INTEGER NOT NULL CHECK(position>=0),
    time REAL NOT NULL, duration REAL NOT NULL CHECK(duration>=0), text TEXT NOT NULL,
    PRIMARY KEY(song_id,line_position,position),
    FOREIGN KEY(song_id,line_position) REFERENCES ios_music_lyric_lines(song_id,position) ON DELETE CASCADE
) STRICT;

-- module: 42_ios_audio_cache.sql
CREATE TABLE ios_music_audio_files (
    cache_key TEXT PRIMARY KEY NOT NULL CHECK(length(cache_key)=64), file TEXT NOT NULL UNIQUE,
    quality TEXT NOT NULL, last_played_at REAL NOT NULL, license_expires_at REAL, expires_at REAL NOT NULL,
    scope TEXT NOT NULL, song_id TEXT NOT NULL, source_key TEXT NOT NULL, platform TEXT NOT NULL,
    native_id TEXT NOT NULL, requested_quality TEXT NOT NULL, provider TEXT NOT NULL,
    generation INTEGER NOT NULL CHECK(generation>0),
    stamp_device INTEGER NOT NULL, stamp_inode TEXT NOT NULL, stamp_size INTEGER NOT NULL CHECK(stamp_size>0),
    stamp_seconds INTEGER NOT NULL, stamp_nanos INTEGER NOT NULL,
    container TEXT NOT NULL, codec TEXT NOT NULL, duration REAL NOT NULL CHECK(duration>0),
    decoded_frames INTEGER NOT NULL CHECK(decoded_frames>0), sample_rate REAL NOT NULL CHECK(sample_rate>0),
    channels INTEGER NOT NULL CHECK(channels>0), bitrate REAL NOT NULL CHECK(bitrate>0), bit_depth INTEGER,
    lossless INTEGER NOT NULL CHECK(lossless IN (0,1)), complete INTEGER NOT NULL CHECK(complete IN (0,1)),
    protected INTEGER NOT NULL CHECK(protected IN (0,1))
) STRICT;
CREATE INDEX ios_music_audio_files_song ON ios_music_audio_files(scope,song_id,last_played_at);
CREATE INDEX ios_music_audio_files_age ON ios_music_audio_files(expires_at);

-- module: 43_windows_owners.sql
-- Generated closed columns/relations; no runtime payload columns.

CREATE TABLE "windows_app_appearance" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "schema" INTEGER,
    "themeColor" TEXT,
    CHECK(("schema" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("themeColor" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "windows_app_window" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "schema" INTEGER,
    "closeBehavior" TEXT,
    CHECK(("schema" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("closeBehavior" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "windows_app_media_controls" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 15),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "schema" INTEGER,
    "globalEnabled" INTEGER CHECK("globalEnabled" IN (0,1)),
    "systemMediaEnabled" INTEGER CHECK("systemMediaEnabled" IN (0,1)),
    CHECK(("schema" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("globalEnabled" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("systemMediaEnabled" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "windows_app_media_controls_bindings" (
    "scope_key" TEXT NOT NULL,
    "bindings_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "action" TEXT,
    CHECK(("action" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","bindings_key"),
    FOREIGN KEY("scope_key") REFERENCES "windows_app_media_controls"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_app_media_controls_bindings_local" (
    "scope_key" TEXT NOT NULL,
    "bindings_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "key" TEXT,
    "modifiers" INTEGER,
    CHECK(("key" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("modifiers" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    PRIMARY KEY("scope_key","bindings_key"),
    FOREIGN KEY("scope_key","bindings_key") REFERENCES "windows_app_media_controls_bindings"("scope_key","bindings_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_app_media_controls_bindings_global" (
    "scope_key" TEXT NOT NULL,
    "bindings_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "key" TEXT,
    "modifiers" INTEGER,
    CHECK(("key" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("modifiers" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    PRIMARY KEY("scope_key","bindings_key"),
    FOREIGN KEY("scope_key","bindings_key") REFERENCES "windows_app_media_controls_bindings"("scope_key","bindings_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_file_grants" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "Schema" INTEGER,
    CHECK(("Schema" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "windows_file_grants_Items" (
    "scope_key" TEXT NOT NULL,
    "Items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "Path" TEXT,
    "Title" TEXT,
    CHECK(("Path" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("Title" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    PRIMARY KEY("scope_key","Items_key"),
    FOREIGN KEY("scope_key") REFERENCES "windows_file_grants"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_tmdb" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "version" INTEGER,
    CHECK(("version" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "windows_tmdb_settings" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 127),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "apiBase" TEXT,
    "imageBase" TEXT,
    "apiKey" TEXT,
    "accessToken" TEXT,
    "language" TEXT,
    "region" TEXT,
    "includeAdult" INTEGER CHECK("includeAdult" IN (0,1)),
    CHECK(("apiBase" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("imageBase" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("apiKey" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("accessToken" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("language" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("region" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("includeAdult" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    PRIMARY KEY("scope_key"),
    FOREIGN KEY("scope_key") REFERENCES "windows_tmdb"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_douban" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "version" INTEGER,
    CHECK(("version" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "windows_douban_settings" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 63),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "apiBase" TEXT,
    "searchBase" TEXT,
    "imageBase" TEXT,
    "apiProxy" TEXT,
    "imageProxy" TEXT,
    "cookie" TEXT,
    CHECK(("apiBase" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("searchBase" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("imageBase" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("apiProxy" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("imageProxy" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("cookie" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    PRIMARY KEY("scope_key"),
    FOREIGN KEY("scope_key") REFERENCES "windows_douban"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_film_rules" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "version" INTEGER,
    CHECK(("version" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "windows_film_rules_rules" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 15),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    PRIMARY KEY("scope_key"),
    FOREIGN KEY("scope_key") REFERENCES "windows_film_rules"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_film_rules_rules_episodeCleanRegexRules" (
    "scope_key" TEXT NOT NULL,
    "episodeCleanRegexRules_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","episodeCleanRegexRules_key"),
    FOREIGN KEY("scope_key") REFERENCES "windows_film_rules_rules"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_film_rules_rules_aggregateRegexRules" (
    "scope_key" TEXT NOT NULL,
    "aggregateRegexRules_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","aggregateRegexRules_key"),
    FOREIGN KEY("scope_key") REFERENCES "windows_film_rules_rules"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_film_rules_rules_episodeRules" (
    "scope_key" TEXT NOT NULL,
    "episodeRules_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "pattern" TEXT,
    "replace" TEXT,
    "flags" TEXT,
    CHECK(("pattern" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("replace" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("flags" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    PRIMARY KEY("scope_key","episodeRules_key"),
    FOREIGN KEY("scope_key") REFERENCES "windows_film_rules_rules"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_film_rules_rules_movieRules" (
    "scope_key" TEXT NOT NULL,
    "movieRules_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "pattern" TEXT,
    "replace" TEXT,
    "flags" TEXT,
    CHECK(("pattern" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("replace" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("flags" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    PRIMARY KEY("scope_key","movieRules_key"),
    FOREIGN KEY("scope_key") REFERENCES "windows_film_rules_rules"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_film_preferences" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "version" INTEGER,
    CHECK(("version" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "windows_film_preferences_preferences" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 63),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "iosAutoPrepare" INTEGER CHECK("iosAutoPrepare" IN (0,1)),
    "metadataViewProvider" TEXT,
    CHECK(("iosAutoPrepare" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("metadataViewProvider" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    PRIMARY KEY("scope_key"),
    FOREIGN KEY("scope_key") REFERENCES "windows_film_preferences"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_film_preferences_preferences_ruleOrder" (
    "scope_key" TEXT NOT NULL,
    "ruleOrder_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","ruleOrder_key"),
    FOREIGN KEY("scope_key") REFERENCES "windows_film_preferences_preferences"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_film_preferences_preferences_panOrder" (
    "scope_key" TEXT NOT NULL,
    "panOrder_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","panOrder_key"),
    FOREIGN KEY("scope_key") REFERENCES "windows_film_preferences_preferences"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_film_preferences_preferences_keywords" (
    "scope_key" TEXT NOT NULL,
    "keywords_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","keywords_key"),
    FOREIGN KEY("scope_key") REFERENCES "windows_film_preferences_preferences"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_film_preferences_preferences_siteCleanKeywords" (
    "scope_key" TEXT NOT NULL,
    "siteCleanKeywords_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","siteCleanKeywords_key"),
    FOREIGN KEY("scope_key") REFERENCES "windows_film_preferences_preferences"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_cat_settings" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 31),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "version" INTEGER,
    "port" INTEGER,
    CHECK(("version" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("port" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "windows_cat_settings_providers" (
    "scope_key" TEXT NOT NULL,
    "providers_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" INTEGER CHECK("value" IN (0,1)),
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","providers_key"),
    FOREIGN KEY("scope_key") REFERENCES "windows_cat_settings"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_cat_settings_mappings" (
    "scope_key" TEXT NOT NULL,
    "mappings_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "provider" TEXT,
    CHECK(("provider" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","mappings_key"),
    FOREIGN KEY("scope_key") REFERENCES "windows_cat_settings"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_cat_settings_mappings_aliases" (
    "scope_key" TEXT NOT NULL,
    "mappings_key" TEXT NOT NULL,
    "aliases_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","mappings_key","aliases_key"),
    FOREIGN KEY("scope_key","mappings_key") REFERENCES "windows_cat_settings_mappings"("scope_key","mappings_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_cat_settings_scripts" (
    "scope_key" TEXT NOT NULL,
    "scripts_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 127),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "name" TEXT,
    "sourceType" TEXT,
    "sourceUrl" TEXT,
    "localPath" TEXT,
    "localHash" TEXT,
    "enabled" INTEGER CHECK("enabled" IN (0,1)),
    "inputRevision" INTEGER,
    CHECK(("name" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("sourceType" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("sourceUrl" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("localPath" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("localHash" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("enabled" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("inputRevision" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    PRIMARY KEY("scope_key","scripts_key"),
    FOREIGN KEY("scope_key") REFERENCES "windows_cat_settings"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_film_sources" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 15),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "version" INTEGER,
    CHECK(("version" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "windows_film_sources_cats" (
    "scope_key" TEXT NOT NULL,
    "cats_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 2047),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "name" TEXT,
    "sourceType" TEXT,
    "sourceUrl" TEXT,
    "enabled" INTEGER CHECK("enabled" IN (0,1)),
    "importedAt" INTEGER,
    "updatedAt" INTEGER,
    "updateState" TEXT,
    "updateError" TEXT,
    "sourceHash" TEXT,
    "inputRevision" INTEGER,
    CHECK(("name" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("sourceType" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("sourceUrl" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("enabled" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("importedAt" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("updatedAt" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("updateState" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("updateError" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("sourceHash" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("inputRevision" IS NULL)=((shape_mask & 512)=0 OR (null_mask & 512)<>0)),
    PRIMARY KEY("scope_key","cats_key"),
    FOREIGN KEY("scope_key") REFERENCES "windows_film_sources"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_film_sources_cats_sites" (
    "scope_key" TEXT NOT NULL,
    "cats_key" TEXT NOT NULL,
    "sites_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 15),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "name" TEXT,
    "api" TEXT,
    CHECK(("name" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("api" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    PRIMARY KEY("scope_key","cats_key","sites_key"),
    FOREIGN KEY("scope_key","cats_key") REFERENCES "windows_film_sources_cats"("scope_key","cats_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_film_sources_cats_sites_actions" (
    "scope_key" TEXT NOT NULL,
    "cats_key" TEXT NOT NULL,
    "sites_key" TEXT NOT NULL,
    "actions_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","cats_key","sites_key","actions_key"),
    FOREIGN KEY("scope_key","cats_key","sites_key") REFERENCES "windows_film_sources_cats_sites"("scope_key","cats_key","sites_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_film_sources_cats_sites_methods" (
    "scope_key" TEXT NOT NULL,
    "cats_key" TEXT NOT NULL,
    "sites_key" TEXT NOT NULL,
    "methods_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","cats_key","sites_key","methods_key"),
    FOREIGN KEY("scope_key","cats_key","sites_key") REFERENCES "windows_film_sources_cats_sites"("scope_key","cats_key","sites_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_film_sources_videos" (
    "scope_key" TEXT NOT NULL,
    "videos_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 255),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "name" TEXT,
    "api" TEXT,
    "order" INTEGER,
    CHECK(("name" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("api" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("order" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    PRIMARY KEY("scope_key","videos_key"),
    FOREIGN KEY("scope_key") REFERENCES "windows_film_sources"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_film_sources_videos_key" (
    "scope_key" TEXT NOT NULL,
    "videos_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "installationId" TEXT,
    "siteKey" TEXT,
    CHECK(("installationId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("siteKey" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    PRIMARY KEY("scope_key","videos_key"),
    FOREIGN KEY("scope_key","videos_key") REFERENCES "windows_film_sources_videos"("scope_key","videos_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_film_sources_videos_actions" (
    "scope_key" TEXT NOT NULL,
    "videos_key" TEXT NOT NULL,
    "actions_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","videos_key","actions_key"),
    FOREIGN KEY("scope_key","videos_key") REFERENCES "windows_film_sources_videos"("scope_key","videos_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_film_sources_videos_methods" (
    "scope_key" TEXT NOT NULL,
    "videos_key" TEXT NOT NULL,
    "methods_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","videos_key","methods_key"),
    FOREIGN KEY("scope_key","videos_key") REFERENCES "windows_film_sources_videos"("scope_key","videos_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_film_sources_videos_options" (
    "scope_key" TEXT NOT NULL,
    "videos_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 15),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "enabled" INTEGER CHECK("enabled" IN (0,1)),
    "homeVisible" INTEGER CHECK("homeVisible" IN (0,1)),
    "searchEnabled" INTEGER CHECK("searchEnabled" IN (0,1)),
    "aggregateImages" INTEGER CHECK("aggregateImages" IN (0,1)),
    CHECK(("enabled" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("homeVisible" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("searchEnabled" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("aggregateImages" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    PRIMARY KEY("scope_key","videos_key"),
    FOREIGN KEY("scope_key","videos_key") REFERENCES "windows_film_sources_videos"("scope_key","videos_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_film_sources_videos_detection" (
    "scope_key" TEXT NOT NULL,
    "videos_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 15),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "result" TEXT,
    "checkedAt" INTEGER,
    "playbackResolved" INTEGER CHECK("playbackResolved" IN (0,1)),
    CHECK(("result" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("checkedAt" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("playbackResolved" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    PRIMARY KEY("scope_key","videos_key"),
    FOREIGN KEY("scope_key","videos_key") REFERENCES "windows_film_sources_videos"("scope_key","videos_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_film_sources_videos_detection_steps" (
    "scope_key" TEXT NOT NULL,
    "videos_key" TEXT NOT NULL,
    "steps_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "operation" TEXT,
    "state" TEXT,
    "error" TEXT,
    CHECK(("operation" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("state" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("error" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    PRIMARY KEY("scope_key","videos_key","steps_key"),
    FOREIGN KEY("scope_key","videos_key") REFERENCES "windows_film_sources_videos_detection"("scope_key","videos_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_film_sources_deleted" (
    "scope_key" TEXT NOT NULL,
    "deleted_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "installationId" TEXT,
    "siteKey" TEXT,
    CHECK(("installationId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("siteKey" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    PRIMARY KEY("scope_key","deleted_key"),
    FOREIGN KEY("scope_key") REFERENCES "windows_film_sources"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_film_library" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "version" INTEGER,
    CHECK(("version" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "windows_film_library_favorites" (
    "scope_key" TEXT NOT NULL,
    "favorites_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 127),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "id" TEXT,
    "title" TEXT,
    "poster" TEXT,
    "score" TEXT,
    "textBadge" TEXT,
    "subtitle" TEXT,
    CHECK(("id" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("title" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("poster" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("score" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("textBadge" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("subtitle" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    PRIMARY KEY("scope_key","favorites_key"),
    FOREIGN KEY("scope_key") REFERENCES "windows_film_library"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_film_library_favorites_playbackContext" (
    "scope_key" TEXT NOT NULL,
    "favorites_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    PRIMARY KEY("scope_key","favorites_key"),
    FOREIGN KEY("scope_key","favorites_key") REFERENCES "windows_film_library_favorites"("scope_key","favorites_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_film_library_favorites_playbackContext_target" (
    "scope_key" TEXT NOT NULL,
    "favorites_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 2047),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "id" TEXT,
    "kind" TEXT,
    "title" TEXT,
    "metadataProvider" TEXT,
    "metadataId" TEXT,
    "year" INTEGER,
    "season" INTEGER,
    "episode" INTEGER,
    "globalEpisode" INTEGER,
    CHECK(("id" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("kind" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("title" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("metadataProvider" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("metadataId" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("year" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("season" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("episode" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("globalEpisode" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    PRIMARY KEY("scope_key","favorites_key"),
    FOREIGN KEY("scope_key","favorites_key") REFERENCES "windows_film_library_favorites_playbackContext"("scope_key","favorites_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_film_library_favorites_playbackContext_target_source" (
    "scope_key" TEXT NOT NULL,
    "favorites_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 63),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "key" TEXT,
    "engine" TEXT,
    "installationId" TEXT,
    "siteId_kind" TEXT CHECK("siteId_kind" IN ('text','number')),
    "siteId_value" TEXT,
    "detailId_kind" TEXT CHECK("detailId_kind" IN ('text','number')),
    "detailId_value" TEXT,
    "lineId_kind" TEXT CHECK("lineId_kind" IN ('text','number')),
    "lineId_value" TEXT,
    CHECK(("key" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("engine" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("installationId" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("siteId_kind" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("siteId_kind" IS NULL)=("siteId_value" IS NULL)),
    CHECK(("detailId_kind" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("detailId_kind" IS NULL)=("detailId_value" IS NULL)),
    CHECK(("lineId_kind" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("lineId_kind" IS NULL)=("lineId_value" IS NULL)),
    PRIMARY KEY("scope_key","favorites_key"),
    FOREIGN KEY("scope_key","favorites_key") REFERENCES "windows_film_library_favorites_playbackContext_target"("scope_key","favorites_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_film_library_favorites_playbackContext_target_members" (
    "scope_key" TEXT NOT NULL,
    "favorites_key" TEXT NOT NULL,
    "members_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "sourceKey" TEXT,
    "detailId_kind" TEXT CHECK("detailId_kind" IN ('text','number')),
    "detailId_value" TEXT,
    CHECK(("sourceKey" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("detailId_kind" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("detailId_kind" IS NULL)=("detailId_value" IS NULL)),
    PRIMARY KEY("scope_key","favorites_key","members_key"),
    FOREIGN KEY("scope_key","favorites_key") REFERENCES "windows_film_library_favorites_playbackContext_target"("scope_key","favorites_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_novel_library" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 15),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "version" INTEGER,
    CHECK(("version" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "windows_novel_library_entries" (
    "scope_key" TEXT NOT NULL,
    "entries_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 511),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "identityKey" TEXT,
    "onShelf" INTEGER CHECK("onShelf" IN (0,1)),
    "addedAt" TEXT,
    "pinnedAt" TEXT,
    "readAt" TEXT,
    "chapterCount" INTEGER,
    "contentSignature" TEXT,
    CHECK(("identityKey" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("onShelf" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("addedAt" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("pinnedAt" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("readAt" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("chapterCount" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("contentSignature" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    PRIMARY KEY("scope_key","entries_key"),
    FOREIGN KEY("scope_key") REFERENCES "windows_novel_library"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_novel_library_entries_book" (
    "scope_key" TEXT NOT NULL,
    "entries_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 511),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "installationId" TEXT,
    "revision" INTEGER,
    "bookUrl" TEXT,
    "name" TEXT,
    "author" TEXT,
    "coverUrl" TEXT,
    "intro" TEXT,
    "tocUrl" TEXT,
    "latestChapterTitle" TEXT,
    CHECK(("installationId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("revision" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("bookUrl" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("name" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("author" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("coverUrl" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("intro" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("tocUrl" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("latestChapterTitle" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    PRIMARY KEY("scope_key","entries_key"),
    FOREIGN KEY("scope_key","entries_key") REFERENCES "windows_novel_library_entries"("scope_key","entries_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_novel_library_entries_book_extensions" (
    "scope_key" TEXT NOT NULL,
    "entries_key" TEXT NOT NULL,
    node_id INTEGER NOT NULL CHECK(node_id>=0),
    parent_id INTEGER,
    prop_key TEXT,
    item_index INTEGER,
    kind TEXT NOT NULL CHECK(kind IN ('null','boolean','integer','real','number_text','text','array','object')),
    bool_value INTEGER CHECK(bool_value IN (0,1)),
    int_value INTEGER,
    real_value REAL,
    text_value TEXT,
    PRIMARY KEY("scope_key","entries_key","node_id"),
    FOREIGN KEY("scope_key","entries_key") REFERENCES "windows_novel_library_entries_book"("scope_key","entries_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_novel_library_entries_anchor" (
    "scope_key" TEXT NOT NULL,
    "entries_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 63),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "chapterUrl" TEXT,
    "chapterTitle" TEXT,
    "chapterIndex" INTEGER,
    "blockIndex" INTEGER,
    "utf16Offset" INTEGER,
    "ratio" REAL,
    CHECK(("chapterUrl" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("chapterTitle" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("chapterIndex" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("blockIndex" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("utf16Offset" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("ratio" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    PRIMARY KEY("scope_key","entries_key"),
    FOREIGN KEY("scope_key","entries_key") REFERENCES "windows_novel_library_entries"("scope_key","entries_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_novel_library_appearance" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 16383),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "mode" TEXT,
    "fontSize" REAL,
    "fontFamily" TEXT,
    "bold" INTEGER CHECK("bold" IN (0,1)),
    "lineHeight" REAL,
    "paragraphSpacing" REAL,
    "margin" REAL,
    "contentWidth" REAL,
    "conversion" TEXT,
    "theme" INTEGER,
    "background" TEXT,
    "foreground" TEXT,
    "backgroundImage" TEXT,
    "autoSpeed" REAL,
    CHECK(("mode" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("fontSize" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("fontFamily" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("bold" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("lineHeight" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("paragraphSpacing" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("margin" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("contentWidth" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("conversion" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("theme" IS NULL)=((shape_mask & 512)=0 OR (null_mask & 512)<>0)),
    CHECK(("background" IS NULL)=((shape_mask & 1024)=0 OR (null_mask & 1024)<>0)),
    CHECK(("foreground" IS NULL)=((shape_mask & 2048)=0 OR (null_mask & 2048)<>0)),
    CHECK(("backgroundImage" IS NULL)=((shape_mask & 4096)=0 OR (null_mask & 4096)<>0)),
    CHECK(("autoSpeed" IS NULL)=((shape_mask & 8192)=0 OR (null_mask & 8192)<>0)),
    PRIMARY KEY("scope_key"),
    FOREIGN KEY("scope_key") REFERENCES "windows_novel_library"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_novel_library_rules" (
    "scope_key" TEXT NOT NULL,
    "rules_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 4095),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "name" TEXT,
    "group" TEXT,
    "pattern" TEXT,
    "replacement" TEXT,
    "isRegex" INTEGER CHECK("isRegex" IN (0,1)),
    "isEnabled" INTEGER CHECK("isEnabled" IN (0,1)),
    "scopeTitle" INTEGER CHECK("scopeTitle" IN (0,1)),
    "scopeContent" INTEGER CHECK("scopeContent" IN (0,1)),
    "scope" TEXT,
    "excludeScope" TEXT,
    "order" INTEGER,
    "timeoutMillisecond" INTEGER,
    CHECK(("name" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("group" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("pattern" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("replacement" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("isRegex" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("isEnabled" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("scopeTitle" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("scopeContent" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("scope" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("excludeScope" IS NULL)=((shape_mask & 512)=0 OR (null_mask & 512)<>0)),
    CHECK(("order" IS NULL)=((shape_mask & 1024)=0 OR (null_mask & 1024)<>0)),
    CHECK(("timeoutMillisecond" IS NULL)=((shape_mask & 2048)=0 OR (null_mask & 2048)<>0)),
    PRIMARY KEY("scope_key","rules_key"),
    FOREIGN KEY("scope_key") REFERENCES "windows_novel_library"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_novel_library_rules_extensions" (
    "scope_key" TEXT NOT NULL,
    "rules_key" TEXT NOT NULL,
    node_id INTEGER NOT NULL CHECK(node_id>=0),
    parent_id INTEGER,
    prop_key TEXT,
    item_index INTEGER,
    kind TEXT NOT NULL CHECK(kind IN ('null','boolean','integer','real','number_text','text','array','object')),
    bool_value INTEGER CHECK(bool_value IN (0,1)),
    int_value INTEGER,
    real_value REAL,
    text_value TEXT,
    PRIMARY KEY("scope_key","rules_key","node_id"),
    FOREIGN KEY("scope_key","rules_key") REFERENCES "windows_novel_library_rules"("scope_key","rules_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_novel_sources" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "version" INTEGER,
    "revision" INTEGER,
    CHECK(("version" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("revision" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "windows_novel_sources_sources" (
    "scope_key" TEXT NOT NULL,
    "sources_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 255),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "revision" INTEGER,
    "name" TEXT,
    "sourceUrl" TEXT,
    "group" TEXT,
    "order" INTEGER,
    "enabled" INTEGER CHECK("enabled" IN (0,1)),
    "enabledExplore" INTEGER CHECK("enabledExplore" IN (0,1)),
    "protocol" TEXT,
    CHECK(("revision" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("name" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("sourceUrl" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("group" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("order" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("enabled" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("enabledExplore" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("protocol" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    PRIMARY KEY("scope_key","sources_key"),
    FOREIGN KEY("scope_key") REFERENCES "windows_novel_sources"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_novel_sources_sources_extensions" (
    "scope_key" TEXT NOT NULL,
    "sources_key" TEXT NOT NULL,
    node_id INTEGER NOT NULL CHECK(node_id>=0),
    parent_id INTEGER,
    prop_key TEXT,
    item_index INTEGER,
    kind TEXT NOT NULL CHECK(kind IN ('null','boolean','integer','real','number_text','text','array','object')),
    bool_value INTEGER CHECK(bool_value IN (0,1)),
    int_value INTEGER,
    real_value REAL,
    text_value TEXT,
    PRIMARY KEY("scope_key","sources_key","node_id"),
    FOREIGN KEY("scope_key","sources_key") REFERENCES "windows_novel_sources_sources"("scope_key","sources_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_feedback" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "version" INTEGER,
    "revision" INTEGER,
    CHECK(("version" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("revision" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "windows_feedback_enabled" (
    "scope_key" TEXT NOT NULL,
    "enabled_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","enabled_key"),
    FOREIGN KEY("scope_key") REFERENCES "windows_feedback"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_playback_outbox" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "version" INTEGER,
    CHECK(("version" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "windows_playback_outbox_playbacks" (
    "scope_key" TEXT NOT NULL,
    "playbacks_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1023),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "consentRevision" INTEGER,
    "recordingId" TEXT,
    "source" TEXT,
    "sourceId" TEXT,
    "algorithm" TEXT,
    "startConfirmed" INTEGER CHECK("startConfirmed" IN (0,1)),
    "playScene" TEXT,
    CHECK(("consentRevision" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("recordingId" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("source" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("sourceId" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("algorithm" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("startConfirmed" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("playScene" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    PRIMARY KEY("scope_key","playbacks_key"),
    FOREIGN KEY("scope_key") REFERENCES "windows_playback_outbox"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_playback_outbox_playbacks_account" (
    "scope_key" TEXT NOT NULL,
    "playbacks_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "scope" TEXT,
    "generation" INTEGER,
    "userId" TEXT,
    CHECK(("scope" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("generation" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("userId" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    PRIMARY KEY("scope_key","playbacks_key"),
    FOREIGN KEY("scope_key","playbacks_key") REFERENCES "windows_playback_outbox_playbacks"("scope_key","playbacks_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_playback_outbox_playbacks_start" (
    "scope_key" TEXT NOT NULL,
    "playbacks_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 127),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "id" TEXT,
    "at" INTEGER,
    "inflight" INTEGER CHECK("inflight" IN (0,1)),
    "time" REAL,
    "realtime" REAL,
    "reason" TEXT,
    "rate" REAL,
    CHECK(("id" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("at" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("inflight" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("time" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("realtime" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("reason" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("rate" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    PRIMARY KEY("scope_key","playbacks_key"),
    FOREIGN KEY("scope_key","playbacks_key") REFERENCES "windows_playback_outbox_playbacks"("scope_key","playbacks_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_playback_outbox_playbacks_end" (
    "scope_key" TEXT NOT NULL,
    "playbacks_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 127),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "id" TEXT,
    "at" INTEGER,
    "inflight" INTEGER CHECK("inflight" IN (0,1)),
    "time" REAL,
    "realtime" REAL,
    "reason" TEXT,
    "rate" REAL,
    CHECK(("id" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("at" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("inflight" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("time" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("realtime" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("reason" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("rate" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    PRIMARY KEY("scope_key","playbacks_key"),
    FOREIGN KEY("scope_key","playbacks_key") REFERENCES "windows_playback_outbox_playbacks"("scope_key","playbacks_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_fm_preference" (
    "account_id" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 15),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "mode" TEXT,
    "submode" TEXT,
    "ruleId" TEXT,
    "tabTagId" TEXT,
    CHECK(("mode" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("submode" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("ruleId" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("tabTagId" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    PRIMARY KEY("account_id")
) STRICT;

CREATE TABLE "windows_music_account" (
    "platform" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 63),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "version" INTEGER,
    "generation" INTEGER,
    "cookie" TEXT,
    "scope" TEXT,
    "validatedAt" INTEGER,
    CHECK(("version" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("generation" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("cookie" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("scope" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("validatedAt" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    PRIMARY KEY("platform")
) STRICT;

CREATE TABLE "windows_music_account_profile" (
    "platform" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 15),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "platform_field" TEXT,
    "userId" TEXT,
    "displayName" TEXT,
    "avatar" TEXT,
    CHECK(("platform_field" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("userId" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("displayName" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("avatar" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    PRIMARY KEY("platform"),
    FOREIGN KEY("platform") REFERENCES "windows_music_account"("platform") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_chorus" (
    "resource_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "at" INTEGER,
    "start" REAL,
    "end" REAL,
    CHECK(("at" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("start" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("end" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    PRIMARY KEY("resource_key")
) STRICT;

CREATE TABLE "windows_playback_source" (
    "account_scope" TEXT NOT NULL,
    "song_id" TEXT NOT NULL,
    "entry_kind" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 31),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "signature" TEXT,
    "at" INTEGER,
    "until" INTEGER,
    "storedBytes" INTEGER,
    CHECK(("signature" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("at" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("until" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("storedBytes" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    PRIMARY KEY("account_scope","song_id","entry_kind")
) STRICT;

CREATE TABLE "windows_playback_source_info" (
    "account_scope" TEXT NOT NULL,
    "song_id" TEXT NOT NULL,
    "entry_kind" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 4194303),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "ok" INTEGER CHECK("ok" IN (0,1)),
    "kind" TEXT,
    "platform" TEXT,
    "songId" TEXT,
    "sourceKey" TEXT,
    "scope" TEXT,
    "generation" INTEGER,
    "quality" TEXT,
    "actualQuality" TEXT,
    "qualityVerified" INTEGER CHECK("qualityVerified" IN (0,1)),
    "playbackVerified" INTEGER CHECK("playbackVerified" IN (0,1)),
    "qualityPolicySignature" TEXT,
    "qualityCacheSignature" TEXT,
    "url" TEXT,
    "expiresAt" INTEGER,
    "duration" REAL,
    "size" INTEGER,
    "full" INTEGER CHECK("full" IN (0,1)),
    "delivery" TEXT,
    "requestedQuality" TEXT,
    CHECK(("ok" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("kind" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("platform" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("songId" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("sourceKey" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("scope" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("generation" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("quality" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("actualQuality" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("qualityVerified" IS NULL)=((shape_mask & 512)=0 OR (null_mask & 512)<>0)),
    CHECK(("playbackVerified" IS NULL)=((shape_mask & 1024)=0 OR (null_mask & 1024)<>0)),
    CHECK(("qualityPolicySignature" IS NULL)=((shape_mask & 2048)=0 OR (null_mask & 2048)<>0)),
    CHECK(("qualityCacheSignature" IS NULL)=((shape_mask & 4096)=0 OR (null_mask & 4096)<>0)),
    CHECK(("url" IS NULL)=((shape_mask & 8192)=0 OR (null_mask & 8192)<>0)),
    CHECK(("expiresAt" IS NULL)=((shape_mask & 16384)=0 OR (null_mask & 16384)<>0)),
    CHECK(("duration" IS NULL)=((shape_mask & 32768)=0 OR (null_mask & 32768)<>0)),
    CHECK(("size" IS NULL)=((shape_mask & 65536)=0 OR (null_mask & 65536)<>0)),
    CHECK(("full" IS NULL)=((shape_mask & 131072)=0 OR (null_mask & 131072)<>0)),
    CHECK(("delivery" IS NULL)=((shape_mask & 262144)=0 OR (null_mask & 262144)<>0)),
    CHECK(("requestedQuality" IS NULL)=((shape_mask & 524288)=0 OR (null_mask & 524288)<>0)),
    FOREIGN KEY("songId") REFERENCES music_songs(song_id),
    PRIMARY KEY("account_scope","song_id","entry_kind"),
    FOREIGN KEY("account_scope","song_id","entry_kind") REFERENCES "windows_playback_source"("account_scope","song_id","entry_kind") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_playback_source_info_headers" (
    "account_scope" TEXT NOT NULL,
    "song_id" TEXT NOT NULL,
    "entry_kind" TEXT NOT NULL,
    "headers_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("account_scope","song_id","entry_kind","headers_key"),
    FOREIGN KEY("account_scope","song_id","entry_kind") REFERENCES "windows_playback_source_info"("account_scope","song_id","entry_kind") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_playback_source_info_recording" (
    "account_scope" TEXT NOT NULL,
    "song_id" TEXT NOT NULL,
    "entry_kind" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "source" TEXT,
    "sourceKey" TEXT,
    CHECK(("source" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("sourceKey" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    PRIMARY KEY("account_scope","song_id","entry_kind"),
    FOREIGN KEY("account_scope","song_id","entry_kind") REFERENCES "windows_playback_source_info"("account_scope","song_id","entry_kind") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_playback_source_info_recording_extensions" (
    "account_scope" TEXT NOT NULL,
    "song_id" TEXT NOT NULL,
    "entry_kind" TEXT NOT NULL,
    node_id INTEGER NOT NULL CHECK(node_id>=0),
    parent_id INTEGER,
    prop_key TEXT,
    item_index INTEGER,
    kind TEXT NOT NULL CHECK(kind IN ('null','boolean','integer','real','number_text','text','array','object')),
    bool_value INTEGER CHECK(bool_value IN (0,1)),
    int_value INTEGER,
    real_value REAL,
    text_value TEXT,
    PRIMARY KEY("account_scope","song_id","entry_kind","node_id"),
    FOREIGN KEY("account_scope","song_id","entry_kind") REFERENCES "windows_playback_source_info_recording"("account_scope","song_id","entry_kind") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_playback_source_info_recording_musicInfo" (
    "account_scope" TEXT NOT NULL,
    "song_id" TEXT NOT NULL,
    "entry_kind" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 4294967295),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "source" TEXT,
    "songmid_kind" TEXT CHECK("songmid_kind" IN ('text','number')),
    "songmid_value" TEXT,
    "mid_kind" TEXT CHECK("mid_kind" IN ('text','number')),
    "mid_value" TEXT,
    "id_kind" TEXT CHECK("id_kind" IN ('text','number')),
    "id_value" TEXT,
    "songId_kind" TEXT CHECK("songId_kind" IN ('text','number')),
    "songId_value" TEXT,
    "qqSongId_kind" TEXT CHECK("qqSongId_kind" IN ('text','number')),
    "qqSongId_value" TEXT,
    "songType_kind" TEXT CHECK("songType_kind" IN ('text','number')),
    "songType_value" TEXT,
    "type_kind" TEXT CHECK("type_kind" IN ('text','number')),
    "type_value" TEXT,
    "copyrightId_kind" TEXT CHECK("copyrightId_kind" IN ('text','number')),
    "copyrightId_value" TEXT,
    "strMediaMid_kind" TEXT CHECK("strMediaMid_kind" IN ('text','number')),
    "strMediaMid_value" TEXT,
    "hash" TEXT,
    "albumId_kind" TEXT CHECK("albumId_kind" IN ('text','number')),
    "albumId_value" TEXT,
    "album_id_kind" TEXT CHECK("album_id_kind" IN ('text','number')),
    "album_id_value" TEXT,
    "albumMid_kind" TEXT CHECK("albumMid_kind" IN ('text','number')),
    "albumMid_value" TEXT,
    "albumAudioId_kind" TEXT CHECK("albumAudioId_kind" IN ('text','number')),
    "albumAudioId_value" TEXT,
    "mixsongid_kind" TEXT CHECK("mixsongid_kind" IN ('text','number')),
    "mixsongid_value" TEXT,
    "fileid_kind" TEXT CHECK("fileid_kind" IN ('text','number')),
    "fileid_value" TEXT,
    "fileId_camel_kind" TEXT CHECK("fileId_camel_kind" IN ('text','number')),
    "fileId_camel_value" TEXT,
    "name" TEXT,
    "singer" TEXT,
    "albumName" TEXT,
    "duration_kind" TEXT CHECK("duration_kind" IN ('text','number')),
    "duration_value" TEXT,
    "size_kind" TEXT CHECK("size_kind" IN ('text','number')),
    "size_value" TEXT,
    "contentId_kind" TEXT CHECK("contentId_kind" IN ('text','number')),
    "contentId_value" TEXT,
    "md5" TEXT,
    "img" TEXT,
    "qualityKnown" INTEGER CHECK("qualityKnown" IN (0,1)),
    CHECK(("source" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("songmid_kind" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("songmid_kind" IS NULL)=("songmid_value" IS NULL)),
    CHECK(("mid_kind" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("mid_kind" IS NULL)=("mid_value" IS NULL)),
    CHECK(("id_kind" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("id_kind" IS NULL)=("id_value" IS NULL)),
    CHECK(("songId_kind" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("songId_kind" IS NULL)=("songId_value" IS NULL)),
    CHECK(("qqSongId_kind" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("qqSongId_kind" IS NULL)=("qqSongId_value" IS NULL)),
    CHECK(("songType_kind" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("songType_kind" IS NULL)=("songType_value" IS NULL)),
    CHECK(("type_kind" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("type_kind" IS NULL)=("type_value" IS NULL)),
    CHECK(("copyrightId_kind" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("copyrightId_kind" IS NULL)=("copyrightId_value" IS NULL)),
    CHECK(("strMediaMid_kind" IS NULL)=((shape_mask & 512)=0 OR (null_mask & 512)<>0)),
    CHECK(("strMediaMid_kind" IS NULL)=("strMediaMid_value" IS NULL)),
    CHECK(("hash" IS NULL)=((shape_mask & 1024)=0 OR (null_mask & 1024)<>0)),
    CHECK(("albumId_kind" IS NULL)=((shape_mask & 2048)=0 OR (null_mask & 2048)<>0)),
    CHECK(("albumId_kind" IS NULL)=("albumId_value" IS NULL)),
    CHECK(("album_id_kind" IS NULL)=((shape_mask & 4096)=0 OR (null_mask & 4096)<>0)),
    CHECK(("album_id_kind" IS NULL)=("album_id_value" IS NULL)),
    CHECK(("albumMid_kind" IS NULL)=((shape_mask & 8192)=0 OR (null_mask & 8192)<>0)),
    CHECK(("albumMid_kind" IS NULL)=("albumMid_value" IS NULL)),
    CHECK(("albumAudioId_kind" IS NULL)=((shape_mask & 16384)=0 OR (null_mask & 16384)<>0)),
    CHECK(("albumAudioId_kind" IS NULL)=("albumAudioId_value" IS NULL)),
    CHECK(("mixsongid_kind" IS NULL)=((shape_mask & 32768)=0 OR (null_mask & 32768)<>0)),
    CHECK(("mixsongid_kind" IS NULL)=("mixsongid_value" IS NULL)),
    CHECK(("fileid_kind" IS NULL)=((shape_mask & 65536)=0 OR (null_mask & 65536)<>0)),
    CHECK(("fileid_kind" IS NULL)=("fileid_value" IS NULL)),
    CHECK(("fileId_camel_kind" IS NULL)=((shape_mask & 131072)=0 OR (null_mask & 131072)<>0)),
    CHECK(("fileId_camel_kind" IS NULL)=("fileId_camel_value" IS NULL)),
    CHECK(("name" IS NULL)=((shape_mask & 262144)=0 OR (null_mask & 262144)<>0)),
    CHECK(("singer" IS NULL)=((shape_mask & 524288)=0 OR (null_mask & 524288)<>0)),
    CHECK(("albumName" IS NULL)=((shape_mask & 1048576)=0 OR (null_mask & 1048576)<>0)),
    CHECK(("duration_kind" IS NULL)=((shape_mask & 2097152)=0 OR (null_mask & 2097152)<>0)),
    CHECK(("duration_kind" IS NULL)=("duration_value" IS NULL)),
    CHECK(("size_kind" IS NULL)=((shape_mask & 4194304)=0 OR (null_mask & 4194304)<>0)),
    CHECK(("size_kind" IS NULL)=("size_value" IS NULL)),
    CHECK(("contentId_kind" IS NULL)=((shape_mask & 8388608)=0 OR (null_mask & 8388608)<>0)),
    CHECK(("contentId_kind" IS NULL)=("contentId_value" IS NULL)),
    CHECK(("md5" IS NULL)=((shape_mask & 16777216)=0 OR (null_mask & 16777216)<>0)),
    CHECK(("img" IS NULL)=((shape_mask & 33554432)=0 OR (null_mask & 33554432)<>0)),
    CHECK(("qualityKnown" IS NULL)=((shape_mask & 67108864)=0 OR (null_mask & 67108864)<>0)),
    PRIMARY KEY("account_scope","song_id","entry_kind"),
    FOREIGN KEY("account_scope","song_id","entry_kind") REFERENCES "windows_playback_source_info_recording"("account_scope","song_id","entry_kind") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_playback_source_info_recording_musicInfo_extensions" (
    "account_scope" TEXT NOT NULL,
    "song_id" TEXT NOT NULL,
    "entry_kind" TEXT NOT NULL,
    node_id INTEGER NOT NULL CHECK(node_id>=0),
    parent_id INTEGER,
    prop_key TEXT,
    item_index INTEGER,
    kind TEXT NOT NULL CHECK(kind IN ('null','boolean','integer','real','number_text','text','array','object')),
    bool_value INTEGER CHECK(bool_value IN (0,1)),
    int_value INTEGER,
    real_value REAL,
    text_value TEXT,
    PRIMARY KEY("account_scope","song_id","entry_kind","node_id"),
    FOREIGN KEY("account_scope","song_id","entry_kind") REFERENCES "windows_playback_source_info_recording_musicInfo"("account_scope","song_id","entry_kind") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_playback_source_info_recording_musicInfo_artistNames" (
    "account_scope" TEXT NOT NULL,
    "song_id" TEXT NOT NULL,
    "entry_kind" TEXT NOT NULL,
    "artistNames_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("account_scope","song_id","entry_kind","artistNames_key"),
    FOREIGN KEY("account_scope","song_id","entry_kind") REFERENCES "windows_playback_source_info_recording_musicInfo"("account_scope","song_id","entry_kind") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_playback_source_info_recording_musicInfo_types" (
    "account_scope" TEXT NOT NULL,
    "song_id" TEXT NOT NULL,
    "entry_kind" TEXT NOT NULL,
    "types_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "type_kind" TEXT CHECK("type_kind" IN ('text','number')),
    "type_value" TEXT,
    "hash" TEXT,
    "size_kind" TEXT CHECK("size_kind" IN ('text','number')),
    "size_value" TEXT,
    CHECK(("type_kind" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("type_kind" IS NULL)=("type_value" IS NULL)),
    CHECK(("hash" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("size_kind" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("size_kind" IS NULL)=("size_value" IS NULL)),
    PRIMARY KEY("account_scope","song_id","entry_kind","types_key"),
    FOREIGN KEY("account_scope","song_id","entry_kind") REFERENCES "windows_playback_source_info_recording_musicInfo"("account_scope","song_id","entry_kind") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_playback_source_info_recording_musicInfo__types" (
    "account_scope" TEXT NOT NULL,
    "song_id" TEXT NOT NULL,
    "entry_kind" TEXT NOT NULL,
    "_types_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "hash" TEXT,
    "size_kind" TEXT CHECK("size_kind" IN ('text','number')),
    "size_value" TEXT,
    CHECK(("hash" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("size_kind" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("size_kind" IS NULL)=("size_value" IS NULL)),
    PRIMARY KEY("account_scope","song_id","entry_kind","_types_key"),
    FOREIGN KEY("account_scope","song_id","entry_kind") REFERENCES "windows_playback_source_info_recording_musicInfo"("account_scope","song_id","entry_kind") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_playback_source_info_recording_musicInfo_audioFormats" (
    "account_scope" TEXT NOT NULL,
    "song_id" TEXT NOT NULL,
    "entry_kind" TEXT NOT NULL,
    "audioFormats_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 33554431),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "formatType_kind" TEXT CHECK("formatType_kind" IN ('text','number')),
    "formatType_value" TEXT,
    "resourceType_kind" TEXT CHECK("resourceType_kind" IN ('text','number')),
    "resourceType_value" TEXT,
    "size_kind" TEXT CHECK("size_kind" IN ('text','number')),
    "size_value" TEXT,
    "androidSize_kind" TEXT CHECK("androidSize_kind" IN ('text','number')),
    "androidSize_value" TEXT,
    "iosSize_kind" TEXT CHECK("iosSize_kind" IN ('text','number')),
    "iosSize_value" TEXT,
    "asize_kind" TEXT CHECK("asize_kind" IN ('text','number')),
    "asize_value" TEXT,
    "isize_kind" TEXT CHECK("isize_kind" IN ('text','number')),
    "isize_value" TEXT,
    "format_kind" TEXT CHECK("format_kind" IN ('text','number')),
    "format_value" TEXT,
    "androidFormat_kind" TEXT CHECK("androidFormat_kind" IN ('text','number')),
    "androidFormat_value" TEXT,
    "iosFormat_kind" TEXT CHECK("iosFormat_kind" IN ('text','number')),
    "iosFormat_value" TEXT,
    "aformat_kind" TEXT CHECK("aformat_kind" IN ('text','number')),
    "aformat_value" TEXT,
    "iformat_kind" TEXT CHECK("iformat_kind" IN ('text','number')),
    "iformat_value" TEXT,
    "bitrate_kind" TEXT CHECK("bitrate_kind" IN ('text','number')),
    "bitrate_value" TEXT,
    "sampleRate_kind" TEXT CHECK("sampleRate_kind" IN ('text','number')),
    "sampleRate_value" TEXT,
    "bitDepth_kind" TEXT CHECK("bitDepth_kind" IN ('text','number')),
    "bitDepth_value" TEXT,
    "fileType_kind" TEXT CHECK("fileType_kind" IN ('text','number')),
    "fileType_value" TEXT,
    "androidFileType_kind" TEXT CHECK("androidFileType_kind" IN ('text','number')),
    "androidFileType_value" TEXT,
    "iosFileType_kind" TEXT CHECK("iosFileType_kind" IN ('text','number')),
    "iosFileType_value" TEXT,
    "iosAccuracyLevel_kind" TEXT CHECK("iosAccuracyLevel_kind" IN ('text','number')),
    "iosAccuracyLevel_value" TEXT,
    "androidAccuracyLevel_kind" TEXT CHECK("androidAccuracyLevel_kind" IN ('text','number')),
    "androidAccuracyLevel_value" TEXT,
    "hash" TEXT,
    "md5" TEXT,
    "androidMd5" TEXT,
    "fileId_kind" TEXT CHECK("fileId_kind" IN ('text','number')),
    "fileId_value" TEXT,
    "contentId_kind" TEXT CHECK("contentId_kind" IN ('text','number')),
    "contentId_value" TEXT,
    CHECK(("formatType_kind" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("formatType_kind" IS NULL)=("formatType_value" IS NULL)),
    CHECK(("resourceType_kind" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("resourceType_kind" IS NULL)=("resourceType_value" IS NULL)),
    CHECK(("size_kind" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("size_kind" IS NULL)=("size_value" IS NULL)),
    CHECK(("androidSize_kind" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("androidSize_kind" IS NULL)=("androidSize_value" IS NULL)),
    CHECK(("iosSize_kind" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("iosSize_kind" IS NULL)=("iosSize_value" IS NULL)),
    CHECK(("asize_kind" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("asize_kind" IS NULL)=("asize_value" IS NULL)),
    CHECK(("isize_kind" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("isize_kind" IS NULL)=("isize_value" IS NULL)),
    CHECK(("format_kind" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("format_kind" IS NULL)=("format_value" IS NULL)),
    CHECK(("androidFormat_kind" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("androidFormat_kind" IS NULL)=("androidFormat_value" IS NULL)),
    CHECK(("iosFormat_kind" IS NULL)=((shape_mask & 512)=0 OR (null_mask & 512)<>0)),
    CHECK(("iosFormat_kind" IS NULL)=("iosFormat_value" IS NULL)),
    CHECK(("aformat_kind" IS NULL)=((shape_mask & 1024)=0 OR (null_mask & 1024)<>0)),
    CHECK(("aformat_kind" IS NULL)=("aformat_value" IS NULL)),
    CHECK(("iformat_kind" IS NULL)=((shape_mask & 2048)=0 OR (null_mask & 2048)<>0)),
    CHECK(("iformat_kind" IS NULL)=("iformat_value" IS NULL)),
    CHECK(("bitrate_kind" IS NULL)=((shape_mask & 4096)=0 OR (null_mask & 4096)<>0)),
    CHECK(("bitrate_kind" IS NULL)=("bitrate_value" IS NULL)),
    CHECK(("sampleRate_kind" IS NULL)=((shape_mask & 8192)=0 OR (null_mask & 8192)<>0)),
    CHECK(("sampleRate_kind" IS NULL)=("sampleRate_value" IS NULL)),
    CHECK(("bitDepth_kind" IS NULL)=((shape_mask & 16384)=0 OR (null_mask & 16384)<>0)),
    CHECK(("bitDepth_kind" IS NULL)=("bitDepth_value" IS NULL)),
    CHECK(("fileType_kind" IS NULL)=((shape_mask & 32768)=0 OR (null_mask & 32768)<>0)),
    CHECK(("fileType_kind" IS NULL)=("fileType_value" IS NULL)),
    CHECK(("androidFileType_kind" IS NULL)=((shape_mask & 65536)=0 OR (null_mask & 65536)<>0)),
    CHECK(("androidFileType_kind" IS NULL)=("androidFileType_value" IS NULL)),
    CHECK(("iosFileType_kind" IS NULL)=((shape_mask & 131072)=0 OR (null_mask & 131072)<>0)),
    CHECK(("iosFileType_kind" IS NULL)=("iosFileType_value" IS NULL)),
    CHECK(("iosAccuracyLevel_kind" IS NULL)=((shape_mask & 262144)=0 OR (null_mask & 262144)<>0)),
    CHECK(("iosAccuracyLevel_kind" IS NULL)=("iosAccuracyLevel_value" IS NULL)),
    CHECK(("androidAccuracyLevel_kind" IS NULL)=((shape_mask & 524288)=0 OR (null_mask & 524288)<>0)),
    CHECK(("androidAccuracyLevel_kind" IS NULL)=("androidAccuracyLevel_value" IS NULL)),
    CHECK(("hash" IS NULL)=((shape_mask & 1048576)=0 OR (null_mask & 1048576)<>0)),
    CHECK(("md5" IS NULL)=((shape_mask & 2097152)=0 OR (null_mask & 2097152)<>0)),
    CHECK(("androidMd5" IS NULL)=((shape_mask & 4194304)=0 OR (null_mask & 4194304)<>0)),
    CHECK(("fileId_kind" IS NULL)=((shape_mask & 8388608)=0 OR (null_mask & 8388608)<>0)),
    CHECK(("fileId_kind" IS NULL)=("fileId_value" IS NULL)),
    CHECK(("contentId_kind" IS NULL)=((shape_mask & 16777216)=0 OR (null_mask & 16777216)<>0)),
    CHECK(("contentId_kind" IS NULL)=("contentId_value" IS NULL)),
    PRIMARY KEY("account_scope","song_id","entry_kind","audioFormats_key"),
    FOREIGN KEY("account_scope","song_id","entry_kind") REFERENCES "windows_playback_source_info_recording_musicInfo"("account_scope","song_id","entry_kind") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_playback_source_info_recording_musicInfo_newRateFormats" (
    "account_scope" TEXT NOT NULL,
    "song_id" TEXT NOT NULL,
    "entry_kind" TEXT NOT NULL,
    "newRateFormats_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 33554431),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "formatType_kind" TEXT CHECK("formatType_kind" IN ('text','number')),
    "formatType_value" TEXT,
    "resourceType_kind" TEXT CHECK("resourceType_kind" IN ('text','number')),
    "resourceType_value" TEXT,
    "size_kind" TEXT CHECK("size_kind" IN ('text','number')),
    "size_value" TEXT,
    "androidSize_kind" TEXT CHECK("androidSize_kind" IN ('text','number')),
    "androidSize_value" TEXT,
    "iosSize_kind" TEXT CHECK("iosSize_kind" IN ('text','number')),
    "iosSize_value" TEXT,
    "asize_kind" TEXT CHECK("asize_kind" IN ('text','number')),
    "asize_value" TEXT,
    "isize_kind" TEXT CHECK("isize_kind" IN ('text','number')),
    "isize_value" TEXT,
    "format_kind" TEXT CHECK("format_kind" IN ('text','number')),
    "format_value" TEXT,
    "androidFormat_kind" TEXT CHECK("androidFormat_kind" IN ('text','number')),
    "androidFormat_value" TEXT,
    "iosFormat_kind" TEXT CHECK("iosFormat_kind" IN ('text','number')),
    "iosFormat_value" TEXT,
    "aformat_kind" TEXT CHECK("aformat_kind" IN ('text','number')),
    "aformat_value" TEXT,
    "iformat_kind" TEXT CHECK("iformat_kind" IN ('text','number')),
    "iformat_value" TEXT,
    "bitrate_kind" TEXT CHECK("bitrate_kind" IN ('text','number')),
    "bitrate_value" TEXT,
    "sampleRate_kind" TEXT CHECK("sampleRate_kind" IN ('text','number')),
    "sampleRate_value" TEXT,
    "bitDepth_kind" TEXT CHECK("bitDepth_kind" IN ('text','number')),
    "bitDepth_value" TEXT,
    "fileType_kind" TEXT CHECK("fileType_kind" IN ('text','number')),
    "fileType_value" TEXT,
    "androidFileType_kind" TEXT CHECK("androidFileType_kind" IN ('text','number')),
    "androidFileType_value" TEXT,
    "iosFileType_kind" TEXT CHECK("iosFileType_kind" IN ('text','number')),
    "iosFileType_value" TEXT,
    "iosAccuracyLevel_kind" TEXT CHECK("iosAccuracyLevel_kind" IN ('text','number')),
    "iosAccuracyLevel_value" TEXT,
    "androidAccuracyLevel_kind" TEXT CHECK("androidAccuracyLevel_kind" IN ('text','number')),
    "androidAccuracyLevel_value" TEXT,
    "hash" TEXT,
    "md5" TEXT,
    "androidMd5" TEXT,
    "fileId_kind" TEXT CHECK("fileId_kind" IN ('text','number')),
    "fileId_value" TEXT,
    "contentId_kind" TEXT CHECK("contentId_kind" IN ('text','number')),
    "contentId_value" TEXT,
    CHECK(("formatType_kind" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("formatType_kind" IS NULL)=("formatType_value" IS NULL)),
    CHECK(("resourceType_kind" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("resourceType_kind" IS NULL)=("resourceType_value" IS NULL)),
    CHECK(("size_kind" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("size_kind" IS NULL)=("size_value" IS NULL)),
    CHECK(("androidSize_kind" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("androidSize_kind" IS NULL)=("androidSize_value" IS NULL)),
    CHECK(("iosSize_kind" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("iosSize_kind" IS NULL)=("iosSize_value" IS NULL)),
    CHECK(("asize_kind" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("asize_kind" IS NULL)=("asize_value" IS NULL)),
    CHECK(("isize_kind" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("isize_kind" IS NULL)=("isize_value" IS NULL)),
    CHECK(("format_kind" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("format_kind" IS NULL)=("format_value" IS NULL)),
    CHECK(("androidFormat_kind" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("androidFormat_kind" IS NULL)=("androidFormat_value" IS NULL)),
    CHECK(("iosFormat_kind" IS NULL)=((shape_mask & 512)=0 OR (null_mask & 512)<>0)),
    CHECK(("iosFormat_kind" IS NULL)=("iosFormat_value" IS NULL)),
    CHECK(("aformat_kind" IS NULL)=((shape_mask & 1024)=0 OR (null_mask & 1024)<>0)),
    CHECK(("aformat_kind" IS NULL)=("aformat_value" IS NULL)),
    CHECK(("iformat_kind" IS NULL)=((shape_mask & 2048)=0 OR (null_mask & 2048)<>0)),
    CHECK(("iformat_kind" IS NULL)=("iformat_value" IS NULL)),
    CHECK(("bitrate_kind" IS NULL)=((shape_mask & 4096)=0 OR (null_mask & 4096)<>0)),
    CHECK(("bitrate_kind" IS NULL)=("bitrate_value" IS NULL)),
    CHECK(("sampleRate_kind" IS NULL)=((shape_mask & 8192)=0 OR (null_mask & 8192)<>0)),
    CHECK(("sampleRate_kind" IS NULL)=("sampleRate_value" IS NULL)),
    CHECK(("bitDepth_kind" IS NULL)=((shape_mask & 16384)=0 OR (null_mask & 16384)<>0)),
    CHECK(("bitDepth_kind" IS NULL)=("bitDepth_value" IS NULL)),
    CHECK(("fileType_kind" IS NULL)=((shape_mask & 32768)=0 OR (null_mask & 32768)<>0)),
    CHECK(("fileType_kind" IS NULL)=("fileType_value" IS NULL)),
    CHECK(("androidFileType_kind" IS NULL)=((shape_mask & 65536)=0 OR (null_mask & 65536)<>0)),
    CHECK(("androidFileType_kind" IS NULL)=("androidFileType_value" IS NULL)),
    CHECK(("iosFileType_kind" IS NULL)=((shape_mask & 131072)=0 OR (null_mask & 131072)<>0)),
    CHECK(("iosFileType_kind" IS NULL)=("iosFileType_value" IS NULL)),
    CHECK(("iosAccuracyLevel_kind" IS NULL)=((shape_mask & 262144)=0 OR (null_mask & 262144)<>0)),
    CHECK(("iosAccuracyLevel_kind" IS NULL)=("iosAccuracyLevel_value" IS NULL)),
    CHECK(("androidAccuracyLevel_kind" IS NULL)=((shape_mask & 524288)=0 OR (null_mask & 524288)<>0)),
    CHECK(("androidAccuracyLevel_kind" IS NULL)=("androidAccuracyLevel_value" IS NULL)),
    CHECK(("hash" IS NULL)=((shape_mask & 1048576)=0 OR (null_mask & 1048576)<>0)),
    CHECK(("md5" IS NULL)=((shape_mask & 2097152)=0 OR (null_mask & 2097152)<>0)),
    CHECK(("androidMd5" IS NULL)=((shape_mask & 4194304)=0 OR (null_mask & 4194304)<>0)),
    CHECK(("fileId_kind" IS NULL)=((shape_mask & 8388608)=0 OR (null_mask & 8388608)<>0)),
    CHECK(("fileId_kind" IS NULL)=("fileId_value" IS NULL)),
    CHECK(("contentId_kind" IS NULL)=((shape_mask & 16777216)=0 OR (null_mask & 16777216)<>0)),
    CHECK(("contentId_kind" IS NULL)=("contentId_value" IS NULL)),
    PRIMARY KEY("account_scope","song_id","entry_kind","newRateFormats_key"),
    FOREIGN KEY("account_scope","song_id","entry_kind") REFERENCES "windows_playback_source_info_recording_musicInfo"("account_scope","song_id","entry_kind") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_lyrics" (
    "song_id" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 15),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "sourceKey" TEXT,
    "updatedAt" INTEGER,
    "storedBytes" INTEGER,
    CHECK(("sourceKey" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("updatedAt" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("storedBytes" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    PRIMARY KEY("song_id")
) STRICT;

CREATE TABLE "windows_lyrics_lines" (
    "song_id" TEXT NOT NULL,
    "lines_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 63),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "time" REAL,
    "text" TEXT,
    "translation" TEXT,
    "romanization" TEXT,
    "end" REAL,
    CHECK(("time" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("text" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("translation" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("romanization" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("end" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    PRIMARY KEY("song_id","lines_key"),
    FOREIGN KEY("song_id") REFERENCES "windows_lyrics"("song_id") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_lyrics_lines_words" (
    "song_id" TEXT NOT NULL,
    "lines_key" TEXT NOT NULL,
    "words_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 15),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "time" REAL,
    "text" TEXT,
    "duration" REAL,
    "offset" REAL,
    CHECK(("time" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("text" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("duration" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("offset" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    PRIMARY KEY("song_id","lines_key","words_key"),
    FOREIGN KEY("song_id","lines_key") REFERENCES "windows_lyrics_lines"("song_id","lines_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_account_details" (
    "platform" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 255),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "accountGeneration" INTEGER,
    "accountScope" TEXT,
    "accountFingerprint" TEXT,
    "userId" TEXT,
    "profileFetchedAt" INTEGER,
    "profileExpiresAt" INTEGER,
    CHECK(("accountGeneration" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("accountScope" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("accountFingerprint" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("userId" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("profileFetchedAt" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("profileExpiresAt" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    PRIMARY KEY("platform")
) STRICT;

CREATE TABLE "windows_account_details_profile" (
    "platform" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 15),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "platform_field" TEXT,
    "userId" TEXT,
    "displayName" TEXT,
    "avatar" TEXT,
    CHECK(("platform_field" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("userId" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("displayName" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("avatar" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    PRIMARY KEY("platform"),
    FOREIGN KEY("platform") REFERENCES "windows_account_details"("platform") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_account_details_rights" (
    "platform" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 511),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "vip" INTEGER CHECK("vip" IN (0,1)),
    "tier" TEXT,
    "expiresAt" INTEGER,
    "checkedAt" INTEGER,
    "recheckAt" INTEGER,
    "verificationDeferred" INTEGER CHECK("verificationDeferred" IN (0,1)),
    "errorCode" TEXT,
    "retryAt" INTEGER,
    CHECK(("vip" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("tier" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("expiresAt" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("checkedAt" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("recheckAt" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("verificationDeferred" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("errorCode" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("retryAt" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    PRIMARY KEY("platform"),
    FOREIGN KEY("platform") REFERENCES "windows_account_details"("platform") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_account_details_rights_audioLevels" (
    "platform" TEXT NOT NULL,
    "audioLevels_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("platform","audioLevels_key"),
    FOREIGN KEY("platform") REFERENCES "windows_account_details_rights"("platform") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_home" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "platform" TEXT,
    CHECK(("platform" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "windows_home_pages" (
    "scope_key" TEXT NOT NULL,
    "pages_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "platform" TEXT,
    CHECK(("platform" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","pages_key"),
    FOREIGN KEY("scope_key") REFERENCES "windows_home"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_home_pages_sections" (
    "scope_key" TEXT NOT NULL,
    "pages_key" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 16383),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "id_kind" TEXT CHECK("id_kind" IN ('text','number')),
    "id_value" TEXT,
    "title" TEXT,
    "type" TEXT,
    "layout" TEXT,
    "available" INTEGER CHECK("available" IN (0,1)),
    "errorCode" TEXT,
    "requiresAccount" INTEGER CHECK("requiresAccount" IN (0,1)),
    "display" INTEGER CHECK("display" IN (0,1)),
    "selectedTab" TEXT,
    "selectedTag" TEXT,
    CHECK(("id_kind" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("id_kind" IS NULL)=("id_value" IS NULL)),
    CHECK(("title" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("type" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("layout" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("available" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("errorCode" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("requiresAccount" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("display" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("selectedTab" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("selectedTag" IS NULL)=((shape_mask & 512)=0 OR (null_mask & 512)<>0)),
    PRIMARY KEY("scope_key","pages_key","sections_key"),
    FOREIGN KEY("scope_key","pages_key") REFERENCES "windows_home_pages"("scope_key","pages_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_home_pages_sections_action" (
    "scope_key" TEXT NOT NULL,
    "pages_key" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 131071),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "type" TEXT,
    "id_kind" TEXT CHECK("id_kind" IN ('text','number')),
    "id_value" TEXT,
    "branch" TEXT,
    "pageCode" TEXT,
    "provider" TEXT,
    "mode" TEXT,
    "submode" TEXT,
    "ruleId" TEXT,
    "tabTagId" TEXT,
    "sourceType" TEXT,
    "sourceName" TEXT,
    "entry" TEXT,
    "songId" TEXT,
    CHECK(("type" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("id_kind" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("id_kind" IS NULL)=("id_value" IS NULL)),
    CHECK(("branch" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("pageCode" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("provider" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("mode" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("submode" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("ruleId" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("tabTagId" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("sourceType" IS NULL)=((shape_mask & 512)=0 OR (null_mask & 512)<>0)),
    CHECK(("sourceName" IS NULL)=((shape_mask & 1024)=0 OR (null_mask & 1024)<>0)),
    CHECK(("entry" IS NULL)=((shape_mask & 2048)=0 OR (null_mask & 2048)<>0)),
    CHECK(("songId" IS NULL)=((shape_mask & 4096)=0 OR (null_mask & 4096)<>0)),
    PRIMARY KEY("scope_key","pages_key","sections_key"),
    FOREIGN KEY("scope_key","pages_key","sections_key") REFERENCES "windows_home_pages_sections"("scope_key","pages_key","sections_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_home_pages_sections_action_channel" (
    "scope_key" TEXT NOT NULL,
    "pages_key" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 31),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "id_kind" TEXT CHECK("id_kind" IN ('text','number')),
    "id_value" TEXT,
    "name" TEXT,
    "pageCode" TEXT,
    "channelKind" TEXT,
    "placement" TEXT,
    CHECK(("id_kind" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("id_kind" IS NULL)=("id_value" IS NULL)),
    CHECK(("name" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("pageCode" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("channelKind" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("placement" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    PRIMARY KEY("scope_key","pages_key","sections_key"),
    FOREIGN KEY("scope_key","pages_key","sections_key") REFERENCES "windows_home_pages_sections_action"("scope_key","pages_key","sections_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_home_pages_sections_action_sourceIds" (
    "scope_key" TEXT NOT NULL,
    "pages_key" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "sourceIds_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","pages_key","sections_key","sourceIds_key"),
    FOREIGN KEY("scope_key","pages_key","sections_key") REFERENCES "windows_home_pages_sections_action"("scope_key","pages_key","sections_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_home_pages_sections_action_ids" (
    "scope_key" TEXT NOT NULL,
    "pages_key" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "ids_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","pages_key","sections_key","ids_key"),
    FOREIGN KEY("scope_key","pages_key","sections_key") REFERENCES "windows_home_pages_sections_action"("scope_key","pages_key","sections_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_home_pages_sections_action_locator" (
    "scope_key" TEXT NOT NULL,
    "pages_key" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 511),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "kind" TEXT,
    "id_kind" TEXT CHECK("id_kind" IN ('text','number')),
    "id_value" TEXT,
    "playlistId_kind" TEXT CHECK("playlistId_kind" IN ('text','number')),
    "playlistId_value" TEXT,
    "dirId_kind" TEXT CHECK("dirId_kind" IN ('text','number')),
    "dirId_value" TEXT,
    "listId_kind" TEXT CHECK("listId_kind" IN ('text','number')),
    "listId_value" TEXT,
    "version_kind" TEXT CHECK("version_kind" IN ('text','number')),
    "version_value" TEXT,
    "totalVersion_kind" TEXT CHECK("totalVersion_kind" IN ('text','number')),
    "totalVersion_value" TEXT,
    "creatorId_kind" TEXT CHECK("creatorId_kind" IN ('text','number')),
    "creatorId_value" TEXT,
    "creatorListId_kind" TEXT CHECK("creatorListId_kind" IN ('text','number')),
    "creatorListId_value" TEXT,
    CHECK(("kind" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("id_kind" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("id_kind" IS NULL)=("id_value" IS NULL)),
    CHECK(("playlistId_kind" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("playlistId_kind" IS NULL)=("playlistId_value" IS NULL)),
    CHECK(("dirId_kind" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("dirId_kind" IS NULL)=("dirId_value" IS NULL)),
    CHECK(("listId_kind" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("listId_kind" IS NULL)=("listId_value" IS NULL)),
    CHECK(("version_kind" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("version_kind" IS NULL)=("version_value" IS NULL)),
    CHECK(("totalVersion_kind" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("totalVersion_kind" IS NULL)=("totalVersion_value" IS NULL)),
    CHECK(("creatorId_kind" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("creatorId_kind" IS NULL)=("creatorId_value" IS NULL)),
    CHECK(("creatorListId_kind" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("creatorListId_kind" IS NULL)=("creatorListId_value" IS NULL)),
    PRIMARY KEY("scope_key","pages_key","sections_key"),
    FOREIGN KEY("scope_key","pages_key","sections_key") REFERENCES "windows_home_pages_sections_action"("scope_key","pages_key","sections_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_home_pages_sections_items" (
    "scope_key" TEXT NOT NULL,
    "pages_key" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 34359738367),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "songId" TEXT,
    "id_kind" TEXT CHECK("id_kind" IN ('text','number')),
    "id_value" TEXT,
    "key_kind" TEXT CHECK("key_kind" IN ('text','number')),
    "key_value" TEXT,
    "name" TEXT,
    "title" TEXT,
    "label" TEXT,
    "subtitle" TEXT,
    "contextText" TEXT,
    "creator" TEXT,
    "cover" TEXT,
    "coverLabel" TEXT,
    "kind" TEXT,
    "pageCode" TEXT,
    "channelKind" TEXT,
    "available" INTEGER CHECK("available" IN (0,1)),
    "errorCode" TEXT,
    "requiresAccount" INTEGER CHECK("requiresAccount" IN (0,1)),
    "display" INTEGER CHECK("display" IN (0,1)),
    "placement" TEXT,
    "canPlay" INTEGER CHECK("canPlay" IN (0,1)),
    "rankText" TEXT,
    "salesText" TEXT,
    "relationship" TEXT,
    "count" INTEGER,
    "playCount" INTEGER,
    "presentation" TEXT,
    "dayOfMonth" INTEGER,
    "decoration" TEXT,
    "updateFrequency" TEXT,
    CHECK(("songId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("id_kind" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("id_kind" IS NULL)=("id_value" IS NULL)),
    CHECK(("key_kind" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("key_kind" IS NULL)=("key_value" IS NULL)),
    CHECK(("name" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("title" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("label" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("subtitle" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("contextText" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("creator" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("cover" IS NULL)=((shape_mask & 512)=0 OR (null_mask & 512)<>0)),
    CHECK(("coverLabel" IS NULL)=((shape_mask & 1024)=0 OR (null_mask & 1024)<>0)),
    CHECK(("kind" IS NULL)=((shape_mask & 2048)=0 OR (null_mask & 2048)<>0)),
    CHECK(("pageCode" IS NULL)=((shape_mask & 4096)=0 OR (null_mask & 4096)<>0)),
    CHECK(("channelKind" IS NULL)=((shape_mask & 8192)=0 OR (null_mask & 8192)<>0)),
    CHECK(("available" IS NULL)=((shape_mask & 16384)=0 OR (null_mask & 16384)<>0)),
    CHECK(("errorCode" IS NULL)=((shape_mask & 32768)=0 OR (null_mask & 32768)<>0)),
    CHECK(("requiresAccount" IS NULL)=((shape_mask & 65536)=0 OR (null_mask & 65536)<>0)),
    CHECK(("display" IS NULL)=((shape_mask & 131072)=0 OR (null_mask & 131072)<>0)),
    CHECK(("placement" IS NULL)=((shape_mask & 262144)=0 OR (null_mask & 262144)<>0)),
    CHECK(("canPlay" IS NULL)=((shape_mask & 524288)=0 OR (null_mask & 524288)<>0)),
    CHECK(("rankText" IS NULL)=((shape_mask & 1048576)=0 OR (null_mask & 1048576)<>0)),
    CHECK(("salesText" IS NULL)=((shape_mask & 2097152)=0 OR (null_mask & 2097152)<>0)),
    CHECK(("relationship" IS NULL)=((shape_mask & 4194304)=0 OR (null_mask & 4194304)<>0)),
    CHECK(("count" IS NULL)=((shape_mask & 8388608)=0 OR (null_mask & 8388608)<>0)),
    CHECK(("playCount" IS NULL)=((shape_mask & 16777216)=0 OR (null_mask & 16777216)<>0)),
    CHECK(("presentation" IS NULL)=((shape_mask & 33554432)=0 OR (null_mask & 33554432)<>0)),
    CHECK(("dayOfMonth" IS NULL)=((shape_mask & 67108864)=0 OR (null_mask & 67108864)<>0)),
    CHECK(("decoration" IS NULL)=((shape_mask & 134217728)=0 OR (null_mask & 134217728)<>0)),
    CHECK(("updateFrequency" IS NULL)=((shape_mask & 268435456)=0 OR (null_mask & 268435456)<>0)),
    FOREIGN KEY("songId") REFERENCES music_songs(song_id),
    PRIMARY KEY("scope_key","pages_key","sections_key","items_key"),
    FOREIGN KEY("scope_key","pages_key","sections_key") REFERENCES "windows_home_pages_sections"("scope_key","pages_key","sections_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_home_pages_sections_items_song" (
    "scope_key" TEXT NOT NULL,
    "pages_key" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "songId" TEXT,
    CHECK(("songId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    FOREIGN KEY("songId") REFERENCES music_songs(song_id),
    PRIMARY KEY("scope_key","pages_key","sections_key","items_key"),
    FOREIGN KEY("scope_key","pages_key","sections_key","items_key") REFERENCES "windows_home_pages_sections_items"("scope_key","pages_key","sections_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_home_pages_sections_items_action" (
    "scope_key" TEXT NOT NULL,
    "pages_key" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 131071),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "type" TEXT,
    "id_kind" TEXT CHECK("id_kind" IN ('text','number')),
    "id_value" TEXT,
    "branch" TEXT,
    "pageCode" TEXT,
    "provider" TEXT,
    "mode" TEXT,
    "submode" TEXT,
    "ruleId" TEXT,
    "tabTagId" TEXT,
    "sourceType" TEXT,
    "sourceName" TEXT,
    "entry" TEXT,
    "songId" TEXT,
    CHECK(("type" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("id_kind" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("id_kind" IS NULL)=("id_value" IS NULL)),
    CHECK(("branch" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("pageCode" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("provider" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("mode" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("submode" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("ruleId" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("tabTagId" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("sourceType" IS NULL)=((shape_mask & 512)=0 OR (null_mask & 512)<>0)),
    CHECK(("sourceName" IS NULL)=((shape_mask & 1024)=0 OR (null_mask & 1024)<>0)),
    CHECK(("entry" IS NULL)=((shape_mask & 2048)=0 OR (null_mask & 2048)<>0)),
    CHECK(("songId" IS NULL)=((shape_mask & 4096)=0 OR (null_mask & 4096)<>0)),
    PRIMARY KEY("scope_key","pages_key","sections_key","items_key"),
    FOREIGN KEY("scope_key","pages_key","sections_key","items_key") REFERENCES "windows_home_pages_sections_items"("scope_key","pages_key","sections_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_home_pages_sections_items_action_channel" (
    "scope_key" TEXT NOT NULL,
    "pages_key" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 31),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "id_kind" TEXT CHECK("id_kind" IN ('text','number')),
    "id_value" TEXT,
    "name" TEXT,
    "pageCode" TEXT,
    "channelKind" TEXT,
    "placement" TEXT,
    CHECK(("id_kind" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("id_kind" IS NULL)=("id_value" IS NULL)),
    CHECK(("name" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("pageCode" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("channelKind" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("placement" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    PRIMARY KEY("scope_key","pages_key","sections_key","items_key"),
    FOREIGN KEY("scope_key","pages_key","sections_key","items_key") REFERENCES "windows_home_pages_sections_items_action"("scope_key","pages_key","sections_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_home_pages_sections_items_action_sourceIds" (
    "scope_key" TEXT NOT NULL,
    "pages_key" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    "sourceIds_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","pages_key","sections_key","items_key","sourceIds_key"),
    FOREIGN KEY("scope_key","pages_key","sections_key","items_key") REFERENCES "windows_home_pages_sections_items_action"("scope_key","pages_key","sections_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_home_pages_sections_items_action_ids" (
    "scope_key" TEXT NOT NULL,
    "pages_key" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    "ids_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","pages_key","sections_key","items_key","ids_key"),
    FOREIGN KEY("scope_key","pages_key","sections_key","items_key") REFERENCES "windows_home_pages_sections_items_action"("scope_key","pages_key","sections_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_home_pages_sections_items_action_locator" (
    "scope_key" TEXT NOT NULL,
    "pages_key" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 511),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "kind" TEXT,
    "id_kind" TEXT CHECK("id_kind" IN ('text','number')),
    "id_value" TEXT,
    "playlistId_kind" TEXT CHECK("playlistId_kind" IN ('text','number')),
    "playlistId_value" TEXT,
    "dirId_kind" TEXT CHECK("dirId_kind" IN ('text','number')),
    "dirId_value" TEXT,
    "listId_kind" TEXT CHECK("listId_kind" IN ('text','number')),
    "listId_value" TEXT,
    "version_kind" TEXT CHECK("version_kind" IN ('text','number')),
    "version_value" TEXT,
    "totalVersion_kind" TEXT CHECK("totalVersion_kind" IN ('text','number')),
    "totalVersion_value" TEXT,
    "creatorId_kind" TEXT CHECK("creatorId_kind" IN ('text','number')),
    "creatorId_value" TEXT,
    "creatorListId_kind" TEXT CHECK("creatorListId_kind" IN ('text','number')),
    "creatorListId_value" TEXT,
    CHECK(("kind" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("id_kind" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("id_kind" IS NULL)=("id_value" IS NULL)),
    CHECK(("playlistId_kind" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("playlistId_kind" IS NULL)=("playlistId_value" IS NULL)),
    CHECK(("dirId_kind" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("dirId_kind" IS NULL)=("dirId_value" IS NULL)),
    CHECK(("listId_kind" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("listId_kind" IS NULL)=("listId_value" IS NULL)),
    CHECK(("version_kind" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("version_kind" IS NULL)=("version_value" IS NULL)),
    CHECK(("totalVersion_kind" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("totalVersion_kind" IS NULL)=("totalVersion_value" IS NULL)),
    CHECK(("creatorId_kind" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("creatorId_kind" IS NULL)=("creatorId_value" IS NULL)),
    CHECK(("creatorListId_kind" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("creatorListId_kind" IS NULL)=("creatorListId_value" IS NULL)),
    PRIMARY KEY("scope_key","pages_key","sections_key","items_key"),
    FOREIGN KEY("scope_key","pages_key","sections_key","items_key") REFERENCES "windows_home_pages_sections_items_action"("scope_key","pages_key","sections_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_home_pages_sections_items_playbackSource" (
    "scope_key" TEXT NOT NULL,
    "pages_key" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 131071),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "type" TEXT,
    "id_kind" TEXT CHECK("id_kind" IN ('text','number')),
    "id_value" TEXT,
    "branch" TEXT,
    "pageCode" TEXT,
    "provider" TEXT,
    "mode" TEXT,
    "submode" TEXT,
    "ruleId" TEXT,
    "tabTagId" TEXT,
    "sourceType" TEXT,
    "sourceName" TEXT,
    "entry" TEXT,
    "songId" TEXT,
    CHECK(("type" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("id_kind" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("id_kind" IS NULL)=("id_value" IS NULL)),
    CHECK(("branch" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("pageCode" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("provider" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("mode" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("submode" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("ruleId" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("tabTagId" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("sourceType" IS NULL)=((shape_mask & 512)=0 OR (null_mask & 512)<>0)),
    CHECK(("sourceName" IS NULL)=((shape_mask & 1024)=0 OR (null_mask & 1024)<>0)),
    CHECK(("entry" IS NULL)=((shape_mask & 2048)=0 OR (null_mask & 2048)<>0)),
    CHECK(("songId" IS NULL)=((shape_mask & 4096)=0 OR (null_mask & 4096)<>0)),
    PRIMARY KEY("scope_key","pages_key","sections_key","items_key"),
    FOREIGN KEY("scope_key","pages_key","sections_key","items_key") REFERENCES "windows_home_pages_sections_items"("scope_key","pages_key","sections_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_home_pages_sections_items_playbackSource_channel" (
    "scope_key" TEXT NOT NULL,
    "pages_key" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 31),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "id_kind" TEXT CHECK("id_kind" IN ('text','number')),
    "id_value" TEXT,
    "name" TEXT,
    "pageCode" TEXT,
    "channelKind" TEXT,
    "placement" TEXT,
    CHECK(("id_kind" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("id_kind" IS NULL)=("id_value" IS NULL)),
    CHECK(("name" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("pageCode" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("channelKind" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("placement" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    PRIMARY KEY("scope_key","pages_key","sections_key","items_key"),
    FOREIGN KEY("scope_key","pages_key","sections_key","items_key") REFERENCES "windows_home_pages_sections_items_playbackSource"("scope_key","pages_key","sections_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_home_pages_sections_items_playbackSource_sourceIds" (
    "scope_key" TEXT NOT NULL,
    "pages_key" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    "sourceIds_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","pages_key","sections_key","items_key","sourceIds_key"),
    FOREIGN KEY("scope_key","pages_key","sections_key","items_key") REFERENCES "windows_home_pages_sections_items_playbackSource"("scope_key","pages_key","sections_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_home_pages_sections_items_playbackSource_ids" (
    "scope_key" TEXT NOT NULL,
    "pages_key" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    "ids_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","pages_key","sections_key","items_key","ids_key"),
    FOREIGN KEY("scope_key","pages_key","sections_key","items_key") REFERENCES "windows_home_pages_sections_items_playbackSource"("scope_key","pages_key","sections_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_home_pages_sections_items_playbackSource_locator" (
    "scope_key" TEXT NOT NULL,
    "pages_key" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 511),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "kind" TEXT,
    "id_kind" TEXT CHECK("id_kind" IN ('text','number')),
    "id_value" TEXT,
    "playlistId_kind" TEXT CHECK("playlistId_kind" IN ('text','number')),
    "playlistId_value" TEXT,
    "dirId_kind" TEXT CHECK("dirId_kind" IN ('text','number')),
    "dirId_value" TEXT,
    "listId_kind" TEXT CHECK("listId_kind" IN ('text','number')),
    "listId_value" TEXT,
    "version_kind" TEXT CHECK("version_kind" IN ('text','number')),
    "version_value" TEXT,
    "totalVersion_kind" TEXT CHECK("totalVersion_kind" IN ('text','number')),
    "totalVersion_value" TEXT,
    "creatorId_kind" TEXT CHECK("creatorId_kind" IN ('text','number')),
    "creatorId_value" TEXT,
    "creatorListId_kind" TEXT CHECK("creatorListId_kind" IN ('text','number')),
    "creatorListId_value" TEXT,
    CHECK(("kind" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("id_kind" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("id_kind" IS NULL)=("id_value" IS NULL)),
    CHECK(("playlistId_kind" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("playlistId_kind" IS NULL)=("playlistId_value" IS NULL)),
    CHECK(("dirId_kind" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("dirId_kind" IS NULL)=("dirId_value" IS NULL)),
    CHECK(("listId_kind" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("listId_kind" IS NULL)=("listId_value" IS NULL)),
    CHECK(("version_kind" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("version_kind" IS NULL)=("version_value" IS NULL)),
    CHECK(("totalVersion_kind" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("totalVersion_kind" IS NULL)=("totalVersion_value" IS NULL)),
    CHECK(("creatorId_kind" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("creatorId_kind" IS NULL)=("creatorId_value" IS NULL)),
    CHECK(("creatorListId_kind" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("creatorListId_kind" IS NULL)=("creatorListId_value" IS NULL)),
    PRIMARY KEY("scope_key","pages_key","sections_key","items_key"),
    FOREIGN KEY("scope_key","pages_key","sections_key","items_key") REFERENCES "windows_home_pages_sections_items_playbackSource"("scope_key","pages_key","sections_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_home_pages_sections_items_locator" (
    "scope_key" TEXT NOT NULL,
    "pages_key" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 511),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "kind" TEXT,
    "id_kind" TEXT CHECK("id_kind" IN ('text','number')),
    "id_value" TEXT,
    "playlistId_kind" TEXT CHECK("playlistId_kind" IN ('text','number')),
    "playlistId_value" TEXT,
    "dirId_kind" TEXT CHECK("dirId_kind" IN ('text','number')),
    "dirId_value" TEXT,
    "listId_kind" TEXT CHECK("listId_kind" IN ('text','number')),
    "listId_value" TEXT,
    "version_kind" TEXT CHECK("version_kind" IN ('text','number')),
    "version_value" TEXT,
    "totalVersion_kind" TEXT CHECK("totalVersion_kind" IN ('text','number')),
    "totalVersion_value" TEXT,
    "creatorId_kind" TEXT CHECK("creatorId_kind" IN ('text','number')),
    "creatorId_value" TEXT,
    "creatorListId_kind" TEXT CHECK("creatorListId_kind" IN ('text','number')),
    "creatorListId_value" TEXT,
    CHECK(("kind" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("id_kind" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("id_kind" IS NULL)=("id_value" IS NULL)),
    CHECK(("playlistId_kind" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("playlistId_kind" IS NULL)=("playlistId_value" IS NULL)),
    CHECK(("dirId_kind" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("dirId_kind" IS NULL)=("dirId_value" IS NULL)),
    CHECK(("listId_kind" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("listId_kind" IS NULL)=("listId_value" IS NULL)),
    CHECK(("version_kind" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("version_kind" IS NULL)=("version_value" IS NULL)),
    CHECK(("totalVersion_kind" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("totalVersion_kind" IS NULL)=("totalVersion_value" IS NULL)),
    CHECK(("creatorId_kind" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("creatorId_kind" IS NULL)=("creatorId_value" IS NULL)),
    CHECK(("creatorListId_kind" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("creatorListId_kind" IS NULL)=("creatorListId_value" IS NULL)),
    PRIMARY KEY("scope_key","pages_key","sections_key","items_key"),
    FOREIGN KEY("scope_key","pages_key","sections_key","items_key") REFERENCES "windows_home_pages_sections_items"("scope_key","pages_key","sections_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_home_pages_sections_items_coverRef" (
    "scope_key" TEXT NOT NULL,
    "pages_key" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "kind" TEXT,
    "id_kind" TEXT CHECK("id_kind" IN ('text','number')),
    "id_value" TEXT,
    CHECK(("kind" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("id_kind" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("id_kind" IS NULL)=("id_value" IS NULL)),
    PRIMARY KEY("scope_key","pages_key","sections_key","items_key"),
    FOREIGN KEY("scope_key","pages_key","sections_key","items_key") REFERENCES "windows_home_pages_sections_items"("scope_key","pages_key","sections_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_home_pages_sections_items_preview" (
    "scope_key" TEXT NOT NULL,
    "pages_key" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    "preview_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 63),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "title" TEXT,
    "artist" TEXT,
    "cover" TEXT,
    "rank" INTEGER,
    "lastRank" INTEGER,
    CHECK(("title" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("artist" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("cover" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("rank" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("lastRank" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    PRIMARY KEY("scope_key","pages_key","sections_key","items_key","preview_key"),
    FOREIGN KEY("scope_key","pages_key","sections_key","items_key") REFERENCES "windows_home_pages_sections_items"("scope_key","pages_key","sections_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_home_pages_sections_items_preview_song" (
    "scope_key" TEXT NOT NULL,
    "pages_key" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    "preview_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "songId" TEXT,
    CHECK(("songId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    FOREIGN KEY("songId") REFERENCES music_songs(song_id),
    PRIMARY KEY("scope_key","pages_key","sections_key","items_key","preview_key"),
    FOREIGN KEY("scope_key","pages_key","sections_key","items_key","preview_key") REFERENCES "windows_home_pages_sections_items_preview"("scope_key","pages_key","sections_key","items_key","preview_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_home_pages_sections_tabs" (
    "scope_key" TEXT NOT NULL,
    "pages_key" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "tabs_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "id_kind" TEXT CHECK("id_kind" IN ('text','number')),
    "id_value" TEXT,
    "name" TEXT,
    CHECK(("id_kind" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("id_kind" IS NULL)=("id_value" IS NULL)),
    CHECK(("name" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    PRIMARY KEY("scope_key","pages_key","sections_key","tabs_key"),
    FOREIGN KEY("scope_key","pages_key","sections_key") REFERENCES "windows_home_pages_sections"("scope_key","pages_key","sections_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_home_pages_sections_tabs_items" (
    "scope_key" TEXT NOT NULL,
    "pages_key" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "tabs_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 34359738367),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "songId" TEXT,
    "id_kind" TEXT CHECK("id_kind" IN ('text','number')),
    "id_value" TEXT,
    "key_kind" TEXT CHECK("key_kind" IN ('text','number')),
    "key_value" TEXT,
    "name" TEXT,
    "title" TEXT,
    "label" TEXT,
    "subtitle" TEXT,
    "contextText" TEXT,
    "creator" TEXT,
    "cover" TEXT,
    "coverLabel" TEXT,
    "kind" TEXT,
    "pageCode" TEXT,
    "channelKind" TEXT,
    "available" INTEGER CHECK("available" IN (0,1)),
    "errorCode" TEXT,
    "requiresAccount" INTEGER CHECK("requiresAccount" IN (0,1)),
    "display" INTEGER CHECK("display" IN (0,1)),
    "placement" TEXT,
    "canPlay" INTEGER CHECK("canPlay" IN (0,1)),
    "rankText" TEXT,
    "salesText" TEXT,
    "relationship" TEXT,
    "count" INTEGER,
    "playCount" INTEGER,
    "presentation" TEXT,
    "dayOfMonth" INTEGER,
    "decoration" TEXT,
    "updateFrequency" TEXT,
    CHECK(("songId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("id_kind" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("id_kind" IS NULL)=("id_value" IS NULL)),
    CHECK(("key_kind" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("key_kind" IS NULL)=("key_value" IS NULL)),
    CHECK(("name" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("title" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("label" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("subtitle" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("contextText" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("creator" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("cover" IS NULL)=((shape_mask & 512)=0 OR (null_mask & 512)<>0)),
    CHECK(("coverLabel" IS NULL)=((shape_mask & 1024)=0 OR (null_mask & 1024)<>0)),
    CHECK(("kind" IS NULL)=((shape_mask & 2048)=0 OR (null_mask & 2048)<>0)),
    CHECK(("pageCode" IS NULL)=((shape_mask & 4096)=0 OR (null_mask & 4096)<>0)),
    CHECK(("channelKind" IS NULL)=((shape_mask & 8192)=0 OR (null_mask & 8192)<>0)),
    CHECK(("available" IS NULL)=((shape_mask & 16384)=0 OR (null_mask & 16384)<>0)),
    CHECK(("errorCode" IS NULL)=((shape_mask & 32768)=0 OR (null_mask & 32768)<>0)),
    CHECK(("requiresAccount" IS NULL)=((shape_mask & 65536)=0 OR (null_mask & 65536)<>0)),
    CHECK(("display" IS NULL)=((shape_mask & 131072)=0 OR (null_mask & 131072)<>0)),
    CHECK(("placement" IS NULL)=((shape_mask & 262144)=0 OR (null_mask & 262144)<>0)),
    CHECK(("canPlay" IS NULL)=((shape_mask & 524288)=0 OR (null_mask & 524288)<>0)),
    CHECK(("rankText" IS NULL)=((shape_mask & 1048576)=0 OR (null_mask & 1048576)<>0)),
    CHECK(("salesText" IS NULL)=((shape_mask & 2097152)=0 OR (null_mask & 2097152)<>0)),
    CHECK(("relationship" IS NULL)=((shape_mask & 4194304)=0 OR (null_mask & 4194304)<>0)),
    CHECK(("count" IS NULL)=((shape_mask & 8388608)=0 OR (null_mask & 8388608)<>0)),
    CHECK(("playCount" IS NULL)=((shape_mask & 16777216)=0 OR (null_mask & 16777216)<>0)),
    CHECK(("presentation" IS NULL)=((shape_mask & 33554432)=0 OR (null_mask & 33554432)<>0)),
    CHECK(("dayOfMonth" IS NULL)=((shape_mask & 67108864)=0 OR (null_mask & 67108864)<>0)),
    CHECK(("decoration" IS NULL)=((shape_mask & 134217728)=0 OR (null_mask & 134217728)<>0)),
    CHECK(("updateFrequency" IS NULL)=((shape_mask & 268435456)=0 OR (null_mask & 268435456)<>0)),
    FOREIGN KEY("songId") REFERENCES music_songs(song_id),
    PRIMARY KEY("scope_key","pages_key","sections_key","tabs_key","items_key"),
    FOREIGN KEY("scope_key","pages_key","sections_key","tabs_key") REFERENCES "windows_home_pages_sections_tabs"("scope_key","pages_key","sections_key","tabs_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_home_pages_sections_tabs_items_song" (
    "scope_key" TEXT NOT NULL,
    "pages_key" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "tabs_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "songId" TEXT,
    CHECK(("songId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    FOREIGN KEY("songId") REFERENCES music_songs(song_id),
    PRIMARY KEY("scope_key","pages_key","sections_key","tabs_key","items_key"),
    FOREIGN KEY("scope_key","pages_key","sections_key","tabs_key","items_key") REFERENCES "windows_home_pages_sections_tabs_items"("scope_key","pages_key","sections_key","tabs_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_home_pages_sections_tabs_items_action" (
    "scope_key" TEXT NOT NULL,
    "pages_key" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "tabs_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 131071),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "type" TEXT,
    "id_kind" TEXT CHECK("id_kind" IN ('text','number')),
    "id_value" TEXT,
    "branch" TEXT,
    "pageCode" TEXT,
    "provider" TEXT,
    "mode" TEXT,
    "submode" TEXT,
    "ruleId" TEXT,
    "tabTagId" TEXT,
    "sourceType" TEXT,
    "sourceName" TEXT,
    "entry" TEXT,
    "songId" TEXT,
    CHECK(("type" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("id_kind" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("id_kind" IS NULL)=("id_value" IS NULL)),
    CHECK(("branch" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("pageCode" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("provider" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("mode" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("submode" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("ruleId" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("tabTagId" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("sourceType" IS NULL)=((shape_mask & 512)=0 OR (null_mask & 512)<>0)),
    CHECK(("sourceName" IS NULL)=((shape_mask & 1024)=0 OR (null_mask & 1024)<>0)),
    CHECK(("entry" IS NULL)=((shape_mask & 2048)=0 OR (null_mask & 2048)<>0)),
    CHECK(("songId" IS NULL)=((shape_mask & 4096)=0 OR (null_mask & 4096)<>0)),
    PRIMARY KEY("scope_key","pages_key","sections_key","tabs_key","items_key"),
    FOREIGN KEY("scope_key","pages_key","sections_key","tabs_key","items_key") REFERENCES "windows_home_pages_sections_tabs_items"("scope_key","pages_key","sections_key","tabs_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_home_pages_sections_tabs_items_action_channel" (
    "scope_key" TEXT NOT NULL,
    "pages_key" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "tabs_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 31),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "id_kind" TEXT CHECK("id_kind" IN ('text','number')),
    "id_value" TEXT,
    "name" TEXT,
    "pageCode" TEXT,
    "channelKind" TEXT,
    "placement" TEXT,
    CHECK(("id_kind" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("id_kind" IS NULL)=("id_value" IS NULL)),
    CHECK(("name" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("pageCode" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("channelKind" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("placement" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    PRIMARY KEY("scope_key","pages_key","sections_key","tabs_key","items_key"),
    FOREIGN KEY("scope_key","pages_key","sections_key","tabs_key","items_key") REFERENCES "windows_home_pages_sections_tabs_items_action"("scope_key","pages_key","sections_key","tabs_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_home_pages_sections_tabs_items_action_sourceIds" (
    "scope_key" TEXT NOT NULL,
    "pages_key" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "tabs_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    "sourceIds_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","pages_key","sections_key","tabs_key","items_key","sourceIds_key"),
    FOREIGN KEY("scope_key","pages_key","sections_key","tabs_key","items_key") REFERENCES "windows_home_pages_sections_tabs_items_action"("scope_key","pages_key","sections_key","tabs_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_home_pages_sections_tabs_items_action_ids" (
    "scope_key" TEXT NOT NULL,
    "pages_key" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "tabs_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    "ids_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","pages_key","sections_key","tabs_key","items_key","ids_key"),
    FOREIGN KEY("scope_key","pages_key","sections_key","tabs_key","items_key") REFERENCES "windows_home_pages_sections_tabs_items_action"("scope_key","pages_key","sections_key","tabs_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_home_pages_sections_tabs_items_action_locator" (
    "scope_key" TEXT NOT NULL,
    "pages_key" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "tabs_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 511),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "kind" TEXT,
    "id_kind" TEXT CHECK("id_kind" IN ('text','number')),
    "id_value" TEXT,
    "playlistId_kind" TEXT CHECK("playlistId_kind" IN ('text','number')),
    "playlistId_value" TEXT,
    "dirId_kind" TEXT CHECK("dirId_kind" IN ('text','number')),
    "dirId_value" TEXT,
    "listId_kind" TEXT CHECK("listId_kind" IN ('text','number')),
    "listId_value" TEXT,
    "version_kind" TEXT CHECK("version_kind" IN ('text','number')),
    "version_value" TEXT,
    "totalVersion_kind" TEXT CHECK("totalVersion_kind" IN ('text','number')),
    "totalVersion_value" TEXT,
    "creatorId_kind" TEXT CHECK("creatorId_kind" IN ('text','number')),
    "creatorId_value" TEXT,
    "creatorListId_kind" TEXT CHECK("creatorListId_kind" IN ('text','number')),
    "creatorListId_value" TEXT,
    CHECK(("kind" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("id_kind" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("id_kind" IS NULL)=("id_value" IS NULL)),
    CHECK(("playlistId_kind" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("playlistId_kind" IS NULL)=("playlistId_value" IS NULL)),
    CHECK(("dirId_kind" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("dirId_kind" IS NULL)=("dirId_value" IS NULL)),
    CHECK(("listId_kind" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("listId_kind" IS NULL)=("listId_value" IS NULL)),
    CHECK(("version_kind" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("version_kind" IS NULL)=("version_value" IS NULL)),
    CHECK(("totalVersion_kind" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("totalVersion_kind" IS NULL)=("totalVersion_value" IS NULL)),
    CHECK(("creatorId_kind" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("creatorId_kind" IS NULL)=("creatorId_value" IS NULL)),
    CHECK(("creatorListId_kind" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("creatorListId_kind" IS NULL)=("creatorListId_value" IS NULL)),
    PRIMARY KEY("scope_key","pages_key","sections_key","tabs_key","items_key"),
    FOREIGN KEY("scope_key","pages_key","sections_key","tabs_key","items_key") REFERENCES "windows_home_pages_sections_tabs_items_action"("scope_key","pages_key","sections_key","tabs_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_home_pages_sections_tabs_items_playbackSource" (
    "scope_key" TEXT NOT NULL,
    "pages_key" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "tabs_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 131071),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "type" TEXT,
    "id_kind" TEXT CHECK("id_kind" IN ('text','number')),
    "id_value" TEXT,
    "branch" TEXT,
    "pageCode" TEXT,
    "provider" TEXT,
    "mode" TEXT,
    "submode" TEXT,
    "ruleId" TEXT,
    "tabTagId" TEXT,
    "sourceType" TEXT,
    "sourceName" TEXT,
    "entry" TEXT,
    "songId" TEXT,
    CHECK(("type" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("id_kind" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("id_kind" IS NULL)=("id_value" IS NULL)),
    CHECK(("branch" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("pageCode" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("provider" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("mode" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("submode" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("ruleId" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("tabTagId" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("sourceType" IS NULL)=((shape_mask & 512)=0 OR (null_mask & 512)<>0)),
    CHECK(("sourceName" IS NULL)=((shape_mask & 1024)=0 OR (null_mask & 1024)<>0)),
    CHECK(("entry" IS NULL)=((shape_mask & 2048)=0 OR (null_mask & 2048)<>0)),
    CHECK(("songId" IS NULL)=((shape_mask & 4096)=0 OR (null_mask & 4096)<>0)),
    PRIMARY KEY("scope_key","pages_key","sections_key","tabs_key","items_key"),
    FOREIGN KEY("scope_key","pages_key","sections_key","tabs_key","items_key") REFERENCES "windows_home_pages_sections_tabs_items"("scope_key","pages_key","sections_key","tabs_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_home_pages_sections_tabs_items_playbackSource_channel" (
    "scope_key" TEXT NOT NULL,
    "pages_key" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "tabs_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 31),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "id_kind" TEXT CHECK("id_kind" IN ('text','number')),
    "id_value" TEXT,
    "name" TEXT,
    "pageCode" TEXT,
    "channelKind" TEXT,
    "placement" TEXT,
    CHECK(("id_kind" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("id_kind" IS NULL)=("id_value" IS NULL)),
    CHECK(("name" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("pageCode" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("channelKind" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("placement" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    PRIMARY KEY("scope_key","pages_key","sections_key","tabs_key","items_key"),
    FOREIGN KEY("scope_key","pages_key","sections_key","tabs_key","items_key") REFERENCES "windows_home_pages_sections_tabs_items_playbackSource"("scope_key","pages_key","sections_key","tabs_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_home_pages_sections_tabs_items_playbackSource_sourceIds" (
    "scope_key" TEXT NOT NULL,
    "pages_key" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "tabs_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    "sourceIds_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","pages_key","sections_key","tabs_key","items_key","sourceIds_key"),
    FOREIGN KEY("scope_key","pages_key","sections_key","tabs_key","items_key") REFERENCES "windows_home_pages_sections_tabs_items_playbackSource"("scope_key","pages_key","sections_key","tabs_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_home_pages_sections_tabs_items_playbackSource_ids" (
    "scope_key" TEXT NOT NULL,
    "pages_key" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "tabs_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    "ids_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","pages_key","sections_key","tabs_key","items_key","ids_key"),
    FOREIGN KEY("scope_key","pages_key","sections_key","tabs_key","items_key") REFERENCES "windows_home_pages_sections_tabs_items_playbackSource"("scope_key","pages_key","sections_key","tabs_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_home_pages_sections_tabs_items_playbackSource_locator" (
    "scope_key" TEXT NOT NULL,
    "pages_key" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "tabs_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 511),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "kind" TEXT,
    "id_kind" TEXT CHECK("id_kind" IN ('text','number')),
    "id_value" TEXT,
    "playlistId_kind" TEXT CHECK("playlistId_kind" IN ('text','number')),
    "playlistId_value" TEXT,
    "dirId_kind" TEXT CHECK("dirId_kind" IN ('text','number')),
    "dirId_value" TEXT,
    "listId_kind" TEXT CHECK("listId_kind" IN ('text','number')),
    "listId_value" TEXT,
    "version_kind" TEXT CHECK("version_kind" IN ('text','number')),
    "version_value" TEXT,
    "totalVersion_kind" TEXT CHECK("totalVersion_kind" IN ('text','number')),
    "totalVersion_value" TEXT,
    "creatorId_kind" TEXT CHECK("creatorId_kind" IN ('text','number')),
    "creatorId_value" TEXT,
    "creatorListId_kind" TEXT CHECK("creatorListId_kind" IN ('text','number')),
    "creatorListId_value" TEXT,
    CHECK(("kind" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("id_kind" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("id_kind" IS NULL)=("id_value" IS NULL)),
    CHECK(("playlistId_kind" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("playlistId_kind" IS NULL)=("playlistId_value" IS NULL)),
    CHECK(("dirId_kind" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("dirId_kind" IS NULL)=("dirId_value" IS NULL)),
    CHECK(("listId_kind" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("listId_kind" IS NULL)=("listId_value" IS NULL)),
    CHECK(("version_kind" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("version_kind" IS NULL)=("version_value" IS NULL)),
    CHECK(("totalVersion_kind" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("totalVersion_kind" IS NULL)=("totalVersion_value" IS NULL)),
    CHECK(("creatorId_kind" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("creatorId_kind" IS NULL)=("creatorId_value" IS NULL)),
    CHECK(("creatorListId_kind" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("creatorListId_kind" IS NULL)=("creatorListId_value" IS NULL)),
    PRIMARY KEY("scope_key","pages_key","sections_key","tabs_key","items_key"),
    FOREIGN KEY("scope_key","pages_key","sections_key","tabs_key","items_key") REFERENCES "windows_home_pages_sections_tabs_items_playbackSource"("scope_key","pages_key","sections_key","tabs_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_home_pages_sections_tabs_items_locator" (
    "scope_key" TEXT NOT NULL,
    "pages_key" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "tabs_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 511),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "kind" TEXT,
    "id_kind" TEXT CHECK("id_kind" IN ('text','number')),
    "id_value" TEXT,
    "playlistId_kind" TEXT CHECK("playlistId_kind" IN ('text','number')),
    "playlistId_value" TEXT,
    "dirId_kind" TEXT CHECK("dirId_kind" IN ('text','number')),
    "dirId_value" TEXT,
    "listId_kind" TEXT CHECK("listId_kind" IN ('text','number')),
    "listId_value" TEXT,
    "version_kind" TEXT CHECK("version_kind" IN ('text','number')),
    "version_value" TEXT,
    "totalVersion_kind" TEXT CHECK("totalVersion_kind" IN ('text','number')),
    "totalVersion_value" TEXT,
    "creatorId_kind" TEXT CHECK("creatorId_kind" IN ('text','number')),
    "creatorId_value" TEXT,
    "creatorListId_kind" TEXT CHECK("creatorListId_kind" IN ('text','number')),
    "creatorListId_value" TEXT,
    CHECK(("kind" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("id_kind" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("id_kind" IS NULL)=("id_value" IS NULL)),
    CHECK(("playlistId_kind" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("playlistId_kind" IS NULL)=("playlistId_value" IS NULL)),
    CHECK(("dirId_kind" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("dirId_kind" IS NULL)=("dirId_value" IS NULL)),
    CHECK(("listId_kind" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("listId_kind" IS NULL)=("listId_value" IS NULL)),
    CHECK(("version_kind" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("version_kind" IS NULL)=("version_value" IS NULL)),
    CHECK(("totalVersion_kind" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("totalVersion_kind" IS NULL)=("totalVersion_value" IS NULL)),
    CHECK(("creatorId_kind" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("creatorId_kind" IS NULL)=("creatorId_value" IS NULL)),
    CHECK(("creatorListId_kind" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("creatorListId_kind" IS NULL)=("creatorListId_value" IS NULL)),
    PRIMARY KEY("scope_key","pages_key","sections_key","tabs_key","items_key"),
    FOREIGN KEY("scope_key","pages_key","sections_key","tabs_key","items_key") REFERENCES "windows_home_pages_sections_tabs_items"("scope_key","pages_key","sections_key","tabs_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_home_pages_sections_tabs_items_coverRef" (
    "scope_key" TEXT NOT NULL,
    "pages_key" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "tabs_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "kind" TEXT,
    "id_kind" TEXT CHECK("id_kind" IN ('text','number')),
    "id_value" TEXT,
    CHECK(("kind" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("id_kind" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("id_kind" IS NULL)=("id_value" IS NULL)),
    PRIMARY KEY("scope_key","pages_key","sections_key","tabs_key","items_key"),
    FOREIGN KEY("scope_key","pages_key","sections_key","tabs_key","items_key") REFERENCES "windows_home_pages_sections_tabs_items"("scope_key","pages_key","sections_key","tabs_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_home_pages_sections_tabs_items_preview" (
    "scope_key" TEXT NOT NULL,
    "pages_key" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "tabs_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    "preview_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 63),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "title" TEXT,
    "artist" TEXT,
    "cover" TEXT,
    "rank" INTEGER,
    "lastRank" INTEGER,
    CHECK(("title" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("artist" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("cover" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("rank" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("lastRank" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    PRIMARY KEY("scope_key","pages_key","sections_key","tabs_key","items_key","preview_key"),
    FOREIGN KEY("scope_key","pages_key","sections_key","tabs_key","items_key") REFERENCES "windows_home_pages_sections_tabs_items"("scope_key","pages_key","sections_key","tabs_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_home_pages_sections_tabs_items_preview_song" (
    "scope_key" TEXT NOT NULL,
    "pages_key" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "tabs_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    "preview_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "songId" TEXT,
    CHECK(("songId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    FOREIGN KEY("songId") REFERENCES music_songs(song_id),
    PRIMARY KEY("scope_key","pages_key","sections_key","tabs_key","items_key","preview_key"),
    FOREIGN KEY("scope_key","pages_key","sections_key","tabs_key","items_key","preview_key") REFERENCES "windows_home_pages_sections_tabs_items_preview"("scope_key","pages_key","sections_key","tabs_key","items_key","preview_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_home_pages_sections_tags" (
    "scope_key" TEXT NOT NULL,
    "pages_key" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "tags_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 15),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "id_kind" TEXT CHECK("id_kind" IN ('text','number')),
    "id_value" TEXT,
    "name" TEXT,
    "group" TEXT,
    "selected" INTEGER CHECK("selected" IN (0,1)),
    CHECK(("id_kind" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("id_kind" IS NULL)=("id_value" IS NULL)),
    CHECK(("name" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("group" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("selected" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    PRIMARY KEY("scope_key","pages_key","sections_key","tags_key"),
    FOREIGN KEY("scope_key","pages_key","sections_key") REFERENCES "windows_home_pages_sections"("scope_key","pages_key","sections_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_audio_index" (
    "resource_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 127),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "file" TEXT,
    "size" INTEGER,
    "sha256" TEXT,
    "signature" TEXT,
    "confirmedAt" INTEGER,
    "duration" REAL,
    CHECK(("file" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("size" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("sha256" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("signature" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("confirmedAt" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("duration" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    PRIMARY KEY("resource_key")
) STRICT;

CREATE TABLE "windows_audio_index_info" (
    "resource_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 33554431),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "kind" TEXT,
    "platform" TEXT,
    "songId" TEXT,
    "sourceKey" TEXT,
    "scope" TEXT,
    "generation" INTEGER,
    "installationId" TEXT,
    "providerName" TEXT,
    "quality" TEXT,
    "requestedQuality" TEXT,
    "actualQuality" TEXT,
    "qualityVerified" INTEGER CHECK("qualityVerified" IN (0,1)),
    "playbackVerified" INTEGER CHECK("playbackVerified" IN (0,1)),
    "qualityPolicySignature" TEXT,
    "qualityCacheSignature" TEXT,
    "duration" REAL,
    "size" INTEGER,
    "full" INTEGER CHECK("full" IN (0,1)),
    "delivery" TEXT,
    "qualityEvidence" TEXT,
    "codec" TEXT,
    "sampleRate" INTEGER,
    "bitDepth" INTEGER,
    "channels" INTEGER,
    "bitrate" INTEGER,
    CHECK(("kind" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("platform" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("songId" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("sourceKey" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("scope" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("generation" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("installationId" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("providerName" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("quality" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("requestedQuality" IS NULL)=((shape_mask & 512)=0 OR (null_mask & 512)<>0)),
    CHECK(("actualQuality" IS NULL)=((shape_mask & 1024)=0 OR (null_mask & 1024)<>0)),
    CHECK(("qualityVerified" IS NULL)=((shape_mask & 2048)=0 OR (null_mask & 2048)<>0)),
    CHECK(("playbackVerified" IS NULL)=((shape_mask & 4096)=0 OR (null_mask & 4096)<>0)),
    CHECK(("qualityPolicySignature" IS NULL)=((shape_mask & 8192)=0 OR (null_mask & 8192)<>0)),
    CHECK(("qualityCacheSignature" IS NULL)=((shape_mask & 16384)=0 OR (null_mask & 16384)<>0)),
    CHECK(("duration" IS NULL)=((shape_mask & 32768)=0 OR (null_mask & 32768)<>0)),
    CHECK(("size" IS NULL)=((shape_mask & 65536)=0 OR (null_mask & 65536)<>0)),
    CHECK(("full" IS NULL)=((shape_mask & 131072)=0 OR (null_mask & 131072)<>0)),
    CHECK(("delivery" IS NULL)=((shape_mask & 262144)=0 OR (null_mask & 262144)<>0)),
    CHECK(("qualityEvidence" IS NULL)=((shape_mask & 524288)=0 OR (null_mask & 524288)<>0)),
    CHECK(("codec" IS NULL)=((shape_mask & 1048576)=0 OR (null_mask & 1048576)<>0)),
    CHECK(("sampleRate" IS NULL)=((shape_mask & 2097152)=0 OR (null_mask & 2097152)<>0)),
    CHECK(("bitDepth" IS NULL)=((shape_mask & 4194304)=0 OR (null_mask & 4194304)<>0)),
    CHECK(("channels" IS NULL)=((shape_mask & 8388608)=0 OR (null_mask & 8388608)<>0)),
    CHECK(("bitrate" IS NULL)=((shape_mask & 16777216)=0 OR (null_mask & 16777216)<>0)),
    FOREIGN KEY("songId") REFERENCES music_songs(song_id),
    PRIMARY KEY("resource_key"),
    FOREIGN KEY("resource_key") REFERENCES "windows_audio_index"("resource_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "windows_source_match" (
    "song_id" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "storedBytes" INTEGER,
    CHECK(("storedBytes" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("song_id")
) STRICT;

CREATE TABLE "windows_source_match_platforms" (
    "song_id" TEXT NOT NULL,
    "platforms_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 63),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "state" TEXT,
    "checkedAt" INTEGER,
    "context" TEXT,
    "query" TEXT,
    "code" TEXT,
    "matchedAt" INTEGER,
    CHECK(("state" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("checkedAt" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("context" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("query" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("code" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("matchedAt" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    PRIMARY KEY("song_id","platforms_key"),
    FOREIGN KEY("song_id") REFERENCES "windows_source_match"("song_id") ON DELETE CASCADE
) STRICT;

-- module: 44_windows_pan.sql
CREATE TABLE windows_pan_state (
    singleton INTEGER PRIMARY KEY CHECK(singleton=1),
    revision INTEGER NOT NULL CHECK(revision BETWEEN 0 AND 9007199254740991)
) STRICT;
INSERT INTO windows_pan_state VALUES(1,0);
CREATE TABLE windows_pan_accounts (
    provider TEXT PRIMARY KEY NOT NULL CHECK(provider IN ('baidu','quark','uc','tianyi','yidong')),
    account_revision INTEGER NOT NULL CHECK(account_revision BETWEEN 0 AND 9007199254740991),
    row_revision INTEGER NOT NULL CHECK(row_revision BETWEEN 0 AND 9007199254740991),
    cookie TEXT, username TEXT, password TEXT, authorization TEXT,
    refreshedAt INTEGER CHECK(refreshedAt BETWEEN 0 AND 9007199254740991),
    CHECK(provider='tianyi' OR (username IS NULL AND password IS NULL)),
    CHECK(provider='yidong' OR (authorization IS NULL AND refreshedAt IS NULL)),
    CHECK(provider<>'yidong' OR cookie IS NULL)
) STRICT;
INSERT INTO windows_pan_accounts(provider,account_revision,row_revision) VALUES
    ('baidu',0,0),('quark',0,0),('uc',0,0),('tianyi',0,0),('yidong',0,0);

-- module: 45_windows_resources.sql
CREATE TABLE windows_images (
    address TEXT PRIMARY KEY NOT NULL,
    file_id TEXT NOT NULL CHECK(length(file_id)=64),
    bytes INTEGER NOT NULL CHECK(bytes BETWEEN 1 AND 8388608),
    updated_at INTEGER NOT NULL CHECK(updated_at>0)
) STRICT;
CREATE INDEX windows_images_updated ON windows_images(updated_at);
CREATE INDEX windows_images_file ON windows_images(file_id);
CREATE INDEX windows_lyrics_updated ON windows_lyrics(updatedAt);
CREATE INDEX windows_playback_source_capacity ON windows_playback_source(account_scope,entry_kind,at);
CREATE INDEX windows_chorus_updated ON windows_chorus(at);
CREATE INDEX windows_audio_confirmed ON windows_audio_index(confirmedAt);
CREATE UNIQUE INDEX windows_audio_file_identity ON windows_audio_index(file);
CREATE INDEX windows_audio_song ON windows_audio_index_info(songId,kind);
