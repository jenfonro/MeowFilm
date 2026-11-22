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
    book_source_name TEXT,
    book_source_url TEXT,
    book_source_group TEXT,
    book_source_comment TEXT,
    book_url_pattern TEXT,
    search_url TEXT,
    explore_url TEXT,
    login_url TEXT,
    login_check_js TEXT,
    js_lib TEXT,
    cover_decode_js TEXT,
    concurrent_rate TEXT,
    header TEXT,
    html TEXT,
    book_source_type INTEGER,
    weight REAL,
    respond_time REAL,
    last_update_time REAL,
    enabled_cookie_jar INTEGER CHECK (enabled_cookie_jar IN (0, 1))
) STRICT;

CREATE TABLE source_definition_rules (
    installation_id TEXT NOT NULL REFERENCES source_installations(installation_id) ON DELETE CASCADE,
    rule_group TEXT NOT NULL CHECK (rule_group IN ('ruleSearch', 'ruleExplore', 'ruleBookInfo', 'ruleToc', 'ruleContent')),
    encoded INTEGER NOT NULL CHECK (encoded IN (0, 1)),
    book_list TEXT,
    name TEXT,
    author TEXT,
    kind TEXT,
    word_count TEXT,
    last_chapter TEXT,
    intro TEXT,
    cover_url TEXT,
    book_url TEXT,
    toc_url TEXT,
    init TEXT,
    chapter_list TEXT,
    chapter_name TEXT,
    chapter_url TEXT,
    next_toc_url TEXT,
    is_reverse TEXT,
    reverse_toc TEXT,
    update_time TEXT,
    pre_update_js TEXT,
    content TEXT,
    next_content_url TEXT,
    title TEXT,
    replace_regex TEXT,
    source_regex TEXT,
    web_js TEXT,
    pay_action TEXT,
    image_style TEXT,
    image_decode TEXT,
    image_decrypt TEXT,
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

-- Reader synchronous source.get/put scope (UTF-16LE BLOBs preserve unpaired JS surrogates).
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
    kind TEXT NOT NULL CHECK (kind IN ('null', 'boolean', 'integer', 'real', 'number_text', 'text', 'array', 'object')),
    bool_value INTEGER CHECK (bool_value IN (0, 1)),
    int_value INTEGER,
    real_value REAL,
    text_value ANY,
    CHECK ((kind = 'boolean') = (bool_value IS NOT NULL)
        AND (kind = 'integer') = (int_value IS NOT NULL)
        AND (kind = 'real') = (real_value IS NOT NULL)),
    CHECK (CASE kind
        WHEN 'text' THEN typeof(text_value) = 'blob' AND length(text_value) % 2 = 0
        WHEN 'number_text' THEN typeof(text_value) = 'text'
        ELSE text_value IS NULL END),
    CHECK (CASE WHEN node_id = 0 THEN parent_id IS NULL AND prop_key IS NULL AND item_index IS NULL
        ELSE parent_id IS NOT NULL AND parent_id >= 0 AND parent_id < node_id AND (prop_key IS NULL) <> (item_index IS NULL) END),
    PRIMARY KEY (installation_id, entry_key, node_id),
    FOREIGN KEY (installation_id, entry_key, parent_id)
        REFERENCES source_reader_value_nodes(installation_id, entry_key, node_id)
) STRICT;

CREATE INDEX idx_source_reader_value_nodes_parent
    ON source_reader_value_nodes(installation_id, entry_key, parent_id);

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
-- Shared Cat gateway configuration (used by standalone runtime or native App owner) and CatNode script state.
CREATE TABLE cat_settings (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    port INTEGER NOT NULL CHECK (port BETWEEN 0 AND 65535)
) STRICT;

CREATE TABLE cat_providers (
    provider TEXT PRIMARY KEY NOT NULL,
    position INTEGER NOT NULL DEFAULT 0 CHECK (position >= 0),
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1))
) STRICT;

CREATE TABLE cat_mappings (
    position INTEGER PRIMARY KEY CHECK (position >= 0),
    provider TEXT NOT NULL
) STRICT;

CREATE TABLE cat_mapping_aliases (
    mapping_position INTEGER NOT NULL REFERENCES cat_mappings(position) ON DELETE CASCADE,
    position INTEGER NOT NULL CHECK (position >= 0),
    alias TEXT NOT NULL,
    PRIMARY KEY (mapping_position, position)
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

-- module: runtime/13_pan.sql
-- Unified Pan accounts (owned by standalone runtime or native App; owner-level CAS uses owner_revisions).
CREATE TABLE pan_accounts (
    provider TEXT PRIMARY KEY NOT NULL CHECK (provider IN ('baidu', 'quark', 'uc', 'tianyi', 'yidong')),
    account_revision INTEGER NOT NULL CHECK (account_revision BETWEEN 0 AND 9007199254740991),
    row_revision INTEGER NOT NULL CHECK (row_revision BETWEEN 0 AND 9007199254740991),
    cookie TEXT,
    username TEXT,
    password TEXT,
    authorization TEXT,
    refreshed_at_ms INTEGER CHECK (refreshed_at_ms IS NULL OR refreshed_at_ms BETWEEN 0 AND 9007199254740991),
    CHECK (provider IN ('baidu', 'quark', 'uc', 'tianyi') OR cookie IS NULL),
    CHECK (provider = 'tianyi' OR (username IS NULL AND password IS NULL)),
    CHECK (provider = 'yidong' OR (authorization IS NULL AND refreshed_at_ms IS NULL))
) STRICT;

INSERT INTO pan_accounts(provider, account_revision, row_revision) VALUES
    ('baidu', 0, 0), ('quark', 0, 0), ('uc', 0, 0), ('tianyi', 0, 0), ('yidong', 0, 0);

-- module: runtime/14_metadata_settings.sql
-- Unified TMDB and Douban settings (owned by standalone runtime or native App; owner-level CAS uses owner_revisions).
CREATE TABLE tmdb_settings (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    api_base TEXT NOT NULL,
    image_base TEXT NOT NULL,
    api_key TEXT NOT NULL,
    access_token TEXT NOT NULL,
    language TEXT NOT NULL,
    region TEXT NOT NULL,
    include_adult INTEGER NOT NULL CHECK (include_adult IN (0, 1)),
    row_revision INTEGER NOT NULL DEFAULT 1 CHECK (row_revision BETWEEN 1 AND 9007199254740991)
) STRICT;

CREATE TABLE douban_settings (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    api_base TEXT NOT NULL,
    search_base TEXT NOT NULL,
    image_base TEXT NOT NULL,
    api_proxy TEXT NOT NULL,
    image_proxy TEXT NOT NULL,
    cookie TEXT NOT NULL,
    row_revision INTEGER NOT NULL DEFAULT 1 CHECK (row_revision >= 1)
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

CREATE INDEX idx_music_anonymous_wy_cookies_singleton ON music_anonymous_wy_cookies(singleton);

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
-- Unified film playback preferences, rules, history, manual matches, blocked sources, preview targets, and groups.
CREATE TABLE film_preferences (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    ios_auto_prepare INTEGER NOT NULL CHECK (ios_auto_prepare IN (0, 1)),
    metadata_view_provider TEXT NOT NULL CHECK (metadata_view_provider IN ('tmdb', 'douban')),
    row_revision INTEGER NOT NULL DEFAULT 1 CHECK (row_revision >= 1)
) STRICT;

CREATE TABLE film_preference_items (
    list_kind TEXT NOT NULL CHECK (list_kind IN ('ruleOrder', 'panOrder', 'keywords', 'siteCleanKeywords')),
    position INTEGER NOT NULL CHECK (position >= 0),
    item_value TEXT NOT NULL,
    PRIMARY KEY (list_kind, position)
) STRICT;

CREATE TABLE film_rule_groups (
    group_name TEXT PRIMARY KEY NOT NULL CHECK (group_name IN ('episodeCleanRegexRules', 'episodeRules', 'movieRules', 'aggregateRegexRules')),
    row_revision INTEGER NOT NULL DEFAULT 0 CHECK (row_revision BETWEEN 0 AND 9007199254740991),
    overridden INTEGER NOT NULL DEFAULT 0 CHECK (overridden IN (0, 1))
) STRICT;

INSERT INTO film_rule_groups(group_name) VALUES
    ('episodeCleanRegexRules'), ('episodeRules'), ('movieRules'), ('aggregateRegexRules');

CREATE TABLE film_rules (
    group_name TEXT NOT NULL REFERENCES film_rule_groups(group_name) ON DELETE CASCADE,
    position INTEGER NOT NULL CHECK (position >= 0),
    pattern TEXT NOT NULL,
    replacement TEXT,
    flags TEXT,
    PRIMARY KEY (group_name, position),
    CHECK (group_name IN ('episodeRules', 'movieRules') OR (replacement IS NULL AND flags IS NULL))
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

-- module: runtime/21_metadata_cache.sql
-- TMDB and Douban component-level metadata cache (dynamic external upstream responses stored as bounded typed value trees).
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

-- module: shared/25_shared_film.sql
-- Shared native App film configuration inputs, favorites, and deleted source tombstones (Windows & iOS).
CREATE TABLE cat_scripts (
    installation_id TEXT PRIMARY KEY NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    name TEXT NOT NULL,
    source_type TEXT NOT NULL CHECK (source_type IN ('local', 'network')),
    source_url TEXT,
    local_path TEXT,
    local_hash TEXT,
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
    input_revision INTEGER NOT NULL CHECK (input_revision BETWEEN 1 AND 9007199254740991),
    row_revision INTEGER NOT NULL DEFAULT 1 CHECK (row_revision BETWEEN 1 AND 9007199254740991)
) STRICT;

CREATE INDEX idx_cat_scripts_order ON cat_scripts(position);

CREATE TABLE film_source_deletions (
    installation_id TEXT NOT NULL,
    site_key TEXT NOT NULL,
    position INTEGER NOT NULL DEFAULT 0 CHECK (position >= 0),
    PRIMARY KEY (installation_id, site_key)
) STRICT;

CREATE TABLE film_favorites (
    favorite_id TEXT PRIMARY KEY NOT NULL,
    position INTEGER NOT NULL DEFAULT 0 CHECK (position >= 0),
    source_id TEXT NOT NULL DEFAULT '',
    item_id TEXT NOT NULL,
    title TEXT NOT NULL DEFAULT '',
    kind TEXT,
    poster TEXT NOT NULL DEFAULT '',
    score TEXT NOT NULL DEFAULT '',
    text_badge TEXT NOT NULL DEFAULT '',
    subtitle TEXT NOT NULL DEFAULT '',
    target_id TEXT,
    target_kind TEXT CHECK (target_kind IS NULL OR target_kind IN ('movie', 'tv', 'source', 'group')),
    target_title TEXT,
    target_year INTEGER,
    metadata_provider TEXT,
    metadata_id_kind TEXT CHECK (metadata_id_kind IS NULL OR metadata_id_kind IN ('text', 'integer')),
    metadata_id_text TEXT,
    season INTEGER,
    episode INTEGER,
    global_episode INTEGER,
    source_key TEXT,
    source_engine TEXT,
    source_installation_id TEXT,
    source_site_kind TEXT CHECK (source_site_kind IS NULL OR source_site_kind IN ('text', 'integer')),
    source_site_text TEXT,
    source_detail_kind TEXT CHECK (source_detail_kind IS NULL OR source_detail_kind IN ('text', 'integer')),
    source_detail_text TEXT,
    source_line_kind TEXT CHECK (source_line_kind IS NULL OR source_line_kind IN ('text', 'integer')),
    source_line_text TEXT,
    added_at REAL NOT NULL DEFAULT 0,
    row_revision INTEGER NOT NULL DEFAULT 1 CHECK (row_revision BETWEEN 1 AND 9007199254740991),
    CHECK ((metadata_id_kind IS NULL) = (metadata_id_text IS NULL)),
    CHECK ((source_site_kind IS NULL) = (source_site_text IS NULL)),
    CHECK ((source_detail_kind IS NULL) = (source_detail_text IS NULL)),
    CHECK ((source_line_kind IS NULL) = (source_line_text IS NULL))
) STRICT;

CREATE INDEX idx_film_favorites_order ON film_favorites(position);
CREATE INDEX idx_film_favorites_recent ON film_favorites(added_at DESC, favorite_id);

CREATE TABLE film_favorite_members (
    favorite_id TEXT NOT NULL REFERENCES film_favorites(favorite_id) ON DELETE CASCADE,
    position INTEGER NOT NULL CHECK (position >= 0),
    source_key TEXT NOT NULL,
    detail_kind TEXT NOT NULL CHECK (detail_kind IN ('text', 'integer')),
    detail_text TEXT NOT NULL,
    PRIMARY KEY (favorite_id, position)
) STRICT;

-- module: shared/26_shared_novel.sql
-- Shared native App novel replacement rules (Windows & iOS).
CREATE TABLE novel_rules (
    rule_id TEXT PRIMARY KEY NOT NULL,
    position INTEGER NOT NULL DEFAULT 0 CHECK (position >= 0),
    name TEXT NOT NULL,
    rule_group TEXT NOT NULL DEFAULT '',
    pattern TEXT NOT NULL,
    replacement TEXT NOT NULL DEFAULT '',
    is_regex INTEGER NOT NULL CHECK (is_regex IN (0, 1)),
    is_enabled INTEGER NOT NULL CHECK (is_enabled IN (0, 1)),
    scope_title INTEGER NOT NULL CHECK (scope_title IN (0, 1)),
    scope_content INTEGER NOT NULL CHECK (scope_content IN (0, 1)),
    scope TEXT NOT NULL DEFAULT '',
    exclude_scope TEXT NOT NULL DEFAULT '',
    sort_order INTEGER NOT NULL DEFAULT 0,
    timeout_ms INTEGER NOT NULL DEFAULT 3000 CHECK (timeout_ms > 0)
) STRICT;

CREATE INDEX idx_novel_rules_order ON novel_rules(position);

-- module: music/30_music_songs.sql
-- Shared native music permanent song identities and platform source facts (owner CAS uses owner_revisions).
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
-- Opaque third-party platform wire fields and format lists live in music_song_source_extras.
CREATE TABLE music_song_sources (
    song_id TEXT NOT NULL REFERENCES music_songs(song_id) ON DELETE CASCADE,
    source_key TEXT NOT NULL UNIQUE,
    position INTEGER NOT NULL CHECK (position >= 0),
    platform TEXT NOT NULL,
    info_source TEXT NOT NULL,
    songmid TEXT,
    mid TEXT,
    native_id TEXT,
    hash TEXT,
    name TEXT,
    singer TEXT,
    album_name TEXT,
    duration REAL CHECK (duration IS NULL OR duration > 0),
    img TEXT,
    quality_known INTEGER CHECK (quality_known IS NULL OR quality_known IN (0, 1)),
    PRIMARY KEY (song_id, source_key)
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

-- Bounded third-party platform wire metadata (types, audioFormats, newRateFormats, upstream IDs & extra fields).
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
    PRIMARY KEY (scope, collection_id, binding_index, remote_entry_id),
    FOREIGN KEY (scope, collection_id, binding_index)
        REFERENCES music_collection_bindings(scope, collection_id, binding_index) ON DELETE CASCADE
) STRICT;

CREATE INDEX idx_music_collection_binding_map_song ON music_collection_binding_map(song_id);

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
CREATE INDEX idx_music_queue_entries_song ON music_queue_entries(song_id);
CREATE INDEX idx_music_queue_entries_similar_song ON music_queue_entries(ctx_similar_based_on_song_id);

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
    provider_pid TEXT,
    provider_sid TEXT,
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
    return_source_search_keyword TEXT
) STRICT;

CREATE INDEX idx_music_queue_similar_seed_song ON music_queue_similar_state(seed_song_id);

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
CREATE INDEX idx_music_queue_similar_return_song ON music_queue_similar_return_entries(song_id);
CREATE INDEX idx_music_queue_similar_return_similar_song ON music_queue_similar_return_entries(ctx_similar_based_on_song_id);

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

CREATE INDEX idx_music_queue_similar_batches_song ON music_queue_similar_batches(based_on_song_id);

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
CREATE INDEX idx_music_history_playlists_similar_song ON music_history_playlists(ctx_similar_based_on_song_id);

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
CREATE INDEX idx_music_history_playlist_entries_song ON music_history_playlist_entries(song_id);
CREATE INDEX idx_music_history_playlist_entries_similar_song ON music_history_playlist_entries(ctx_similar_based_on_song_id);

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
CREATE INDEX idx_music_history_day_songs_song ON music_history_day_songs(song_id);
CREATE INDEX idx_music_history_day_songs_similar_song ON music_history_day_songs(ctx_similar_based_on_song_id);

-- module: music/34_music_common.sql
-- Shared native music settings, appearance, source exclusions, and lyrics cache (Windows & iOS).
CREATE TABLE music_catalog_source (
    scope TEXT PRIMARY KEY NOT NULL,
    revision INTEGER NOT NULL DEFAULT 0 CHECK (revision BETWEEN 0 AND 9007199254740991),
    platform TEXT NOT NULL DEFAULT 'wy'
) STRICT;

CREATE TABLE music_appearance (
    scope TEXT PRIMARY KEY NOT NULL,
    revision INTEGER NOT NULL DEFAULT 0 CHECK (revision BETWEEN 0 AND 9007199254740991),
    playlist_view TEXT NOT NULL,
    player_theme TEXT NOT NULL,
    color_mode TEXT NOT NULL,
    color_palette TEXT NOT NULL,
    lock_lyric INTEGER NOT NULL DEFAULT 0 CHECK (lock_lyric IN (0, 1)),
    lyric_translation INTEGER NOT NULL DEFAULT 1 CHECK (lyric_translation IN (0, 1)),
    lyric_background INTEGER NOT NULL DEFAULT 0 CHECK (lyric_background IN (0, 1))
) STRICT;

CREATE TABLE music_playback_settings (
    scope TEXT PRIMARY KEY NOT NULL,
    revision INTEGER NOT NULL DEFAULT 0 CHECK (revision BETWEEN 0 AND 9007199254740991),
    mode INTEGER NOT NULL DEFAULT 1,
    has_mode INTEGER NOT NULL DEFAULT 0 CHECK (has_mode IN (0, 1)),
    lyrics_enabled INTEGER NOT NULL DEFAULT 1 CHECK (lyrics_enabled IN (0, 1)),
    online_revision INTEGER CHECK (online_revision IS NULL OR online_revision BETWEEN 0 AND 9007199254740991),
    show_translation INTEGER NOT NULL DEFAULT 1 CHECK (show_translation IN (0, 1)),
    quality_request TEXT,
    quality_revision INTEGER CHECK (quality_revision IS NULL OR quality_revision BETWEEN 0 AND 9007199254740991),
    official_revision INTEGER CHECK (official_revision IS NULL OR official_revision BETWEEN 0 AND 9007199254740991),
    metadata_revision INTEGER CHECK (metadata_revision IS NULL OR metadata_revision BETWEEN 0 AND 9007199254740991),
    exclusions_revision INTEGER CHECK (exclusions_revision IS NULL OR exclusions_revision BETWEEN 0 AND 9007199254740991),
    position_occurrence_id TEXT,
    position_seconds REAL NOT NULL DEFAULT 0
) STRICT;

CREATE TABLE music_playback_lists (
    scope TEXT NOT NULL REFERENCES music_playback_settings(scope) ON DELETE CASCADE,
    list_kind TEXT NOT NULL CHECK (list_kind IN (
        'qualityOrder', 'qualityEnabled', 'spatialOrder', 'spatialEnabled',
        'officialPlatforms', 'officialEnabled', 'metadataPlatforms', 'metadataEnabled'
    )),
    position INTEGER NOT NULL CHECK (position >= 0),
    item_value TEXT NOT NULL,
    PRIMARY KEY (scope, list_kind, position)
) STRICT;

CREATE TABLE music_source_exclusions (
    scope TEXT NOT NULL REFERENCES music_playback_settings(scope) ON DELETE CASCADE,
    exclusion_kind TEXT NOT NULL DEFAULT 'source' CHECK (exclusion_kind IN ('source', 'recording')),
    exclusion_id TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    song_id TEXT NOT NULL,
    provider TEXT NOT NULL DEFAULT '',
    platform TEXT NOT NULL DEFAULT '',
    provider_name TEXT NOT NULL DEFAULT '',
    title TEXT NOT NULL DEFAULT '',
    artist TEXT NOT NULL DEFAULT '',
    source_key TEXT,
    excluded_at INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (scope, exclusion_kind, exclusion_id)
) STRICT;

CREATE INDEX idx_music_source_exclusions_order ON music_source_exclusions(scope, exclusion_kind, position);
CREATE INDEX idx_music_source_exclusions_song ON music_source_exclusions(song_id);

CREATE TABLE music_lyrics (
    song_id TEXT PRIMARY KEY NOT NULL,
    source_key TEXT NOT NULL,
    updated_at REAL NOT NULL,
    stored_bytes INTEGER NOT NULL CHECK (stored_bytes >= 0)
) STRICT;

CREATE INDEX idx_music_lyrics_updated ON music_lyrics(updated_at);

CREATE TABLE music_lyric_lines (
    song_id TEXT NOT NULL REFERENCES music_lyrics(song_id) ON DELETE CASCADE,
    position INTEGER NOT NULL CHECK (position >= 0),
    line_index INTEGER NOT NULL CHECK (line_index >= 0),
    time_sec REAL NOT NULL,
    end_sec REAL,
    text TEXT NOT NULL,
    translation TEXT,
    romanization TEXT,
    PRIMARY KEY (song_id, position)
) STRICT;

CREATE TABLE music_lyric_words (
    song_id TEXT NOT NULL,
    line_position INTEGER NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    time_sec REAL NOT NULL,
    duration_sec REAL NOT NULL,
    offset_sec REAL,
    text TEXT NOT NULL,
    PRIMARY KEY (song_id, line_position, position),
    FOREIGN KEY (song_id, line_position) REFERENCES music_lyric_lines(song_id, position) ON DELETE CASCADE
) STRICT;

-- module: ios/40_film_ios.sql
-- iOS-specific film player preferences, source probes, and video site intents (owner CAS uses owner_revisions).
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
    haptic_seek INTEGER NOT NULL CHECK (haptic_seek IN (0, 1)),
    row_revision INTEGER NOT NULL CHECK (row_revision BETWEEN 1 AND 9007199254740991)
) STRICT;

CREATE TABLE ios_film_source_probes (
    installation_id TEXT PRIMARY KEY NOT NULL,
    input_revision INTEGER NOT NULL,
    imported_at INTEGER,
    updated_at INTEGER,
    update_state TEXT NOT NULL,
    update_error TEXT,
    source_hash TEXT
) STRICT;

CREATE TABLE ios_film_source_sites (
    installation_id TEXT NOT NULL REFERENCES ios_film_source_probes(installation_id) ON DELETE CASCADE,
    site_key TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    name TEXT NOT NULL,
    api TEXT NOT NULL,
    PRIMARY KEY (installation_id, site_key)
) STRICT;

CREATE TABLE ios_film_source_actions (
    installation_id TEXT NOT NULL,
    site_key TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    action TEXT NOT NULL,
    PRIMARY KEY (installation_id, site_key, position),
    FOREIGN KEY (installation_id, site_key) REFERENCES ios_film_source_sites(installation_id, site_key) ON DELETE CASCADE
) STRICT;

CREATE TABLE ios_film_source_methods (
    installation_id TEXT NOT NULL,
    site_key TEXT NOT NULL,
    action TEXT NOT NULL,
    method TEXT NOT NULL,
    PRIMARY KEY (installation_id, site_key, action),
    FOREIGN KEY (installation_id, site_key) REFERENCES ios_film_source_sites(installation_id, site_key) ON DELETE CASCADE
) STRICT;

CREATE TABLE ios_film_video_intents (
    installation_id TEXT NOT NULL,
    site_key TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    name TEXT NOT NULL,
    api TEXT NOT NULL,
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
    home_visible INTEGER NOT NULL CHECK (home_visible IN (0, 1)),
    search_enabled INTEGER NOT NULL CHECK (search_enabled IN (0, 1)),
    aggregate_images INTEGER NOT NULL CHECK (aggregate_images IN (0, 1)),
    PRIMARY KEY (installation_id, site_key)
) STRICT;

CREATE UNIQUE INDEX idx_ios_film_one_aggregate_images ON ios_film_video_intents(aggregate_images) WHERE aggregate_images = 1;

CREATE TABLE ios_film_video_actions (
    installation_id TEXT NOT NULL,
    site_key TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    action TEXT NOT NULL,
    PRIMARY KEY (installation_id, site_key, position),
    FOREIGN KEY (installation_id, site_key) REFERENCES ios_film_video_intents(installation_id, site_key) ON DELETE CASCADE
) STRICT;

CREATE TABLE ios_film_video_methods (
    installation_id TEXT NOT NULL,
    site_key TEXT NOT NULL,
    action TEXT NOT NULL,
    method TEXT NOT NULL,
    PRIMARY KEY (installation_id, site_key, action),
    FOREIGN KEY (installation_id, site_key) REFERENCES ios_film_video_intents(installation_id, site_key) ON DELETE CASCADE
) STRICT;

CREATE TABLE ios_film_video_detections (
    installation_id TEXT NOT NULL,
    site_key TEXT NOT NULL,
    result TEXT NOT NULL,
    checked_at INTEGER NOT NULL CHECK (checked_at BETWEEN 0 AND 9007199254740991),
    playback_resolved INTEGER NOT NULL CHECK (playback_resolved IN (0, 1)),
    PRIMARY KEY (installation_id, site_key),
    FOREIGN KEY (installation_id, site_key) REFERENCES ios_film_video_intents(installation_id, site_key) ON DELETE CASCADE
) STRICT;

CREATE TABLE ios_film_video_probe_steps (
    installation_id TEXT NOT NULL,
    site_key TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    operation TEXT NOT NULL,
    state TEXT NOT NULL,
    error TEXT,
    PRIMARY KEY (installation_id, site_key, position),
    FOREIGN KEY (installation_id, site_key) REFERENCES ios_film_video_detections(installation_id, site_key) ON DELETE CASCADE
) STRICT;

-- module: ios/41_novel_ios.sql
-- iOS-specific novel shelf, reading progress, dynamic source variables, and reading settings.
CREATE TABLE ios_novel_books (
    book_id TEXT PRIMARY KEY NOT NULL,
    installation_id TEXT NOT NULL,
    book_url TEXT NOT NULL,
    identity TEXT NOT NULL,
    name TEXT NOT NULL,
    author TEXT NOT NULL,
    toc_url TEXT NOT NULL,
    cover_url TEXT NOT NULL,
    intro TEXT NOT NULL,
    latest_chapter_title TEXT NOT NULL,
    on_shelf INTEGER NOT NULL CHECK (on_shelf IN (0, 1)),
    pinned_at REAL,
    added_at REAL NOT NULL,
    row_revision INTEGER NOT NULL CHECK (row_revision BETWEEN 1 AND 9007199254740991),
    UNIQUE (installation_id, book_url)
) STRICT;

CREATE UNIQUE INDEX idx_ios_novel_book_identity ON ios_novel_books(identity) WHERE identity <> '';
CREATE INDEX idx_ios_novel_shelf ON ios_novel_books(on_shelf, pinned_at DESC, added_at DESC, book_id);

CREATE TABLE ios_novel_progress (
    book_id TEXT PRIMARY KEY NOT NULL REFERENCES ios_novel_books(book_id) ON DELETE CASCADE,
    chapter_url TEXT NOT NULL,
    chapter_title TEXT NOT NULL,
    chapter_index INTEGER NOT NULL CHECK (chapter_index >= 0),
    block_index INTEGER NOT NULL CHECK (block_index >= 0),
    utf16_offset INTEGER NOT NULL CHECK (utf16_offset >= 0),
    ratio REAL NOT NULL CHECK (ratio BETWEEN 0 AND 1),
    read_at REAL NOT NULL,
    row_revision INTEGER NOT NULL CHECK (row_revision BETWEEN 1 AND 9007199254740991)
) STRICT;

CREATE INDEX idx_ios_novel_history ON ios_novel_progress(read_at DESC, book_id);

-- Third-party book source dynamic variable tree.
CREATE TABLE ios_novel_variable_nodes (
    book_id TEXT NOT NULL REFERENCES ios_novel_books(book_id) ON DELETE CASCADE,
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
    font_size REAL NOT NULL,
    font_weight REAL NOT NULL,
    font_name TEXT NOT NULL,
    line_spacing REAL NOT NULL,
    paragraph_spacing REAL NOT NULL,
    margin REAL NOT NULL,
    content_width REAL NOT NULL,
    conversion TEXT NOT NULL,
    theme INTEGER NOT NULL,
    foreground TEXT NOT NULL,
    background TEXT NOT NULL,
    background_image BLOB,
    automatic_speed REAL NOT NULL,
    row_revision INTEGER NOT NULL CHECK (row_revision BETWEEN 1 AND 9007199254740991)
) STRICT;

-- module: ios/42_music_ios.sql
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
