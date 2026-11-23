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
    installation_id TEXT NOT NULL REFERENCES music_script_installations(installation_id) ON DELETE CASCADE,
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

CREATE TABLE source_private_kv_entries (
    installation_id TEXT NOT NULL,
    profile_suffix TEXT NOT NULL,
    entry_key TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    PRIMARY KEY (installation_id, profile_suffix, entry_key),
    FOREIGN KEY (installation_id, profile_suffix)
        REFERENCES source_private_profiles(installation_id, profile_suffix) ON DELETE CASCADE
) STRICT;

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
    row_revision INTEGER NOT NULL DEFAULT 1 CHECK (row_revision BETWEEN 1 AND 9007199254740991)
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
    metadata_view_provider TEXT NOT NULL CHECK (metadata_view_provider IN ('tmdb', 'douban'))
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
    row_revision INTEGER NOT NULL CHECK (row_revision BETWEEN 1 AND 9007199254740991),
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
    CHECK ((detail_id_kind IS NULL) = (detail_id_text IS NULL))
) STRICT;

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

CREATE INDEX idx_film_blocked_order ON film_blocked_sources(position_order);

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

CREATE TABLE film_group_members (
    group_id TEXT NOT NULL REFERENCES film_groups(group_id) ON DELETE CASCADE,
    position INTEGER NOT NULL CHECK (position >= 0),
    member_identity TEXT NOT NULL,
    PRIMARY KEY (group_id, position)
) STRICT;

-- module: data/shared/25_shared_film.sql
-- Shared native App film configuration inputs, favorites, source probes, and video sources (Windows & iOS).
CREATE TABLE cat_scripts (
    installation_id TEXT PRIMARY KEY NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    name TEXT NOT NULL,
    source_type TEXT NOT NULL CHECK (source_type IN ('local', 'network')),
    source_url TEXT,
    local_path TEXT,
    local_hash TEXT,
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
    input_revision INTEGER NOT NULL CHECK (input_revision BETWEEN 1 AND 9007199254740991)
) STRICT;

CREATE INDEX idx_cat_scripts_order ON cat_scripts(position, installation_id);

CREATE TABLE film_source_deletions (
    installation_id TEXT NOT NULL,
    site_key TEXT NOT NULL,
    position INTEGER NOT NULL DEFAULT 0 CHECK (position >= 0),
    PRIMARY KEY (installation_id, site_key)
) STRICT;

CREATE TABLE film_favorites (
    favorite_id TEXT PRIMARY KEY NOT NULL,
    item_id TEXT NOT NULL,
    target_kind TEXT CHECK (target_kind IS NULL OR target_kind IN ('movie', 'tv', 'source', 'group')),
    target_title TEXT,
    target_year INTEGER,
    metadata_provider TEXT,
    metadata_id TEXT,
    season INTEGER,
    episode INTEGER,
    global_episode INTEGER,
    source_key TEXT,
    source_engine TEXT,
    source_installation_id TEXT,
    source_site_id TEXT,
    source_detail_kind TEXT CHECK (source_detail_kind IS NULL OR source_detail_kind IN ('text', 'integer')),
    source_detail_text TEXT,
    source_line_kind TEXT CHECK (source_line_kind IS NULL OR source_line_kind IN ('text', 'integer')),
    source_line_text TEXT,
    added_at_ms INTEGER NOT NULL DEFAULT 0,
    CHECK ((source_detail_kind IS NULL) = (source_detail_text IS NULL)),
    CHECK ((source_line_kind IS NULL) = (source_line_text IS NULL))
) STRICT;

CREATE TABLE film_favorite_members (
    favorite_id TEXT NOT NULL REFERENCES film_favorites(favorite_id) ON DELETE CASCADE,
    position INTEGER NOT NULL CHECK (position >= 0),
    source_key TEXT NOT NULL,
    detail_kind TEXT NOT NULL CHECK (detail_kind IN ('text', 'integer')),
    detail_text TEXT NOT NULL,
    PRIMARY KEY (favorite_id, position)
) STRICT;

-- Unified cross-platform film Cat source probes and video site configuration / detection records.
-- Cat configuration fields (name, source_type, source_url, enabled) are owned exclusively by cat_scripts.
CREATE TABLE film_source_probes (
    installation_id TEXT PRIMARY KEY NOT NULL,
    position INTEGER NOT NULL DEFAULT 0 CHECK (position >= 0),
    input_revision INTEGER NOT NULL,
    imported_at_ms INTEGER,
    updated_at_ms INTEGER,
    update_state TEXT NOT NULL,
    update_error TEXT,
    source_hash TEXT
) STRICT;

CREATE INDEX idx_film_source_probes_order ON film_source_probes(position, installation_id);

CREATE TABLE film_source_sites (
    installation_id TEXT NOT NULL REFERENCES film_source_probes(installation_id) ON DELETE CASCADE,
    site_key TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    name TEXT NOT NULL,
    api TEXT NOT NULL,
    PRIMARY KEY (installation_id, site_key)
) STRICT;

CREATE TABLE film_source_site_actions (
    installation_id TEXT NOT NULL,
    site_key TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    action TEXT NOT NULL,
    PRIMARY KEY (installation_id, site_key, position),
    FOREIGN KEY (installation_id, site_key)
        REFERENCES film_source_sites(installation_id, site_key) ON DELETE CASCADE
) STRICT;

CREATE TABLE film_source_site_methods (
    installation_id TEXT NOT NULL,
    site_key TEXT NOT NULL,
    action TEXT NOT NULL,
    method TEXT NOT NULL,
    PRIMARY KEY (installation_id, site_key, action),
    FOREIGN KEY (installation_id, site_key)
        REFERENCES film_source_sites(installation_id, site_key) ON DELETE CASCADE
) STRICT;

CREATE TABLE film_video_sources (
    installation_id TEXT NOT NULL,
    site_key TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    sort_order INTEGER NOT NULL DEFAULT 0,
    name TEXT NOT NULL,
    api TEXT NOT NULL,
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
    home_visible INTEGER NOT NULL CHECK (home_visible IN (0, 1)),
    search_enabled INTEGER NOT NULL CHECK (search_enabled IN (0, 1)),
    aggregate_images INTEGER NOT NULL CHECK (aggregate_images IN (0, 1)),
    detection_result TEXT,
    detection_checked_at_ms INTEGER CHECK (detection_checked_at_ms IS NULL OR detection_checked_at_ms BETWEEN 0 AND 9007199254740991),
    detection_playback_resolved INTEGER CHECK (detection_playback_resolved IS NULL OR detection_playback_resolved IN (0, 1)),
    PRIMARY KEY (installation_id, site_key),
    CHECK (
        (detection_result IS NULL) = (detection_checked_at_ms IS NULL) AND
        (detection_result IS NULL) = (detection_playback_resolved IS NULL)
    )
) STRICT;

CREATE INDEX idx_film_video_sources_order ON film_video_sources(position);
CREATE UNIQUE INDEX idx_film_video_one_aggregate_images ON film_video_sources(aggregate_images) WHERE aggregate_images = 1;

CREATE TABLE film_video_actions (
    installation_id TEXT NOT NULL,
    site_key TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    action TEXT NOT NULL,
    PRIMARY KEY (installation_id, site_key, position),
    FOREIGN KEY (installation_id, site_key)
        REFERENCES film_video_sources(installation_id, site_key) ON DELETE CASCADE
) STRICT;

CREATE TABLE film_video_methods (
    installation_id TEXT NOT NULL,
    site_key TEXT NOT NULL,
    action TEXT NOT NULL,
    method TEXT NOT NULL,
    PRIMARY KEY (installation_id, site_key, action),
    FOREIGN KEY (installation_id, site_key)
        REFERENCES film_video_sources(installation_id, site_key) ON DELETE CASCADE
) STRICT;

CREATE TABLE film_video_probe_steps (
    installation_id TEXT NOT NULL,
    site_key TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    operation TEXT NOT NULL,
    state TEXT NOT NULL,
    error TEXT,
    PRIMARY KEY (installation_id, site_key, position),
    FOREIGN KEY (installation_id, site_key)
        REFERENCES film_video_sources(installation_id, site_key) ON DELETE CASCADE
) STRICT;

-- module: data/shared/26_shared_novel.sql
-- Shared native App novel bookshelf, reading progress, dynamic source variables, reading settings, and replacement rules (Windows & iOS).
CREATE TABLE novel_books (
    book_id TEXT PRIMARY KEY NOT NULL,
    installation_id TEXT NOT NULL,
    book_url TEXT NOT NULL,
    identity_key TEXT NOT NULL DEFAULT '',
    name TEXT NOT NULL,
    author TEXT NOT NULL DEFAULT '',
    toc_url TEXT NOT NULL DEFAULT '',
    cover_url TEXT NOT NULL DEFAULT '',
    intro TEXT NOT NULL DEFAULT '',
    latest_chapter_title TEXT NOT NULL DEFAULT '',
    on_shelf INTEGER NOT NULL CHECK (on_shelf IN (0, 1)),
    pinned_at_ms INTEGER,
    added_at_ms INTEGER NOT NULL DEFAULT 0,
    UNIQUE (installation_id, book_url)
) STRICT;

CREATE UNIQUE INDEX idx_novel_books_identity ON novel_books(identity_key) WHERE identity_key <> '';

CREATE TABLE novel_progress (
    book_id TEXT PRIMARY KEY NOT NULL REFERENCES novel_books(book_id) ON DELETE CASCADE,
    chapter_url TEXT NOT NULL,
    chapter_title TEXT NOT NULL,
    chapter_index INTEGER NOT NULL CHECK (chapter_index >= 0),
    block_index INTEGER NOT NULL CHECK (block_index >= 0),
    utf16_offset INTEGER NOT NULL CHECK (utf16_offset >= 0),
    ratio REAL NOT NULL CHECK (ratio BETWEEN 0 AND 1),
    read_at_ms INTEGER NOT NULL
) STRICT;

-- Third-party book source dynamic variable / extra attribute tree.
CREATE TABLE novel_book_variables (
    book_id TEXT NOT NULL REFERENCES novel_books(book_id) ON DELETE CASCADE,
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

CREATE TABLE novel_reading_settings (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    mode TEXT NOT NULL,
    font_size REAL NOT NULL,
    font_weight REAL NOT NULL DEFAULT 400,
    font_family TEXT NOT NULL,
    line_spacing REAL NOT NULL,
    paragraph_spacing REAL NOT NULL,
    margin REAL NOT NULL,
    content_width REAL NOT NULL,
    conversion TEXT NOT NULL,
    theme INTEGER NOT NULL,
    foreground TEXT NOT NULL,
    background TEXT NOT NULL,
    auto_speed REAL NOT NULL
) STRICT;

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

CREATE INDEX idx_novel_rules_order ON novel_rules(position, rule_id);

-- module: data/shared/27_shared_playback.sql
-- Shared native App device playback volume (Windows & iOS).
CREATE TABLE playback_volume (
    owner TEXT PRIMARY KEY NOT NULL,
    revision INTEGER NOT NULL DEFAULT 0 CHECK (revision BETWEEN 0 AND 9007199254740991),
    volume REAL NOT NULL CHECK (volume BETWEEN 0 AND 1)
) STRICT;

-- module: data/music/30_music_songs.sql
-- Shared native music permanent song identities and platform source facts (owner CAS uses owner_revisions).
CREATE TABLE music_songs (
    song_id TEXT PRIMARY KEY NOT NULL,
    title TEXT NOT NULL DEFAULT '',
    artist TEXT NOT NULL DEFAULT '',
    album TEXT NOT NULL DEFAULT '',
    duration REAL CHECK (duration IS NULL OR duration > 0),
    baseline_duration REAL CHECK (baseline_duration IS NULL OR baseline_duration > 0),
    cover_url TEXT NOT NULL DEFAULT '',
    artwork_source_key TEXT,
    artwork_source_image TEXT,
    match_key TEXT,
    search_query TEXT
) STRICT;

CREATE INDEX idx_music_songs_match_key ON music_songs(match_key, song_id, baseline_duration);
CREATE INDEX idx_music_songs_search ON music_songs(search_query, song_id);

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

-- module: data/music/31_music_contexts.sql
-- Normalized content-addressed playback context shared across queue entries, history entries/days, and similar return queues.
-- Platform-specific context fields live in windows_music_play_context_extras / ios_music_play_context_extras (1:1 CASCADE).
CREATE TABLE music_play_contexts (
    context_id TEXT PRIMARY KEY NOT NULL CHECK (length(context_id) = 64),
    source_title TEXT,
    playlist TEXT,
    source_type TEXT,
    source_id TEXT,
    search_keyword TEXT,
    daily_branch TEXT
) STRICT;

-- module: data/music/32_music_collections.sql
-- Shared native music library state, user collections, song membership, bindings, and intents.
CREATE TABLE music_library_state (
    scope TEXT PRIMARY KEY NOT NULL,
    revision INTEGER NOT NULL DEFAULT 0 CHECK (revision BETWEEN 0 AND 9007199254740991),
    owner_id TEXT NOT NULL DEFAULT 'device',
    library_revision INTEGER NOT NULL DEFAULT 0 CHECK (library_revision BETWEEN 0 AND 9007199254740991),
    next_id INTEGER NOT NULL DEFAULT 1 CHECK (next_id BETWEEN 1 AND 9007199254740991)
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

-- module: data/music/33_music_queue_history.sql
-- Shared native music playback queue and playback history (all timestamps UTC INTEGER ms; play contexts normalized via music_play_contexts).
CREATE TABLE music_queue_state (
    scope TEXT PRIMARY KEY NOT NULL,
    current_id TEXT,
    serial INTEGER NOT NULL DEFAULT 0 CHECK (serial >= 0),
    revision INTEGER NOT NULL DEFAULT 0 CHECK (revision BETWEEN 0 AND 9007199254740991),
    source_id TEXT,
    source_title TEXT,
    source_platform TEXT
) STRICT;

CREATE TABLE music_queue_entries (
    scope TEXT NOT NULL REFERENCES music_queue_state(scope) ON DELETE CASCADE,
    entry_id TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    song_id TEXT NOT NULL REFERENCES music_songs(song_id),
    context_id TEXT REFERENCES music_play_contexts(context_id),
    PRIMARY KEY (scope, entry_id)
) STRICT;

CREATE INDEX idx_music_queue_entries_order ON music_queue_entries(scope, position);
CREATE INDEX idx_music_queue_entries_song ON music_queue_entries(song_id);
CREATE INDEX idx_music_queue_entries_context ON music_queue_entries(context_id);

CREATE TABLE music_queue_list_items (
    scope TEXT NOT NULL REFERENCES music_queue_state(scope) ON DELETE CASCADE,
    list_kind TEXT NOT NULL CHECK (list_kind IN ('priority', 'history', 'failed')),
    position INTEGER NOT NULL CHECK (position >= 0),
    item_value TEXT NOT NULL,
    PRIMARY KEY (scope, list_kind, position)
) STRICT;

CREATE TABLE music_history_playlists (
    scope TEXT NOT NULL,
    playlist_id TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    played_at_ms INTEGER NOT NULL,
    title TEXT NOT NULL DEFAULT '',
    current_id TEXT,
    source_id TEXT,
    source_title TEXT,
    source_platform TEXT,
    PRIMARY KEY (scope, playlist_id)
) STRICT;

CREATE INDEX idx_music_history_playlists_order ON music_history_playlists(scope, position);

CREATE TABLE music_history_playlist_entries (
    scope TEXT NOT NULL,
    playlist_id TEXT NOT NULL,
    entry_id TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    song_id TEXT NOT NULL REFERENCES music_songs(song_id),
    context_id TEXT REFERENCES music_play_contexts(context_id),
    PRIMARY KEY (scope, playlist_id, entry_id),
    FOREIGN KEY (scope, playlist_id) REFERENCES music_history_playlists(scope, playlist_id) ON DELETE CASCADE
) STRICT;

CREATE INDEX idx_music_history_playlist_entries_order ON music_history_playlist_entries(scope, playlist_id, position);
CREATE INDEX idx_music_history_playlist_entries_song ON music_history_playlist_entries(song_id);
CREATE INDEX idx_music_history_playlist_entries_context ON music_history_playlist_entries(context_id);

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
    updated_at_ms INTEGER NOT NULL,
    PRIMARY KEY (scope, day_date)
) STRICT;

CREATE INDEX idx_music_history_days_order ON music_history_days(scope, position);

CREATE TABLE music_history_day_songs (
    scope TEXT NOT NULL,
    day_date TEXT NOT NULL,
    song_id TEXT NOT NULL REFERENCES music_songs(song_id),
    position INTEGER NOT NULL CHECK (position >= 0),
    played_at_ms INTEGER NOT NULL,
    context_id TEXT REFERENCES music_play_contexts(context_id),
    PRIMARY KEY (scope, day_date, song_id),
    FOREIGN KEY (scope, day_date) REFERENCES music_history_days(scope, day_date) ON DELETE CASCADE
) STRICT;

CREATE INDEX idx_music_history_day_songs_order ON music_history_day_songs(scope, day_date, position);
CREATE INDEX idx_music_history_day_songs_song ON music_history_day_songs(song_id);
CREATE INDEX idx_music_history_day_songs_context ON music_history_day_songs(context_id);

-- module: data/music/34_music_settings.sql
-- Shared native music settings, appearance, source exclusions, and search policy (Windows & iOS).
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
    color_palette TEXT NOT NULL
) STRICT;

CREATE TABLE music_playback_settings (
    scope TEXT PRIMARY KEY NOT NULL,
    revision INTEGER NOT NULL DEFAULT 0 CHECK (revision BETWEEN 0 AND 9007199254740991),
    mode INTEGER NOT NULL DEFAULT 1 CHECK (mode IN (1, 2, 3)),
    lyrics_enabled INTEGER NOT NULL DEFAULT 1 CHECK (lyrics_enabled IN (0, 1)),
    quality_request TEXT
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
    exclusion_id TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    song_id TEXT NOT NULL,
    provider TEXT NOT NULL DEFAULT '',
    platform TEXT NOT NULL DEFAULT '',
    provider_name TEXT NOT NULL DEFAULT '',
    title TEXT NOT NULL DEFAULT '',
    artist TEXT NOT NULL DEFAULT '',
    excluded_at_ms INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (scope, exclusion_id)
) STRICT;

CREATE INDEX idx_music_source_exclusions_order ON music_source_exclusions(scope, position);

CREATE TABLE music_search_policy (
    scope TEXT PRIMARY KEY NOT NULL,
    revision INTEGER NOT NULL DEFAULT 0 CHECK (revision BETWEEN 0 AND 9007199254740991),
    songs_pages INTEGER NOT NULL CHECK (songs_pages >= 1),
    songs_concurrency INTEGER NOT NULL CHECK (songs_concurrency >= 1),
    songs_cache_minutes INTEGER NOT NULL CHECK (songs_cache_minutes BETWEEN 1 AND 1440),
    playlists_pages INTEGER NOT NULL CHECK (playlists_pages >= 1),
    playlists_concurrency INTEGER NOT NULL CHECK (playlists_concurrency >= 1),
    playlists_cache_minutes INTEGER NOT NULL CHECK (playlists_cache_minutes BETWEEN 1 AND 1440)
) STRICT;

CREATE TABLE music_search_policy_platforms (
    scope TEXT NOT NULL REFERENCES music_search_policy(scope) ON DELETE CASCADE,
    target_kind TEXT NOT NULL CHECK (target_kind IN ('songs', 'playlists')),
    list_kind TEXT NOT NULL CHECK (list_kind IN ('order', 'enabled')),
    position INTEGER NOT NULL CHECK (position >= 0),
    platform TEXT NOT NULL,
    PRIMARY KEY (scope, target_kind, list_kind, position)
) STRICT;

-- module: data/windows/40_windows_app.sql
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

CREATE INDEX idx_windows_file_grants_order ON windows_file_grants(position, grant_id);

-- Windows-specific film favorite display metadata and manual ordering paired 1:1 with film_favorites.
CREATE TABLE windows_film_favorite_display (
    favorite_id TEXT PRIMARY KEY NOT NULL REFERENCES film_favorites(favorite_id) ON DELETE CASCADE,
    position INTEGER NOT NULL DEFAULT 0 CHECK (position >= 0),
    title TEXT NOT NULL,
    poster TEXT NOT NULL DEFAULT '',
    score TEXT NOT NULL DEFAULT '',
    text_badge TEXT NOT NULL DEFAULT '',
    subtitle TEXT NOT NULL DEFAULT ''
) STRICT;

CREATE INDEX idx_windows_film_favorite_display_order ON windows_film_favorite_display(position);

-- module: data/windows/41_windows_novel_sources.sql
-- Windows-owned native novel source configurations (pushed to source-runtime via library.apply).
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

CREATE INDEX idx_windows_novel_sources_order ON windows_novel_sources(position, installation_id);

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

-- Windows-specific novel bookshelf ordering, chapter metadata, and background image path.
CREATE TABLE windows_novel_book_extras (
    book_id TEXT PRIMARY KEY NOT NULL REFERENCES novel_books(book_id) ON DELETE CASCADE,
    position INTEGER NOT NULL DEFAULT 0 CHECK (position >= 0),
    chapter_count INTEGER NOT NULL DEFAULT 0 CHECK (chapter_count >= 0),
    content_signature TEXT NOT NULL DEFAULT '',
    book_revision INTEGER NOT NULL DEFAULT 0
) STRICT;

CREATE INDEX idx_windows_novel_book_extras_order ON windows_novel_book_extras(position);

CREATE TABLE windows_novel_reading_background (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1) REFERENCES novel_reading_settings(singleton) ON DELETE CASCADE,
    background_image_path TEXT NOT NULL
) STRICT;

-- module: data/windows/42_windows_music.sql
-- Windows-specific permanent music accounts, FM preferences, feedback outbox, network playlists, sync state, policy revisions, desktop lyrics, and user-imported local lyrics.
CREATE TABLE windows_music_accounts (
    platform TEXT PRIMARY KEY NOT NULL,
    generation INTEGER NOT NULL CHECK (generation >= 1),
    cookie TEXT,
    scope TEXT,
    validated_at_ms INTEGER,
    profile_platform TEXT,
    profile_user_id TEXT,
    profile_display_name TEXT,
    profile_avatar TEXT,
    CHECK (
        (cookie IS NULL AND scope IS NULL AND validated_at_ms IS NULL AND
         profile_platform IS NULL AND profile_user_id IS NULL AND profile_display_name IS NULL AND profile_avatar IS NULL)
        OR
        (cookie IS NOT NULL AND length(cookie) > 0 AND scope IS NOT NULL AND validated_at_ms IS NOT NULL AND
         profile_platform IS NOT NULL AND profile_user_id IS NOT NULL AND profile_display_name IS NOT NULL)
    )
) STRICT;

CREATE TABLE windows_music_fm_preferences (
    account_id TEXT PRIMARY KEY NOT NULL,
    mode TEXT NOT NULL,
    submode TEXT,
    rule_id TEXT,
    tab_tag_id TEXT
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
    start_at_ms INTEGER,
    start_inflight INTEGER CHECK (start_inflight IS NULL OR start_inflight IN (0, 1)),
    start_position_sec REAL,
    start_realtime_sec REAL,
    start_reason TEXT,
    start_rate REAL,
    end_event_id TEXT,
    end_at_ms INTEGER,
    end_inflight INTEGER CHECK (end_inflight IS NULL OR end_inflight IN (0, 1)),
    end_position_sec REAL,
    end_realtime_sec REAL,
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
    status_at_ms INTEGER,
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

CREATE TABLE windows_music_policy_revisions (
    scope TEXT NOT NULL REFERENCES music_playback_settings(scope) ON DELETE CASCADE,
    policy_area TEXT NOT NULL CHECK (policy_area IN ('online', 'quality', 'official', 'metadata', 'exclusions')),
    revision INTEGER NOT NULL DEFAULT 0 CHECK (revision BETWEEN 0 AND 9007199254740991),
    PRIMARY KEY (scope, policy_area)
) STRICT;

CREATE TABLE windows_music_desktop_lyrics (
    scope TEXT PRIMARY KEY NOT NULL,
    revision INTEGER NOT NULL DEFAULT 0 CHECK (revision BETWEEN 0 AND 9007199254740991),
    layout TEXT NOT NULL,
    normal_color TEXT NOT NULL,
    highlight_color TEXT NOT NULL,
    geom_monitor TEXT,
    geom_x REAL,
    geom_y REAL,
    geom_width REAL,
    geom_height REAL
) STRICT;

CREATE TABLE windows_music_song_local_files (
    song_id TEXT PRIMARY KEY NOT NULL REFERENCES music_songs(song_id) ON DELETE CASCADE,
    local_file_id TEXT NOT NULL
) STRICT;

CREATE INDEX idx_windows_music_song_local_file_id ON windows_music_song_local_files(local_file_id);
CREATE INDEX idx_windows_music_songs_cover_url ON music_songs(cover_url, song_id) WHERE cover_url <> '';

CREATE TABLE windows_music_play_context_extras (
    context_id TEXT PRIMARY KEY NOT NULL REFERENCES music_play_contexts(context_id) ON DELETE CASCADE,
    playlist_id TEXT,
    source_platform TEXT,
    heartbeat_playlist_id TEXT,
    heartbeat_account_id TEXT
) STRICT;

CREATE TABLE windows_music_queue_source_kind (
    scope TEXT PRIMARY KEY NOT NULL REFERENCES music_queue_state(scope) ON DELETE CASCADE,
    source_kind TEXT NOT NULL
) STRICT;

CREATE TABLE windows_music_history_playlist_source_kind (
    scope TEXT NOT NULL,
    playlist_id TEXT NOT NULL,
    source_kind TEXT NOT NULL,
    PRIMARY KEY (scope, playlist_id),
    FOREIGN KEY (scope, playlist_id) REFERENCES music_history_playlists(scope, playlist_id) ON DELETE CASCADE
) STRICT;

CREATE TABLE windows_music_search_history (
    scope TEXT NOT NULL,
    kind TEXT NOT NULL,
    query_text TEXT NOT NULL,
    position INTEGER NOT NULL DEFAULT 0 CHECK (position >= 0),
    used_at_ms INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (scope, kind, query_text)
) STRICT;

CREATE INDEX idx_windows_music_search_history_order ON windows_music_search_history(scope, position);

CREATE TABLE windows_music_feedback_platforms (
    platform TEXT PRIMARY KEY NOT NULL CHECK (platform IN ('wy', 'tx', 'kg'))
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
