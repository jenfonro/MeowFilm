package db

import (
	"database/sql"
	"errors"
	"regexp"
	"sort"
	"strings"
	"sync"
	"time"
)

func normalizePlayHistoryAggKey(s string) string {
	raw := strings.ToLower(strings.TrimSpace(s))
	if raw == "" {
		return ""
	}
	// Keep only [0-9a-z] and common CJK Unified Ideographs range used by the frontend keying logic.
	// Avoid regexp unicode escapes (Go RE2 doesn't support \uXXXX in patterns).
	b := strings.Builder{}
	b.Grow(len(raw))
	for _, r := range raw {
		if (r >= '0' && r <= '9') || (r >= 'a' && r <= 'z') || (r >= 0x4e00 && r <= 0x9fa5) {
			b.WriteRune(r)
		}
	}
	return strings.TrimSpace(b.String())
}

var (
	reStripSeasonCN = regexp.MustCompile(`(?i)第\s*([0-9０-９]{1,3}|[一二三四五六七八九十百千两零〇]{1,16})\s*季.*$`)
	reStripSeasonEN = regexp.MustCompile(`(?i)\bseason\s*\d{1,3}\b.*$`)
	reStripSeasonS  = regexp.MustCompile(`(?i)\bS\d{1,2}\b.*$`)
)

type aggRegexCache struct {
	mu       sync.RWMutex
	key      string
	compiled []*regexp.Regexp
}

var playHistoryAggRegexCache aggRegexCache

func listMagicAggregateRegexRulesTx(tx *sql.Tx) ([]string, error) {
	if tx == nil {
		return []string{}, nil
	}
	rows, err := tx.Query(`SELECT pos, pattern FROM magic_aggregate_regex_rule ORDER BY pos ASC`)
	if err != nil {
		return []string{}, nil
	}
	defer rows.Close()
	type row struct {
		pos int
		s   string
	}
	tmp := []row{}
	for rows.Next() {
		var p int
		var s string
		_ = rows.Scan(&p, &s)
		s = strings.TrimSpace(s)
		if s == "" {
			continue
		}
		tmp = append(tmp, row{pos: p, s: s})
	}
	sort.Slice(tmp, func(i, j int) bool { return tmp[i].pos < tmp[j].pos })
	out := make([]string, 0, len(tmp))
	for _, it := range tmp {
		out = append(out, it.s)
	}
	return out, nil
}

func compileAggregateRegexps(patterns []string) ([]*regexp.Regexp, string) {
	if len(patterns) == 0 {
		return []*regexp.Regexp{}, ""
	}
	parts := make([]string, 0, len(patterns))
	res := make([]*regexp.Regexp, 0, len(patterns))
	for _, p := range patterns {
		s := strings.TrimSpace(p)
		if s == "" {
			continue
		}
		re, err := regexp.Compile(s)
		if err != nil || re == nil {
			continue
		}
		parts = append(parts, s)
		res = append(res, re)
	}
	key := strings.Join(parts, "\n")
	return res, key
}

func cachedAggregateRegexps(patterns []string) []*regexp.Regexp {
	compiled, key := compileAggregateRegexps(patterns)
	if key == "" || len(compiled) == 0 {
		return []*regexp.Regexp{}
	}
	playHistoryAggRegexCache.mu.RLock()
	if playHistoryAggRegexCache.key == key && playHistoryAggRegexCache.compiled != nil {
		out := playHistoryAggRegexCache.compiled
		playHistoryAggRegexCache.mu.RUnlock()
		return out
	}
	playHistoryAggRegexCache.mu.RUnlock()

	playHistoryAggRegexCache.mu.Lock()
	defer playHistoryAggRegexCache.mu.Unlock()
	if playHistoryAggRegexCache.key == key && playHistoryAggRegexCache.compiled != nil {
		return playHistoryAggRegexCache.compiled
	}
	playHistoryAggRegexCache.key = key
	playHistoryAggRegexCache.compiled = compiled
	return compiled
}

func computePlayHistoryKeywordKeyFromTitle(title string, regexps []*regexp.Regexp) string {
	raw := strings.TrimSpace(title)
	if raw == "" {
		return ""
	}
	out := raw
	for _, re := range regexps {
		if re == nil {
			continue
		}
		out = re.ReplaceAllString(out, "")
	}
	// Strip season markers and any trailing subtitles after them so "xxx 第1季 雪域" collapses to "xxx".
	// This matches the "same keyword -> single history" requirement across TMDB vs site titles.
	out = strings.TrimSpace(out)
	out = reStripSeasonCN.ReplaceAllString(out, "")
	out = reStripSeasonEN.ReplaceAllString(out, "")
	out = reStripSeasonS.ReplaceAllString(out, "")
	return normalizePlayHistoryAggKey(out)
}

func (d *DB) ComputePlayHistoryKeywordKey(title string) string {
	if d == nil || d.db == nil {
		return normalizePlayHistoryAggKey(title)
	}
	patterns, err := d.ListMagicAggregateRegexRules()
	if err != nil {
		patterns = []string{}
	}
	return computePlayHistoryKeywordKeyFromTitle(title, cachedAggregateRegexps(patterns))
}

type PlayHistoryRow struct {
	ContentKey            string
	Title                 string
	SiteKey               string
	SiteName              string
	SpiderAPI             string
	SiteDetail            string
	Poster                string
	Remark                string
	TMDBID                int
	TMDBType              string
	PlayFlag              string
	PreOrder              bool
	SiteEpisodeIndex      int
	SiteEpisodeFile       string
	TMDBSeason            int
	TMDBEpisode           int
	UpdatedAt             int64
	PlaybackPositionTicks int64
	PlaybackRuntimeTicks  int64
	PlaybackItemID        string
}

type PlayHistoryUpsert struct {
	UserID                int64
	ContentKey            string
	SiteKey               string
	SiteDetail            string
	Poster                string
	Remark                string
	TMDBID                int
	TMDBType              string
	PlayFlag              string
	PreOrder              *bool
	SiteEpisodeIndex      int
	SiteEpisodeFile       string
	TMDBSeason            int
	TMDBEpisode           int
	UpdatedAt             int64
	PlaybackPositionTicks int64
	PlaybackRuntimeTicks  int64
	PlaybackItemID        string
}

type TMDBPlayHistoryUpsert struct {
	UserID                int64
	TMDBID                int
	TMDBType              string
	TMDBSeason            int
	TMDBEpisode           int
	ContentKey            string
	Title                 string
	SiteKey               string
	SiteDetail            string
	Poster                string
	Remark                string
	PlayFlag              string
	PreOrder              *bool
	SiteEpisodeIndex      int
	SiteEpisodeFile       string
	PlaybackPositionTicks int64
	PlaybackRuntimeTicks  int64
	PlaybackItemID        string
	UpdatedAt             int64
}

type PlayHistorySnapshot struct {
	Pos     int64
	Runtime int64
	Updated int64
}

func (d *DB) ensureContent(tx *sql.Tx, contentKey string, tmdbID int, tmdbType string, updatedAt int64) (contentID int64, err error) {
	if tx == nil {
		return 0, errors.New("tx nil")
	}
	key := strings.TrimSpace(contentKey)
	if key == "" {
		return 0, errors.New("content key empty")
	}
	now := updatedAt
	if now <= 0 {
		now = time.Now().Unix()
	}
	_, _ = tx.Exec(`
		INSERT INTO content(content_key, created_at, updated_at)
		VALUES(?,?,?)
		ON CONFLICT(content_key) DO UPDATE SET updated_at = excluded.updated_at
	`, key, now, now)

	if err := tx.QueryRow(`SELECT id FROM content WHERE content_key = ? LIMIT 1`, key).Scan(&contentID); err != nil {
		return 0, err
	}
	typ := strings.TrimSpace(tmdbType)
	if tmdbID > 0 && (typ == "tv" || typ == "movie") {
		_, _ = tx.Exec(`
			INSERT INTO content_tmdb(content_id, tmdb_id, tmdb_type, updated_at)
			VALUES(?,?,?,?)
			ON CONFLICT(content_id) DO UPDATE SET
			  tmdb_id=excluded.tmdb_id,
			  tmdb_type=excluded.tmdb_type,
			  updated_at=excluded.updated_at
		`, contentID, tmdbID, typ, now)
	}
	return contentID, nil
}

func (d *DB) GetPlayHistoryLatestPosterByContentKey(userID int64, contentKey string) (string, error) {
	if d == nil || d.db == nil || userID <= 0 {
		return "", nil
	}
	key := strings.TrimSpace(contentKey)
	if key == "" {
		return "", nil
	}
	var poster sql.NullString
	_ = d.db.QueryRow(`
		SELECT sv.poster
		FROM user_play_history h
		JOIN content c ON c.id = h.content_id
		JOIN site_video sv ON sv.id = h.site_video_id
		WHERE h.user_id = ? AND c.content_key = ? AND sv.poster <> ''
		ORDER BY h.updated_at DESC
		LIMIT 1
	`, userID, key).Scan(&poster)
	return strings.TrimSpace(poster.String), nil
}

func (d *DB) UpsertPlayHistory(row PlayHistoryUpsert) error {
	if d == nil || d.db == nil {
		return errors.New("db nil")
	}
	if row.UserID <= 0 {
		return errors.New("invalid user id")
	}
	contentKey := strings.TrimSpace(row.ContentKey)
	siteKey := strings.TrimSpace(row.SiteKey)
	siteDetail := strings.TrimSpace(row.SiteDetail)
	if contentKey == "" || siteKey == "" || siteDetail == "" {
		return errors.New("invalid args")
	}
	now := row.UpdatedAt
	if now <= 0 {
		now = time.Now().Unix()
	}

	siteKind, ownerID := d.resolveSiteKindAndOwner(row.UserID, siteKey)

	tx, err := d.db.Begin()
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback() }()

	contentID, err := d.ensureContent(tx, contentKey, row.TMDBID, row.TMDBType, now)
	if err != nil {
		return err
	}
	siteVideoID, err := d.upsertSiteVideo(tx, siteKind, ownerID, siteKey, siteDetail, contentKey, row.Poster, row.Remark, now)
	if err != nil {
		return err
	}

	// Enforce: keep only one canonical play-history row per (user, contentKey) in the DB.
	_, _ = tx.Exec(`DELETE FROM user_play_history WHERE user_id=? AND content_id=? AND site_video_id <> ?`, row.UserID, contentID, siteVideoID)

	preOrder := 0
	preOrderPatch := -1
	if row.PreOrder != nil {
		if *row.PreOrder {
			preOrder = 1
		}
		preOrderPatch = preOrder
	}

	_, err = tx.Exec(`
			INSERT INTO user_play_history(
			  user_id, content_id, site_video_id,
			  play_flag, pre_order, site_episode_index, site_episode_file,
			  tmdb_season, tmdb_episode,
			  playback_position_ticks, playback_runtime_ticks, playback_item_id,
			  updated_at
			)
			VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?)
			ON CONFLICT(user_id, site_video_id) DO UPDATE SET
			  content_id = excluded.content_id,
			  play_flag = excluded.play_flag,
			  pre_order = CASE WHEN ? >= 0 THEN excluded.pre_order ELSE user_play_history.pre_order END,
			  site_episode_index = excluded.site_episode_index,
			  site_episode_file = excluded.site_episode_file,
			  tmdb_season = CASE WHEN excluded.tmdb_season > 0 THEN excluded.tmdb_season ELSE user_play_history.tmdb_season END,
			  tmdb_episode = CASE WHEN excluded.tmdb_episode > 0 THEN excluded.tmdb_episode ELSE user_play_history.tmdb_episode END,
			  playback_position_ticks = CASE WHEN excluded.playback_position_ticks > 0 THEN excluded.playback_position_ticks ELSE user_play_history.playback_position_ticks END,
			  playback_runtime_ticks = CASE WHEN excluded.playback_runtime_ticks > 0 THEN excluded.playback_runtime_ticks ELSE user_play_history.playback_runtime_ticks END,
			  playback_item_id = CASE WHEN excluded.playback_item_id <> '' THEN excluded.playback_item_id ELSE user_play_history.playback_item_id END,
			  updated_at = excluded.updated_at
		`,
		row.UserID,
		contentID,
		siteVideoID,
		strings.TrimSpace(row.PlayFlag),
		preOrder,
		row.SiteEpisodeIndex,
		strings.TrimSpace(row.SiteEpisodeFile),
		row.TMDBSeason,
		row.TMDBEpisode,
		row.PlaybackPositionTicks,
		row.PlaybackRuntimeTicks,
		strings.TrimSpace(row.PlaybackItemID),
		now,
		preOrderPatch,
	)
	if err != nil {
		return err
	}
	return tx.Commit()
}

func (d *DB) UpsertTMDBPlayHistory(row TMDBPlayHistoryUpsert) error {
	if d == nil || d.db == nil {
		return errors.New("db nil")
	}
	if row.UserID <= 0 || row.TMDBID <= 0 {
		return errors.New("invalid args")
	}
	typ := strings.TrimSpace(strings.ToLower(row.TMDBType))
	if typ != "tv" && typ != "movie" {
		return errors.New("invalid tmdb type")
	}
	now := row.UpdatedAt
	if now <= 0 {
		now = time.Now().Unix()
	}
	contentKey := strings.TrimSpace(row.ContentKey)
	if contentKey == "" {
		titleKey := strings.TrimSpace(d.ComputePlayHistoryKeywordKey(strings.TrimSpace(row.Title)))
		if titleKey != "" {
			contentKey = titleKey
		}
	}
	if contentKey == "" {
		return errors.New("empty content key")
	}
	title := strings.TrimSpace(row.Title)
	if title == "" {
		return errors.New("empty title")
	}
	siteKey := strings.TrimSpace(row.SiteKey)
	if siteKey == "" {
		return errors.New("empty site key")
	}
	siteDetail := strings.TrimSpace(row.SiteDetail)
	if siteDetail == "" {
		return errors.New("empty site detail")
	}

	siteKind, ownerID := d.resolveSiteKindAndOwner(row.UserID, siteKey)
	tx, err := d.db.Begin()
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback() }()

	contentID, err := d.ensureContent(tx, contentKey, row.TMDBID, typ, now)
	if err != nil {
		return err
	}
	siteVideoID, err := d.upsertSiteVideo(tx, siteKind, ownerID, siteKey, siteDetail, title, row.Poster, row.Remark, now)
	if err != nil {
		return err
	}

	preOrderValue, err := d.resolveTMDBPreOrderForWriteTx(tx, row.UserID, typ, row.TMDBID, row.PreOrder)
	if err != nil {
		return err
	}

	_, _ = tx.Exec(`
		DELETE FROM user_play_history
		WHERE user_id = ?
		  AND content_id IN (
		    SELECT content_id
		    FROM content_tmdb
		    WHERE tmdb_type = ? AND tmdb_id = ?
		  )
		  AND site_video_id <> ?
	`, row.UserID, typ, row.TMDBID, siteVideoID)

	preOrder := boolToInt(preOrderValue)

	_, err = tx.Exec(`
		INSERT INTO user_play_history(
		  user_id, content_id, site_video_id,
		  play_flag, pre_order, site_episode_index, site_episode_file,
		  tmdb_season, tmdb_episode,
		  playback_position_ticks, playback_runtime_ticks, playback_item_id,
		  updated_at
		)
		VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?)
		ON CONFLICT(user_id, site_video_id) DO UPDATE SET
		  content_id = excluded.content_id,
		  play_flag = CASE WHEN excluded.play_flag <> '' THEN excluded.play_flag ELSE user_play_history.play_flag END,
		  pre_order = excluded.pre_order,
		  site_episode_index = CASE WHEN excluded.site_episode_index > 0 THEN excluded.site_episode_index ELSE user_play_history.site_episode_index END,
		  site_episode_file = CASE WHEN excluded.site_episode_file <> '' THEN excluded.site_episode_file ELSE user_play_history.site_episode_file END,
		  tmdb_season = CASE WHEN excluded.tmdb_season > 0 THEN excluded.tmdb_season ELSE user_play_history.tmdb_season END,
		  tmdb_episode = CASE WHEN excluded.tmdb_episode > 0 THEN excluded.tmdb_episode ELSE user_play_history.tmdb_episode END,
		  playback_position_ticks = CASE WHEN excluded.playback_position_ticks > 0 THEN excluded.playback_position_ticks ELSE user_play_history.playback_position_ticks END,
		  playback_runtime_ticks = CASE WHEN excluded.playback_runtime_ticks > 0 THEN excluded.playback_runtime_ticks ELSE user_play_history.playback_runtime_ticks END,
		  playback_item_id = CASE WHEN excluded.playback_item_id <> '' THEN excluded.playback_item_id ELSE user_play_history.playback_item_id END,
		  updated_at = excluded.updated_at
	`,
		row.UserID,
		contentID,
		siteVideoID,
		strings.TrimSpace(row.PlayFlag),
		preOrder,
		row.SiteEpisodeIndex,
		strings.TrimSpace(row.SiteEpisodeFile),
		row.TMDBSeason,
		row.TMDBEpisode,
		row.PlaybackPositionTicks,
		row.PlaybackRuntimeTicks,
		strings.TrimSpace(row.PlaybackItemID),
		now,
	)
	if err != nil {
		return err
	}
	return tx.Commit()
}

func (d *DB) UpdateTMDBPlayHistoryPreOrder(userID int64, tmdbType string, tmdbID int, preOrder bool, updatedAt int64) error {
	if d == nil || d.db == nil {
		return errors.New("db nil")
	}
	if userID <= 0 || tmdbID <= 0 {
		return errors.New("invalid args")
	}
	typ := strings.TrimSpace(strings.ToLower(tmdbType))
	if typ != "tv" && typ != "movie" {
		return errors.New("invalid tmdb type")
	}
	now := updatedAt
	if now <= 0 {
		now = time.Now().Unix()
	}
	_, err := d.db.Exec(`
		UPDATE user_play_history
		SET pre_order = ?, updated_at = ?
		WHERE user_id = ?
		  AND content_id IN (
		    SELECT content_id
		    FROM content_tmdb
		    WHERE tmdb_type = ? AND tmdb_id = ?
		  )
	`, boolToInt(preOrder), now, userID, typ, tmdbID)
	return err
}

func (d *DB) EnsureTMDBPlayHistoryMeta(row TMDBPlayHistoryUpsert) error {
	if d == nil || d.db == nil {
		return errors.New("db nil")
	}
	if row.UserID <= 0 || row.TMDBID <= 0 {
		return errors.New("invalid args")
	}
	typ := strings.TrimSpace(strings.ToLower(row.TMDBType))
	if typ != "tv" && typ != "movie" {
		return errors.New("invalid tmdb type")
	}
	contentKey := strings.TrimSpace(row.ContentKey)
	if contentKey == "" {
		return errors.New("empty content key")
	}
	title := strings.TrimSpace(row.Title)
	if title == "" {
		title = contentKey
	}
	now := row.UpdatedAt
	if now <= 0 {
		now = time.Now().Unix()
	}
	tx, err := d.db.Begin()
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback() }()

	hasHistory, err := d.hasTMDBPlayHistoryTx(tx, row.UserID, typ, row.TMDBID)
	if err != nil {
		return err
	}
	if hasHistory {
		if row.PreOrder != nil {
			if err := d.updateTMDBPlayHistoryPreOrderTx(tx, row.UserID, typ, row.TMDBID, *row.PreOrder, now); err != nil {
				return err
			}
		}
		return tx.Commit()
	}

	contentID, err := d.ensureContent(tx, contentKey, row.TMDBID, typ, now)
	if err != nil {
		return err
	}
	siteVideoID, err := d.upsertSiteVideo(tx, "emby", 0, "emby", contentKey, title, row.Poster, row.Remark, now)
	if err != nil {
		return err
	}
	preOrder := 0
	preOrderPatch := -1
	if row.PreOrder != nil {
		if *row.PreOrder {
			preOrder = 1
		}
		preOrderPatch = preOrder
	}
	_, err = tx.Exec(`
		INSERT INTO user_play_history(
		  user_id, content_id, site_video_id,
		  play_flag, pre_order, site_episode_index, site_episode_file,
		  tmdb_season, tmdb_episode,
		  playback_position_ticks, playback_runtime_ticks, playback_item_id,
		  updated_at
		)
		VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?)
		ON CONFLICT(user_id, site_video_id) DO UPDATE SET
		  content_id = excluded.content_id,
		  pre_order = CASE WHEN ? >= 0 THEN excluded.pre_order ELSE user_play_history.pre_order END,
		  updated_at = excluded.updated_at
	`,
		row.UserID,
		contentID,
		siteVideoID,
		"",
		preOrder,
		0,
		"",
		0,
		0,
		0,
		0,
		"",
		now,
		preOrderPatch,
	)
	if err != nil {
		return err
	}
	return tx.Commit()
}

func (d *DB) UpsertTMDBPlayHistoryMeta(row TMDBPlayHistoryUpsert) error {
	return d.EnsureTMDBPlayHistoryMeta(row)
}

func (d *DB) hasTMDBPlayHistoryTx(tx *sql.Tx, userID int64, tmdbType string, tmdbID int) (bool, error) {
	if tx == nil {
		return false, errors.New("tx nil")
	}
	var count int
	err := tx.QueryRow(`
		SELECT COUNT(1)
		FROM user_play_history
		WHERE user_id = ?
		  AND content_id IN (
		    SELECT content_id
		    FROM content_tmdb
		    WHERE tmdb_type = ? AND tmdb_id = ?
		  )
	`, userID, strings.TrimSpace(tmdbType), tmdbID).Scan(&count)
	return count > 0, err
}

func (d *DB) latestTMDBPreOrderTx(tx *sql.Tx, userID int64, tmdbType string, tmdbID int) (bool, bool, error) {
	if tx == nil {
		return false, false, errors.New("tx nil")
	}
	var value sql.NullInt64
	err := tx.QueryRow(`
		SELECT h.pre_order
		FROM user_play_history h
		WHERE h.user_id = ?
		  AND h.content_id IN (
		    SELECT content_id
		    FROM content_tmdb
		    WHERE tmdb_type = ? AND tmdb_id = ?
		  )
		ORDER BY h.updated_at DESC, h.id DESC
		LIMIT 1
	`, userID, strings.TrimSpace(tmdbType), tmdbID).Scan(&value)
	if err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return false, false, nil
		}
		return false, false, err
	}
	return value.Int64 != 0, true, nil
}

func (d *DB) resolveTMDBPreOrderForWriteTx(tx *sql.Tx, userID int64, tmdbType string, tmdbID int, incoming *bool) (bool, error) {
	if incoming != nil {
		return *incoming, nil
	}
	if inherited, ok, err := d.latestTMDBPreOrderTx(tx, userID, tmdbType, tmdbID); err != nil {
		return false, err
	} else if ok {
		return inherited, nil
	}
	return false, nil
}

func (d *DB) updateTMDBPlayHistoryPreOrderTx(tx *sql.Tx, userID int64, tmdbType string, tmdbID int, preOrder bool, updatedAt int64) error {
	if tx == nil {
		return errors.New("tx nil")
	}
	now := updatedAt
	if now <= 0 {
		now = time.Now().Unix()
	}
	_, err := tx.Exec(`
		UPDATE user_play_history
		SET pre_order = ?, updated_at = ?
		WHERE user_id = ?
		  AND content_id IN (
		    SELECT content_id
		    FROM content_tmdb
		    WHERE tmdb_type = ? AND tmdb_id = ?
		  )
	`, boolToInt(preOrder), now, userID, strings.TrimSpace(tmdbType), tmdbID)
	return err
}

func (d *DB) UpdateTMDBPlayHistoryProgress(userID int64, tmdbType string, tmdbID int, playbackItemID string, positionTicks int64, runtimeTicks int64, updatedAt int64) error {
	if d == nil || d.db == nil {
		return errors.New("db nil")
	}
	if userID <= 0 || tmdbID <= 0 {
		return errors.New("invalid args")
	}
	typ := strings.TrimSpace(strings.ToLower(tmdbType))
	if typ != "tv" && typ != "movie" {
		return errors.New("invalid tmdb type")
	}
	now := updatedAt
	if now <= 0 {
		now = time.Now().Unix()
	}
	_, err := d.db.Exec(`
		UPDATE user_play_history
		SET playback_item_id = CASE WHEN ? <> '' THEN ? ELSE playback_item_id END,
		    playback_position_ticks = CASE WHEN ? > 0 THEN ? ELSE playback_position_ticks END,
		    playback_runtime_ticks = CASE WHEN ? > 0 THEN ? ELSE playback_runtime_ticks END,
		    updated_at = ?
		WHERE user_id = ?
		  AND content_id IN (
		    SELECT content_id
		    FROM content_tmdb
		    WHERE tmdb_type = ? AND tmdb_id = ?
		  )
	`,
		strings.TrimSpace(playbackItemID), strings.TrimSpace(playbackItemID),
		positionTicks, positionTicks,
		runtimeTicks, runtimeTicks,
		now,
		userID,
		typ,
		tmdbID,
	)
	return err
}

func (d *DB) ListPlayHistory(userID int64, limit int) ([]PlayHistoryRow, error) {
	if d == nil || d.db == nil || userID <= 0 {
		return []PlayHistoryRow{}, nil
	}
	if limit <= 0 {
		limit = 20
	}
	if limit > 2000 {
		limit = 2000
	}
	rows, err := d.db.Query(`
			SELECT
			  c.content_key,
			  sv.site_key,
			  COALESCE(gs.name, CASE WHEN sv.site_kind='emby' THEN 'Emby' ELSE '' END) AS site_name,
			  COALESCE(gs.api, CASE WHEN sv.site_kind='emby' THEN 'emby' ELSE '' END) AS spider_api,
			  sv.site_detail,
			  sv.poster,
			  sv.remark,
			  COALESCE(tm.tmdb_id, 0) AS tmdb_id,
			  COALESCE(tm.tmdb_type, '') AS tmdb_type,
			  h.play_flag,
			  h.pre_order,
			  h.site_episode_index,
			  h.site_episode_file,
			  h.tmdb_season,
			  h.tmdb_episode,
			  h.updated_at,
			  h.playback_position_ticks,
			  h.playback_runtime_ticks,
			  h.playback_item_id
			FROM user_play_history h
			JOIN content c ON c.id = h.content_id
			JOIN site_video sv ON sv.id = h.site_video_id
		LEFT JOIN content_tmdb tm ON tm.content_id = c.id
		LEFT JOIN video_source_site gs ON sv.site_kind='global' AND gs.key = sv.site_key
		WHERE h.user_id = ?
		ORDER BY h.updated_at DESC
		LIMIT ?
	`, userID, limit)
	if err != nil {
		return []PlayHistoryRow{}, err
	}
	defer rows.Close()
	out := []PlayHistoryRow{}
	for rows.Next() {
		var r PlayHistoryRow
		_ = rows.Scan(
			&r.ContentKey,
			&r.SiteKey,
			&r.SiteName,
			&r.SpiderAPI,
			&r.SiteDetail,
			&r.Poster,
			&r.Remark,
			&r.TMDBID,
			&r.TMDBType,
			&r.PlayFlag,
			&r.PreOrder,
			&r.SiteEpisodeIndex,
			&r.SiteEpisodeFile,
			&r.TMDBSeason,
			&r.TMDBEpisode,
			&r.UpdatedAt,
			&r.PlaybackPositionTicks,
			&r.PlaybackRuntimeTicks,
			&r.PlaybackItemID,
		)
		out = append(out, r)
	}
	return out, nil
}

func (d *DB) GetPlayHistoryLatestBySiteVideo(userID int64, siteKey string, siteDetail string) (*PlayHistoryRow, error) {
	if d == nil || d.db == nil || userID <= 0 {
		return nil, nil
	}
	sk := strings.TrimSpace(siteKey)
	vid := strings.TrimSpace(siteDetail)
	if sk == "" || vid == "" {
		return nil, nil
	}
	siteKind, ownerID := d.resolveSiteKindAndOwner(userID, sk)

	var r PlayHistoryRow
	err := d.db.QueryRow(`
			SELECT
			  c.content_key,
			  sv.title,
			  sv.site_key,
			  COALESCE(gs.name, CASE WHEN sv.site_kind='emby' THEN 'Emby' ELSE '' END) AS site_name,
			  COALESCE(gs.api, CASE WHEN sv.site_kind='emby' THEN 'emby' ELSE '' END) AS spider_api,
			  sv.site_detail,
			  sv.poster,
			  sv.remark,
			  COALESCE(tm.tmdb_id, 0) AS tmdb_id,
			  COALESCE(tm.tmdb_type, '') AS tmdb_type,
			  h.play_flag,
			  h.pre_order,
			  h.site_episode_index,
			  h.site_episode_file,
			  h.tmdb_season,
			  h.tmdb_episode,
			  h.updated_at,
			  h.playback_position_ticks,
			  h.playback_runtime_ticks,
			  h.playback_item_id
			FROM user_play_history h
			JOIN content c ON c.id = h.content_id
		JOIN site_video sv ON sv.id = h.site_video_id
		LEFT JOIN content_tmdb tm ON tm.content_id = c.id
		LEFT JOIN video_source_site gs ON sv.site_kind='global' AND gs.key = sv.site_key
		WHERE h.user_id = ?
		  AND sv.site_kind = ?
		  AND sv.owner_user_id = ?
		  AND sv.site_key = ?
		  AND sv.site_detail = ?
		ORDER BY h.updated_at DESC
		LIMIT 1
	`, userID, siteKind, ownerID, sk, vid).Scan(
		&r.ContentKey,
		&r.SiteKey,
		&r.SiteName,
		&r.SpiderAPI,
		&r.SiteDetail,
		&r.Poster,
		&r.Remark,
		&r.TMDBID,
		&r.TMDBType,
		&r.PlayFlag,
		&r.PreOrder,
		&r.SiteEpisodeIndex,
		&r.SiteEpisodeFile,
		&r.TMDBSeason,
		&r.TMDBEpisode,
		&r.UpdatedAt,
		&r.PlaybackPositionTicks,
		&r.PlaybackRuntimeTicks,
		&r.PlaybackItemID,
	)
	if err != nil {
		if err == sql.ErrNoRows {
			return nil, nil
		}
		return nil, err
	}
	return &r, nil
}

func (d *DB) GetPlayHistoryLatestByContentKey(userID int64, contentKey string) (*PlayHistoryRow, error) {
	if d == nil || d.db == nil || userID <= 0 {
		return nil, nil
	}
	key := strings.TrimSpace(contentKey)
	if key == "" {
		return nil, nil
	}
	var r PlayHistoryRow
	err := d.db.QueryRow(`
			SELECT
			  c.content_key,
			  sv.site_key,
			  COALESCE(gs.name, CASE WHEN sv.site_kind='emby' THEN 'Emby' ELSE '' END) AS site_name,
			  COALESCE(gs.api, CASE WHEN sv.site_kind='emby' THEN 'emby' ELSE '' END) AS spider_api,
			  sv.site_detail,
			  sv.poster,
			  sv.remark,
			  COALESCE(tm.tmdb_id, 0) AS tmdb_id,
			  COALESCE(tm.tmdb_type, '') AS tmdb_type,
			  h.play_flag,
			  h.pre_order,
			  h.site_episode_index,
			  h.site_episode_file,
			  h.tmdb_season,
			  h.tmdb_episode,
			  h.updated_at,
			  h.playback_position_ticks,
			  h.playback_runtime_ticks,
			  h.playback_item_id
			FROM user_play_history h
			JOIN content c ON c.id = h.content_id
		JOIN site_video sv ON sv.id = h.site_video_id
		LEFT JOIN content_tmdb tm ON tm.content_id = c.id
		LEFT JOIN video_source_site gs ON sv.site_kind='global' AND gs.key = sv.site_key
		WHERE h.user_id = ? AND c.content_key = ?
		ORDER BY h.updated_at DESC
		LIMIT 1
	`, userID, key).Scan(
		&r.ContentKey,
		&r.SiteKey,
		&r.SiteName,
		&r.SpiderAPI,
		&r.SiteDetail,
		&r.Poster,
		&r.Remark,
		&r.TMDBID,
		&r.TMDBType,
		&r.PlayFlag,
		&r.PreOrder,
		&r.SiteEpisodeIndex,
		&r.SiteEpisodeFile,
		&r.TMDBSeason,
		&r.TMDBEpisode,
		&r.UpdatedAt,
		&r.PlaybackPositionTicks,
		&r.PlaybackRuntimeTicks,
		&r.PlaybackItemID,
	)
	if err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return nil, nil
		}
		return nil, err
	}
	r.ContentKey = strings.TrimSpace(r.ContentKey)
	r.SiteKey = strings.TrimSpace(r.SiteKey)
	r.SiteName = strings.TrimSpace(r.SiteName)
	r.SpiderAPI = strings.TrimSpace(r.SpiderAPI)
	r.SiteDetail = strings.TrimSpace(r.SiteDetail)
	r.Poster = strings.TrimSpace(r.Poster)
	r.Remark = strings.TrimSpace(r.Remark)
	r.TMDBType = strings.TrimSpace(r.TMDBType)
	r.PlayFlag = strings.TrimSpace(r.PlayFlag)
	r.SiteEpisodeFile = strings.TrimSpace(r.SiteEpisodeFile)
	r.PlaybackItemID = strings.TrimSpace(r.PlaybackItemID)
	return &r, nil
}

func (d *DB) GetPlayHistoryLatestByTMDB(userID int64, tmdbType string, tmdbID int) (*PlayHistoryRow, error) {
	if d == nil || d.db == nil || userID <= 0 || tmdbID <= 0 {
		return nil, nil
	}
	typ := strings.TrimSpace(strings.ToLower(tmdbType))
	if typ != "tv" && typ != "movie" {
		return nil, nil
	}
	var r PlayHistoryRow
	err := d.db.QueryRow(`
			SELECT
			  c.content_key,
			  sv.title,
			  sv.site_key,
			  COALESCE(gs.name, CASE WHEN sv.site_kind='emby' THEN 'Emby' ELSE '' END) AS site_name,
			  COALESCE(gs.api, CASE WHEN sv.site_kind='emby' THEN 'emby' ELSE '' END) AS spider_api,
			  sv.site_detail,
			  sv.poster,
			  sv.remark,
			  COALESCE(tm.tmdb_id, 0) AS tmdb_id,
			  COALESCE(tm.tmdb_type, '') AS tmdb_type,
			  h.play_flag,
			  h.pre_order,
			  h.site_episode_index,
			  h.site_episode_file,
			  h.tmdb_season,
			  h.tmdb_episode,
			  h.updated_at,
			  h.playback_position_ticks,
			  h.playback_runtime_ticks,
			  h.playback_item_id
			FROM user_play_history h
			JOIN content c ON c.id = h.content_id
			JOIN site_video sv ON sv.id = h.site_video_id
			JOIN content_tmdb tm ON tm.content_id = c.id
			LEFT JOIN video_source_site gs ON sv.site_kind='global' AND gs.key = sv.site_key
			WHERE h.user_id = ? AND tm.tmdb_type = ? AND tm.tmdb_id = ?
			ORDER BY h.updated_at DESC
			LIMIT 1
	`, userID, typ, tmdbID).Scan(
		&r.ContentKey,
		&r.Title,
		&r.SiteKey,
		&r.SiteName,
		&r.SpiderAPI,
		&r.SiteDetail,
		&r.Poster,
		&r.Remark,
		&r.TMDBID,
		&r.TMDBType,
		&r.PlayFlag,
		&r.PreOrder,
		&r.SiteEpisodeIndex,
		&r.SiteEpisodeFile,
		&r.TMDBSeason,
		&r.TMDBEpisode,
		&r.UpdatedAt,
		&r.PlaybackPositionTicks,
		&r.PlaybackRuntimeTicks,
		&r.PlaybackItemID,
	)
	if err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return nil, nil
		}
		return nil, err
	}
	r.ContentKey = strings.TrimSpace(r.ContentKey)
	r.Title = strings.TrimSpace(r.Title)
	r.SiteKey = strings.TrimSpace(r.SiteKey)
	r.SiteName = strings.TrimSpace(r.SiteName)
	r.SpiderAPI = strings.TrimSpace(r.SpiderAPI)
	r.SiteDetail = strings.TrimSpace(r.SiteDetail)
	r.Poster = strings.TrimSpace(r.Poster)
	r.Remark = strings.TrimSpace(r.Remark)
	r.TMDBType = strings.TrimSpace(r.TMDBType)
	r.PlayFlag = strings.TrimSpace(r.PlayFlag)
	r.SiteEpisodeFile = strings.TrimSpace(r.SiteEpisodeFile)
	r.PlaybackItemID = strings.TrimSpace(r.PlaybackItemID)
	return &r, nil
}

func (d *DB) GetPlayHistoryLatestByPlaybackItemID(userID int64, playbackItemID string) (*PlayHistoryRow, error) {
	if d == nil || d.db == nil || userID <= 0 {
		return nil, nil
	}
	itemID := strings.TrimSpace(playbackItemID)
	if itemID == "" {
		return nil, nil
	}
	var r PlayHistoryRow
	err := d.db.QueryRow(`
			SELECT
			  c.content_key,
			  sv.site_key,
			  COALESCE(gs.name, CASE WHEN sv.site_kind='emby' THEN 'Emby' ELSE '' END) AS site_name,
			  COALESCE(gs.api, CASE WHEN sv.site_kind='emby' THEN 'emby' ELSE '' END) AS spider_api,
			  sv.site_detail,
			  sv.poster,
			  sv.remark,
			  COALESCE(tm.tmdb_id, 0) AS tmdb_id,
			  COALESCE(tm.tmdb_type, '') AS tmdb_type,
			  h.play_flag,
			  h.pre_order,
			  h.site_episode_index,
			  h.site_episode_file,
			  h.tmdb_season,
			  h.tmdb_episode,
			  h.updated_at,
			  h.playback_position_ticks,
			  h.playback_runtime_ticks,
			  h.playback_item_id
			FROM user_play_history h
			JOIN content c ON c.id = h.content_id
			JOIN site_video sv ON sv.id = h.site_video_id
			LEFT JOIN content_tmdb tm ON tm.content_id = c.id
			LEFT JOIN video_source_site gs ON sv.site_kind='global' AND gs.key = sv.site_key
			WHERE h.user_id = ? AND h.playback_item_id = ?
			ORDER BY h.updated_at DESC
			LIMIT 1
	`, userID, itemID).Scan(
		&r.ContentKey,
		&r.SiteKey,
		&r.SiteName,
		&r.SpiderAPI,
		&r.SiteDetail,
		&r.Poster,
		&r.Remark,
		&r.TMDBID,
		&r.TMDBType,
		&r.PlayFlag,
		&r.PreOrder,
		&r.SiteEpisodeIndex,
		&r.SiteEpisodeFile,
		&r.TMDBSeason,
		&r.TMDBEpisode,
		&r.UpdatedAt,
		&r.PlaybackPositionTicks,
		&r.PlaybackRuntimeTicks,
		&r.PlaybackItemID,
	)
	if err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return nil, nil
		}
		return nil, err
	}
	r.ContentKey = strings.TrimSpace(r.ContentKey)
	r.SiteKey = strings.TrimSpace(r.SiteKey)
	r.SiteName = strings.TrimSpace(r.SiteName)
	r.SpiderAPI = strings.TrimSpace(r.SpiderAPI)
	r.SiteDetail = strings.TrimSpace(r.SiteDetail)
	r.Poster = strings.TrimSpace(r.Poster)
	r.Remark = strings.TrimSpace(r.Remark)
	r.TMDBType = strings.TrimSpace(r.TMDBType)
	r.PlayFlag = strings.TrimSpace(r.PlayFlag)
	r.SiteEpisodeFile = strings.TrimSpace(r.SiteEpisodeFile)
	r.PlaybackItemID = strings.TrimSpace(r.PlaybackItemID)
	return &r, nil
}

func (d *DB) DeletePlayHistoryByContentKey(userID int64, contentKey string) (int64, error) {
	if d == nil || d.db == nil || userID <= 0 {
		return 0, nil
	}
	key := strings.TrimSpace(contentKey)
	if key == "" {
		return 0, nil
	}
	var cid int64
	if err := d.db.QueryRow(`SELECT id FROM content WHERE content_key=? LIMIT 1`, key).Scan(&cid); err != nil {
		return 0, nil
	}
	res, err := d.db.Exec(`DELETE FROM user_play_history WHERE user_id=? AND content_id=?`, userID, cid)
	if err != nil || res == nil {
		return 0, err
	}
	n, _ := res.RowsAffected()
	return n, nil
}

func (d *DB) GetPlayHistoryLatestPosterByTMDB(tmdbType string, tmdbID int) (string, error) {
	if d == nil || d.db == nil || tmdbID <= 0 {
		return "", nil
	}
	typ := strings.ToLower(strings.TrimSpace(tmdbType))
	if typ != "movie" && typ != "tv" {
		return "", nil
	}
	var poster sql.NullString
	err := d.db.QueryRow(`
		SELECT sv.poster
		FROM user_play_history h
		JOIN content c ON c.id = h.content_id
		JOIN site_video sv ON sv.id = h.site_video_id
		JOIN content_tmdb tm ON tm.content_id = c.id
		WHERE tm.tmdb_type = ? AND tm.tmdb_id = ? AND sv.poster <> ''
		ORDER BY h.updated_at DESC
		LIMIT 1
	`, typ, tmdbID).Scan(&poster)
	if err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return "", nil
		}
		return "", err
	}
	return strings.TrimSpace(poster.String), nil
}

func (d *DB) DeletePlayHistoryBySiteVideo(userID int64, siteKey string, siteDetail string) (int64, error) {
	if d == nil || d.db == nil || userID <= 0 {
		return 0, nil
	}
	sk := strings.TrimSpace(siteKey)
	vid := strings.TrimSpace(siteDetail)
	if sk == "" || vid == "" {
		return 0, nil
	}
	siteKind, ownerID := d.resolveSiteKindAndOwner(userID, sk)
	var siteVideoID int64
	if err := d.db.QueryRow(`
		SELECT id FROM site_video WHERE site_kind=? AND owner_user_id=? AND site_key=? AND site_detail=? LIMIT 1
	`, siteKind, ownerID, sk, vid).Scan(&siteVideoID); err != nil {
		return 0, nil
	}
	res, err := d.db.Exec(`DELETE FROM user_play_history WHERE user_id=? AND site_video_id=?`, userID, siteVideoID)
	if err != nil || res == nil {
		return 0, err
	}
	n, _ := res.RowsAffected()
	return n, nil
}

func (d *DB) GetPlayHistorySnapshotBySiteVideo(userID int64, siteKey string, siteDetail string) (PlayHistorySnapshot, bool) {
	if d == nil || d.db == nil || userID <= 0 {
		return PlayHistorySnapshot{}, false
	}
	sk := strings.TrimSpace(siteKey)
	vid := strings.TrimSpace(siteDetail)
	if sk == "" || vid == "" {
		return PlayHistorySnapshot{}, false
	}
	siteKind, ownerID := d.resolveSiteKindAndOwner(userID, sk)
	var snap PlayHistorySnapshot
	err := d.db.QueryRow(`
		SELECT h.playback_position_ticks, h.playback_runtime_ticks, h.updated_at
		FROM user_play_history h
		JOIN site_video sv ON sv.id = h.site_video_id
		WHERE h.user_id = ?
		  AND sv.site_kind = ?
		  AND sv.owner_user_id = ?
		  AND sv.site_key = ?
		  AND sv.site_detail = ?
		ORDER BY h.updated_at DESC
		LIMIT 1
	`, userID, siteKind, ownerID, sk, vid).Scan(&snap.Pos, &snap.Runtime, &snap.Updated)
	if err != nil {
		return PlayHistorySnapshot{}, false
	}
	return snap, true
}

func (d *DB) GetPlayHistorySnapshotsByPlaybackItemIDs(userID int64, itemIDs []string) (map[string]PlayHistorySnapshot, error) {
	out := map[string]PlayHistorySnapshot{}
	if d == nil || d.db == nil || userID <= 0 || len(itemIDs) == 0 {
		return out, nil
	}
	uniq := make([]string, 0, len(itemIDs))
	seen := map[string]struct{}{}
	for _, id := range itemIDs {
		k := strings.TrimSpace(id)
		if k == "" {
			continue
		}
		if _, ok := seen[k]; ok {
			continue
		}
		seen[k] = struct{}{}
		uniq = append(uniq, k)
	}
	if len(uniq) == 0 {
		return out, nil
	}

	placeholders := strings.Repeat("?,", len(uniq))
	placeholders = strings.TrimSuffix(placeholders, ",")
	args := make([]any, 0, 1+len(uniq))
	args = append(args, userID)
	for _, id := range uniq {
		args = append(args, id)
	}
	rows, err := d.db.Query(`
			SELECT h.playback_item_id, h.playback_position_ticks, h.playback_runtime_ticks, h.updated_at
			FROM user_play_history h
			JOIN (
			  SELECT playback_item_id, MAX(updated_at*1000000000 + id) AS mx
			  FROM user_play_history
			  WHERE user_id = ? AND playback_item_id IN (`+placeholders+`)
			  GROUP BY playback_item_id
			) x ON x.playback_item_id = h.playback_item_id AND x.mx = (h.updated_at*1000000000 + h.id)
			WHERE h.user_id = ? AND h.playback_item_id IN (`+placeholders+`)
		`, append(args, args...)...)
	if err != nil {
		return out, err
	}
	defer rows.Close()
	for rows.Next() {
		var (
			itemID  string
			pos     int64
			runtime int64
			updated int64
		)
		_ = rows.Scan(&itemID, &pos, &runtime, &updated)
		itemID = strings.TrimSpace(itemID)
		if itemID == "" {
			continue
		}
		out[itemID] = PlayHistorySnapshot{Pos: pos, Runtime: runtime, Updated: updated}
	}
	return out, nil
}

func (d *DB) ListResumePlaybackItems(userID int64, limit int, offset int) ([]PlayHistorySnapshot, []string, error) {
	if d == nil || d.db == nil || userID <= 0 {
		return []PlayHistorySnapshot{}, []string{}, nil
	}
	if limit <= 0 {
		limit = 12
	}
	if limit > 60 {
		limit = 60
	}
	if offset < 0 {
		offset = 0
	}
	rows, err := d.db.Query(`
			SELECT h.playback_item_id, h.playback_position_ticks, h.playback_runtime_ticks, h.updated_at
			FROM user_play_history h
			JOIN (
			  SELECT playback_item_id, MAX(updated_at*1000000000 + id) AS mx
			  FROM user_play_history
			  WHERE user_id = ? AND playback_item_id <> '' AND playback_position_ticks > 0
			  GROUP BY playback_item_id
			) x ON x.playback_item_id = h.playback_item_id AND x.mx = (h.updated_at*1000000000 + h.id)
			WHERE h.user_id = ? AND h.playback_item_id <> '' AND h.playback_position_ticks > 0
			ORDER BY h.updated_at DESC
			LIMIT ? OFFSET ?
		`, userID, userID, limit, offset)
	if err != nil {
		return []PlayHistorySnapshot{}, []string{}, err
	}
	defer rows.Close()
	snaps := []PlayHistorySnapshot{}
	ids := []string{}
	for rows.Next() {
		var (
			itemID  string
			pos     int64
			runtime int64
			updated int64
		)
		_ = rows.Scan(&itemID, &pos, &runtime, &updated)
		itemID = strings.TrimSpace(itemID)
		if itemID == "" {
			continue
		}
		ids = append(ids, itemID)
		snaps = append(snaps, PlayHistorySnapshot{Pos: pos, Runtime: runtime, Updated: updated})
	}
	return snaps, ids, nil
}

func (d *DB) CountResumePlaybackItems(userID int64) (int, error) {
	if d == nil || d.db == nil || userID <= 0 {
		return 0, nil
	}
	var total int
	err := d.db.QueryRow(`
		SELECT COUNT(1)
		FROM (
		  SELECT playback_item_id
		  FROM user_play_history
		  WHERE user_id = ? AND playback_item_id <> '' AND playback_position_ticks > 0
		  GROUP BY playback_item_id
		)
	`, userID).Scan(&total)
	return total, err
}

// DeleteResumePlaybackItemsByPrefix removes resume/play-history entries by playback_item_id prefix.
// This matches Emby-style HideFromResume calls that may target a series ID while stored rows are episodes.
func (d *DB) DeleteResumePlaybackItemsByPrefix(userID int64, prefix string) (int64, error) {
	if d == nil || d.db == nil || userID <= 0 {
		return 0, nil
	}
	p := strings.ToLower(strings.TrimSpace(prefix))
	if p == "" {
		return 0, nil
	}
	res, err := d.db.Exec(`
		DELETE FROM user_play_history
		WHERE user_id = ?
		  AND playback_item_id <> ''
		  AND lower(playback_item_id) LIKE ?
	`, userID, p+"%")
	if err != nil {
		return 0, err
	}
	n, _ := res.RowsAffected()
	return n, nil
}

func (d *DB) DeleteResumePlaybackItem(userID int64, itemID string) (int64, error) {
	if d == nil || d.db == nil || userID <= 0 {
		return 0, nil
	}
	id := strings.TrimSpace(itemID)
	if id == "" {
		return 0, nil
	}
	res, err := d.db.Exec(`
		DELETE FROM user_play_history
		WHERE user_id = ?
		  AND playback_item_id = ?
	`, userID, id)
	if err != nil {
		return 0, err
	}
	n, _ := res.RowsAffected()
	return n, nil
}
