package db

import (
	"database/sql"
	"errors"
	"strings"
	"time"
)

type PlayHistoryRow struct {
	ContentKey            string
	SiteKey               string
	SiteName              string
	SpiderAPI             string
	VideoID               string
	VideoTitle            string
	VideoPoster           string
	VideoRemark           string
	TMDBID                int
	TMDBType              string
	PanLabel              string
	PlayFlag              string
	EpisodeIndex          int
	EpisodeName           string
	UpdatedAt             int64
	PlaybackPositionTicks int64
	PlaybackRuntimeTicks  int64
	PlaybackItemID        string
}

type PlayHistoryUpsert struct {
	UserID                int64
	ContentKey            string
	SiteKey               string
	SiteName              string // ignored (derived); kept for API compatibility
	SpiderAPI             string // ignored (derived); kept for API compatibility
	VideoID               string
	VideoTitle            string
	VideoPoster           string
	VideoRemark           string
	TMDBID                int
	TMDBType              string
	PanLabel              string
	PlayFlag              string
	EpisodeIndex          int
	EpisodeName           string
	UpdatedAt             int64
	PlaybackPositionTicks int64
	PlaybackRuntimeTicks  int64
	PlaybackItemID        string
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
	`, strings.ToLower(key), now, now)

	if err := tx.QueryRow(`SELECT id FROM content WHERE content_key = ? LIMIT 1`, strings.ToLower(key)).Scan(&contentID); err != nil {
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
	key := strings.ToLower(strings.TrimSpace(contentKey))
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

func (d *DB) DeletePlayHistoryDedupByContent(userID int64, contentKey string, tmdbID int, tmdbType string) (int64, error) {
	if d == nil || d.db == nil || userID <= 0 {
		return 0, nil
	}
	key := strings.ToLower(strings.TrimSpace(contentKey))
	if key == "" {
		return 0, nil
	}
	var cid int64
	if err := d.db.QueryRow(`SELECT id FROM content WHERE content_key=? LIMIT 1`, key).Scan(&cid); err != nil {
		return 0, nil
	}
	typ := strings.TrimSpace(tmdbType)
	var res sql.Result
	var err error
	if tmdbID > 0 && (typ == "tv" || typ == "movie") {
		res, err = d.db.Exec(`
			DELETE FROM user_play_history
			WHERE user_id = ?
			  AND (content_id = ? OR content_id IN (SELECT content_id FROM content_tmdb WHERE tmdb_id = ? AND tmdb_type = ?))
		`, userID, cid, tmdbID, typ)
	} else {
		res, err = d.db.Exec(`DELETE FROM user_play_history WHERE user_id=? AND content_id=?`, userID, cid)
	}
	if err != nil || res == nil {
		return 0, err
	}
	n, _ := res.RowsAffected()
	return n, nil
}

func (d *DB) UpsertPlayHistory(row PlayHistoryUpsert) error {
	if d == nil || d.db == nil {
		return errors.New("db nil")
	}
	if row.UserID <= 0 {
		return errors.New("invalid user id")
	}
	contentKey := strings.ToLower(strings.TrimSpace(row.ContentKey))
	siteKey := strings.TrimSpace(row.SiteKey)
	videoID := strings.TrimSpace(row.VideoID)
	videoTitle := strings.TrimSpace(row.VideoTitle)
	if contentKey == "" || siteKey == "" || videoID == "" || videoTitle == "" {
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
	siteVideoID, err := d.upsertSiteVideo(tx, siteKind, ownerID, siteKey, videoID, videoTitle, row.VideoPoster, row.VideoRemark, now)
	if err != nil {
		return err
	}

	_, err = tx.Exec(`
		INSERT INTO user_play_history(
		  user_id, content_id, site_video_id,
		  pan_label, play_flag, episode_index, episode_name,
		  playback_position_ticks, playback_runtime_ticks, playback_item_id,
		  updated_at
		)
		VALUES(?,?,?,?,?,?,?,?,?,?,?)
		ON CONFLICT(user_id, site_video_id) DO UPDATE SET
		  content_id = excluded.content_id,
		  pan_label = excluded.pan_label,
		  play_flag = excluded.play_flag,
		  episode_index = excluded.episode_index,
		  episode_name = excluded.episode_name,
		  playback_position_ticks = CASE WHEN excluded.playback_position_ticks > 0 THEN excluded.playback_position_ticks ELSE user_play_history.playback_position_ticks END,
		  playback_runtime_ticks = CASE WHEN excluded.playback_runtime_ticks > 0 THEN excluded.playback_runtime_ticks ELSE user_play_history.playback_runtime_ticks END,
		  playback_item_id = CASE WHEN excluded.playback_item_id <> '' THEN excluded.playback_item_id ELSE user_play_history.playback_item_id END,
		  updated_at = excluded.updated_at
	`,
		row.UserID,
		contentID,
		siteVideoID,
		strings.TrimSpace(row.PanLabel),
		strings.TrimSpace(row.PlayFlag),
		row.EpisodeIndex,
		strings.TrimSpace(row.EpisodeName),
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
		  sv.video_id,
		  sv.title,
		  sv.poster,
		  sv.remark,
		  COALESCE(tm.tmdb_id, 0) AS tmdb_id,
		  COALESCE(tm.tmdb_type, '') AS tmdb_type,
		  h.pan_label,
		  h.play_flag,
		  h.episode_index,
		  h.episode_name,
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
			&r.VideoID,
			&r.VideoTitle,
			&r.VideoPoster,
			&r.VideoRemark,
			&r.TMDBID,
			&r.TMDBType,
			&r.PanLabel,
			&r.PlayFlag,
			&r.EpisodeIndex,
			&r.EpisodeName,
			&r.UpdatedAt,
			&r.PlaybackPositionTicks,
			&r.PlaybackRuntimeTicks,
			&r.PlaybackItemID,
		)
		out = append(out, r)
	}
	return out, nil
}

func (d *DB) GetPlayHistoryLatestBySiteVideo(userID int64, siteKey string, videoID string) (*PlayHistoryRow, error) {
	if d == nil || d.db == nil || userID <= 0 {
		return nil, nil
	}
	sk := strings.TrimSpace(siteKey)
	vid := strings.TrimSpace(videoID)
	if sk == "" || vid == "" {
		return nil, nil
	}
	siteKind, ownerID := d.resolveSiteKindAndOwner(userID, sk)

	var r PlayHistoryRow
	err := d.db.QueryRow(`
		SELECT
		  c.content_key,
		  sv.site_key,
		  COALESCE(gs.name, CASE WHEN sv.site_kind='emby' THEN 'Emby' ELSE '' END) AS site_name,
		  COALESCE(gs.api, CASE WHEN sv.site_kind='emby' THEN 'emby' ELSE '' END) AS spider_api,
		  sv.video_id,
		  sv.title,
		  sv.poster,
		  sv.remark,
		  COALESCE(tm.tmdb_id, 0) AS tmdb_id,
		  COALESCE(tm.tmdb_type, '') AS tmdb_type,
		  h.pan_label,
		  h.play_flag,
		  h.episode_index,
		  h.episode_name,
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
		  AND sv.video_id = ?
		ORDER BY h.updated_at DESC
		LIMIT 1
	`, userID, siteKind, ownerID, sk, vid).Scan(
		&r.ContentKey,
		&r.SiteKey,
		&r.SiteName,
		&r.SpiderAPI,
		&r.VideoID,
		&r.VideoTitle,
		&r.VideoPoster,
		&r.VideoRemark,
		&r.TMDBID,
		&r.TMDBType,
		&r.PanLabel,
		&r.PlayFlag,
		&r.EpisodeIndex,
		&r.EpisodeName,
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

func (d *DB) DeletePlayHistoryByContentKey(userID int64, contentKey string) (int64, error) {
	if d == nil || d.db == nil || userID <= 0 {
		return 0, nil
	}
	key := strings.ToLower(strings.TrimSpace(contentKey))
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

func (d *DB) DeletePlayHistoryBySiteVideo(userID int64, siteKey string, videoID string) (int64, error) {
	if d == nil || d.db == nil || userID <= 0 {
		return 0, nil
	}
	sk := strings.TrimSpace(siteKey)
	vid := strings.TrimSpace(videoID)
	if sk == "" || vid == "" {
		return 0, nil
	}
	siteKind, ownerID := d.resolveSiteKindAndOwner(userID, sk)
	var siteVideoID int64
	if err := d.db.QueryRow(`
		SELECT id FROM site_video WHERE site_kind=? AND owner_user_id=? AND site_key=? AND video_id=? LIMIT 1
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

func (d *DB) GetPlayHistorySnapshotBySiteVideo(userID int64, siteKey string, videoID string) (PlayHistorySnapshot, bool) {
	if d == nil || d.db == nil || userID <= 0 {
		return PlayHistorySnapshot{}, false
	}
	sk := strings.TrimSpace(siteKey)
	vid := strings.TrimSpace(videoID)
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
		  AND sv.video_id = ?
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
		SELECT playback_item_id, playback_position_ticks, playback_runtime_ticks, updated_at
		FROM user_play_history
		WHERE user_id = ? AND playback_item_id IN (`+placeholders+`)
	`, args...)
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
		SELECT playback_item_id, playback_position_ticks, playback_runtime_ticks, updated_at
		FROM user_play_history
		WHERE user_id = ? AND playback_item_id <> ''
		ORDER BY updated_at DESC
		LIMIT ? OFFSET ?
	`, userID, limit, offset)
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
	err := d.db.QueryRow(`SELECT COUNT(1) FROM user_play_history WHERE user_id = ? AND playback_item_id <> ''`, userID).Scan(&total)
	return total, err
}
