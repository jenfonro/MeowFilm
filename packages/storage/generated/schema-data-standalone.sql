-- module: data/core/00_identity.sql
-- Core database identity and monotonic owner revisions (permanent data database).
CREATE TABLE storage_identity (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    schema_hash TEXT NOT NULL CHECK (length(schema_hash) = 64)
) STRICT;

CREATE TABLE owner_revisions (
    owner TEXT PRIMARY KEY NOT NULL,
    revision INTEGER NOT NULL CHECK (revision >= 1)
) STRICT;

-- module: data/runtime/10_music_scripts.sql
-- Music script catalog, declared capabilities, check outcomes, and per-key LX guest KV (revision via owner_revisions).
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

-- module: data/runtime/11_source_runtime.sql
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
    respond_time_ms REAL,
    last_update_at_ms INTEGER,
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
    update_time_rule TEXT,
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

-- module: data/runtime/12_cat_runtime.sql
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

-- module: data/runtime/13_pan.sql
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

-- module: data/runtime/14_metadata_settings.sql
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

-- module: data/runtime/15_music_anonymous.sql
-- Music platform anonymous client protocol state (wy/kg/tx; owner CAS uses owner_revisions).
CREATE TABLE music_anonymous_wy_cookies (
    cookie_name TEXT PRIMARY KEY NOT NULL,
    cookie_value TEXT NOT NULL
) STRICT;

CREATE TABLE music_anonymous_kg (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    seed TEXT NOT NULL,
    mid TEXT NOT NULL,
    dfid TEXT
) STRICT;

CREATE TABLE music_anonymous_tx (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
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

-- module: data/runtime/20_film_playback.sql
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

-- Upstream CatVod/Source JSON identifiers (detailId/lineId/year/metadataId/siteId) preserve exact JS string vs integer identity.
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
    enabled INTEGER CHECK (enabled IS NULL OR enabled IN (0, 1)),
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
