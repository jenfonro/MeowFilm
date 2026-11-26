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
    group_name TEXT PRIMARY KEY NOT NULL CHECK (group_name IN ('episodeCleanRegexRules', 'episodeRules', 'movieRules', 'aggregateRegexRules', 'seasonRules')),
    row_revision INTEGER NOT NULL DEFAULT 0 CHECK (row_revision BETWEEN 0 AND 9007199254740991),
    overridden INTEGER NOT NULL DEFAULT 0 CHECK (overridden IN (0, 1))
) STRICT;

INSERT INTO film_rule_groups(group_name) VALUES
    ('episodeCleanRegexRules'), ('episodeRules'), ('movieRules'), ('aggregateRegexRules'), ('seasonRules');

CREATE TABLE film_rules (
    group_name TEXT NOT NULL REFERENCES film_rule_groups(group_name) ON DELETE CASCADE,
    position INTEGER NOT NULL CHECK (position >= 0),
    pattern TEXT NOT NULL,
    replacement TEXT,
    flags TEXT,
    PRIMARY KEY (group_name, position),
    CHECK (group_name IN ('episodeRules', 'movieRules', 'seasonRules') OR (replacement IS NULL AND flags IS NULL))
) STRICT;

-- Unified keyword-anchored playback history. Upstream CatVod/Source detailId/lineId preserve exact JS string vs integer identity.
CREATE TABLE film_history (
    target_id TEXT PRIMARY KEY NOT NULL,
    keyword TEXT NOT NULL,
    title TEXT NOT NULL,
    poster TEXT NOT NULL DEFAULT '',
    content_kind TEXT CHECK (content_kind IS NULL OR content_kind IN ('movie', 'tv')),
    year INTEGER,
    tmdb_kind TEXT CHECK (tmdb_kind IS NULL OR tmdb_kind IN ('movie', 'tv')),
    tmdb_id INTEGER CHECK (tmdb_id IS NULL OR tmdb_id > 0),
    douban_kind TEXT CHECK (douban_kind IS NULL OR douban_kind IN ('movie', 'tv')),
    douban_id TEXT,
    source_key TEXT NOT NULL,
    detail_id_kind TEXT NOT NULL CHECK (detail_id_kind IN ('text', 'integer')),
    detail_id_text TEXT NOT NULL,
    line_id_kind TEXT NOT NULL CHECK (line_id_kind IN ('text', 'integer')),
    line_id_text TEXT NOT NULL,
    line_label TEXT NOT NULL DEFAULT '',
    share_provider TEXT,
    share_url TEXT,
    share_password TEXT,
    candidate_id TEXT NOT NULL,
    file_name TEXT NOT NULL DEFAULT '',
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
    row_revision INTEGER NOT NULL CHECK (row_revision BETWEEN 1 AND 9007199254740991),
    CHECK ((tmdb_kind IS NULL) = (tmdb_id IS NULL)),
    CHECK ((douban_kind IS NULL) = (douban_id IS NULL)),
    CHECK ((share_provider IS NULL) = (share_url IS NULL) AND (share_url IS NOT NULL OR share_password IS NULL))
) STRICT;

CREATE INDEX idx_film_history_order ON film_history(updated_at_ms DESC, target_id DESC);

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
    line_id_kind TEXT CHECK (line_id_kind IS NULL OR line_id_kind IN ('text', 'integer')),
    line_id_text TEXT,
    CHECK ((line_id_kind IS NULL) = (line_id_text IS NULL))
) STRICT;

CREATE INDEX idx_film_blocked_order ON film_blocked_sources(position_order);

CREATE TABLE film_preview_targets (
    position_order INTEGER NOT NULL CHECK (position_order >= 0),
    target_id TEXT PRIMARY KEY NOT NULL
) STRICT;

CREATE INDEX idx_film_preview_targets_order ON film_preview_targets(position_order);
