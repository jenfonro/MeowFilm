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
