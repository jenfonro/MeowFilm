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
