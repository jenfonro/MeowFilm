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
-- Shared native music playback queue and priority/history/failed lists.
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

-- module: windows/40_app.sql
-- Windows desktop shell appearance, window close behavior, media shortcut bindings, and file grants.
CREATE TABLE windows_app_appearance (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    theme_color TEXT NOT NULL
) STRICT;

CREATE TABLE windows_app_window (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    close_behavior TEXT NOT NULL CHECK (close_behavior IN ('ask', 'minimizeToTray', 'exit'))
) STRICT;

CREATE TABLE windows_app_media_controls (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    global_enabled INTEGER NOT NULL CHECK (global_enabled IN (0, 1)),
    system_media_enabled INTEGER NOT NULL CHECK (system_media_enabled IN (0, 1))
) STRICT;

CREATE TABLE windows_app_media_bindings (
    action TEXT PRIMARY KEY NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    local_key TEXT,
    local_modifiers INTEGER,
    global_key TEXT,
    global_modifiers INTEGER,
    CHECK ((local_key IS NULL) = (local_modifiers IS NULL)),
    CHECK ((global_key IS NULL) = (global_modifiers IS NULL))
) STRICT;

CREATE TABLE windows_file_grants (
    grant_id TEXT PRIMARY KEY NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    file_path TEXT NOT NULL,
    title TEXT NOT NULL DEFAULT ''
) STRICT;

CREATE INDEX idx_windows_file_grants_order ON windows_file_grants(position);

-- module: windows/41_film_sources.sql
-- Windows-specific film source projections and video site detection records.
CREATE TABLE windows_film_source_cats (
    installation_id TEXT PRIMARY KEY NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    name TEXT NOT NULL,
    source_type TEXT NOT NULL CHECK (source_type IN ('local', 'network')),
    source_url TEXT,
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
    imported_at INTEGER,
    updated_at INTEGER,
    update_state TEXT NOT NULL,
    update_error TEXT,
    source_hash TEXT,
    input_revision INTEGER NOT NULL
) STRICT;

CREATE TABLE windows_film_source_cat_sites (
    installation_id TEXT NOT NULL REFERENCES windows_film_source_cats(installation_id) ON DELETE CASCADE,
    site_key TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    name TEXT NOT NULL,
    api TEXT NOT NULL,
    PRIMARY KEY (installation_id, site_key)
) STRICT;

CREATE TABLE windows_film_source_cat_site_actions (
    installation_id TEXT NOT NULL,
    site_key TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    action TEXT NOT NULL,
    PRIMARY KEY (installation_id, site_key, position),
    FOREIGN KEY (installation_id, site_key)
        REFERENCES windows_film_source_cat_sites(installation_id, site_key) ON DELETE CASCADE
) STRICT;

CREATE TABLE windows_film_source_cat_site_methods (
    installation_id TEXT NOT NULL,
    site_key TEXT NOT NULL,
    method_key TEXT NOT NULL,
    method_name TEXT NOT NULL,
    PRIMARY KEY (installation_id, site_key, method_key),
    FOREIGN KEY (installation_id, site_key)
        REFERENCES windows_film_source_cat_sites(installation_id, site_key) ON DELETE CASCADE
) STRICT;

CREATE TABLE windows_film_source_videos (
    installation_id TEXT NOT NULL,
    site_key TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    sort_order INTEGER NOT NULL,
    name TEXT NOT NULL,
    api TEXT NOT NULL,
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
    home_visible INTEGER NOT NULL CHECK (home_visible IN (0, 1)),
    search_enabled INTEGER NOT NULL CHECK (search_enabled IN (0, 1)),
    aggregate_images INTEGER NOT NULL CHECK (aggregate_images IN (0, 1)),
    detection_result TEXT,
    detection_checked_at INTEGER,
    detection_playback_resolved INTEGER CHECK (detection_playback_resolved IS NULL OR detection_playback_resolved IN (0, 1)),
    PRIMARY KEY (installation_id, site_key)
) STRICT;

CREATE INDEX idx_windows_film_source_videos_order ON windows_film_source_videos(position);

CREATE TABLE windows_film_source_video_actions (
    installation_id TEXT NOT NULL,
    site_key TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    action TEXT NOT NULL,
    PRIMARY KEY (installation_id, site_key, position),
    FOREIGN KEY (installation_id, site_key)
        REFERENCES windows_film_source_videos(installation_id, site_key) ON DELETE CASCADE
) STRICT;

CREATE TABLE windows_film_source_video_methods (
    installation_id TEXT NOT NULL,
    site_key TEXT NOT NULL,
    method_key TEXT NOT NULL,
    method_name TEXT NOT NULL,
    PRIMARY KEY (installation_id, site_key, method_key),
    FOREIGN KEY (installation_id, site_key)
        REFERENCES windows_film_source_videos(installation_id, site_key) ON DELETE CASCADE
) STRICT;

CREATE TABLE windows_film_source_video_steps (
    installation_id TEXT NOT NULL,
    site_key TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    operation TEXT NOT NULL,
    state TEXT NOT NULL,
    error TEXT,
    PRIMARY KEY (installation_id, site_key, position),
    FOREIGN KEY (installation_id, site_key)
        REFERENCES windows_film_source_videos(installation_id, site_key) ON DELETE CASCADE
) STRICT;

-- module: windows/42_novel.sql
-- Windows-specific novel shelf, reading progress, reader appearance, and source configuration (revisions use owner_revisions).
CREATE TABLE windows_novel_entries (
    book_id TEXT PRIMARY KEY NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    identity_key TEXT NOT NULL,
    on_shelf INTEGER NOT NULL CHECK (on_shelf IN (0, 1)),
    added_at TEXT,
    pinned_at TEXT,
    read_at TEXT,
    chapter_count INTEGER NOT NULL DEFAULT 0 CHECK (chapter_count >= 0),
    content_signature TEXT NOT NULL DEFAULT '',
    installation_id TEXT NOT NULL,
    book_revision INTEGER NOT NULL DEFAULT 0,
    book_url TEXT NOT NULL,
    name TEXT NOT NULL,
    author TEXT NOT NULL DEFAULT '',
    cover_url TEXT NOT NULL DEFAULT '',
    intro TEXT NOT NULL DEFAULT '',
    toc_url TEXT NOT NULL DEFAULT '',
    latest_chapter_title TEXT NOT NULL DEFAULT '',
    anchor_chapter_url TEXT,
    anchor_chapter_title TEXT,
    anchor_chapter_index INTEGER,
    anchor_block_index INTEGER,
    anchor_utf16_offset INTEGER,
    anchor_ratio REAL
) STRICT;

CREATE INDEX idx_windows_novel_entries_order ON windows_novel_entries(position);
CREATE INDEX idx_windows_novel_entries_shelf ON windows_novel_entries(on_shelf, pinned_at DESC, added_at DESC);
CREATE INDEX idx_windows_novel_entries_history ON windows_novel_entries(read_at DESC);

-- Bounded third-party source dynamic book fields (e.g. variable / rule-specific attributes).
CREATE TABLE windows_novel_book_extras (
    book_id TEXT NOT NULL REFERENCES windows_novel_entries(book_id) ON DELETE CASCADE,
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

CREATE TABLE windows_novel_appearance (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    mode TEXT NOT NULL,
    font_size REAL NOT NULL,
    font_family TEXT NOT NULL,
    bold INTEGER NOT NULL CHECK (bold IN (0, 1)),
    line_height REAL NOT NULL,
    paragraph_spacing REAL NOT NULL,
    margin REAL NOT NULL,
    content_width REAL NOT NULL,
    conversion TEXT NOT NULL,
    theme INTEGER NOT NULL,
    background TEXT NOT NULL,
    foreground TEXT NOT NULL,
    background_image TEXT NOT NULL DEFAULT '',
    auto_speed REAL NOT NULL
) STRICT;

CREATE TABLE windows_novel_sources (
    installation_id TEXT PRIMARY KEY NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    revision INTEGER NOT NULL CHECK (revision >= 1),
    name TEXT NOT NULL,
    source_url TEXT NOT NULL,
    source_group TEXT NOT NULL DEFAULT '',
    sort_order INTEGER NOT NULL DEFAULT 0,
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
    enabled_explore INTEGER NOT NULL CHECK (enabled_explore IN (0, 1)),
    protocol TEXT NOT NULL
) STRICT;

CREATE INDEX idx_windows_novel_sources_order ON windows_novel_sources(position);

-- Bounded third-party novel source definition value tree.
CREATE TABLE windows_novel_source_definition_nodes (
    installation_id TEXT NOT NULL REFERENCES windows_novel_sources(installation_id) ON DELETE CASCADE,
    node_id INTEGER NOT NULL CHECK (node_id >= 0),
    parent_id INTEGER,
    prop_key TEXT,
    item_index INTEGER,
    kind TEXT NOT NULL CHECK (kind IN ('null', 'boolean', 'integer', 'real', 'number_text', 'text', 'array', 'object')),
    bool_value INTEGER CHECK (bool_value IN (0, 1)),
    int_value INTEGER,
    real_value REAL,
    text_value TEXT,
    PRIMARY KEY (installation_id, node_id)
) STRICT;

-- module: windows/43_music_windows.sql
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
