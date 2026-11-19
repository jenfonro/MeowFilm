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
 iosAutoPrepare INTEGER NOT NULL CHECK(iosAutoPrepare IN(0,1))
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
