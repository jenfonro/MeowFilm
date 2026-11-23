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
