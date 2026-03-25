package db

import (
	"database/sql"
	"errors"
	"strings"
	"time"
)

type FavoriteRow struct {
	SiteKey    string
	SiteName   string
	SpiderAPI  string
	SiteDetail string
	ContentKey string
	Poster     string
	Remark     string
	UpdatedAt  int64
}

type FavoriteUpsert struct {
	UserID     int64
	SiteKey    string
	SiteName   string // ignored (derived); kept for API compatibility
	SpiderAPI  string // ignored (derived); kept for API compatibility
	SiteDetail string
	ContentKey string
	Poster     string
	Remark     string
	UpdatedAt  int64
}

func (d *DB) ListFavorites(userID int64, limit int) ([]FavoriteRow, error) {
	if d == nil || d.db == nil || userID <= 0 {
		return []FavoriteRow{}, nil
	}
	if limit <= 0 {
		limit = 200
	}
	if limit > 2000 {
		limit = 2000
	}
	rows, err := d.db.Query(`
		SELECT
		  sv.site_key,
		  COALESCE(gs.name, CASE WHEN sv.site_kind='emby' THEN 'Emby' ELSE '' END) AS site_name,
		  COALESCE(gs.api, CASE WHEN sv.site_kind='emby' THEN 'emby' ELSE '' END) AS spider_api,
		  sv.site_detail,
		  sv.title,
		  sv.poster,
		  sv.remark,
		  f.updated_at
		FROM user_favorite f
		JOIN site_video sv ON sv.id = f.site_video_id
		LEFT JOIN video_source_site gs ON sv.site_kind='global' AND gs.key = sv.site_key
		WHERE f.user_id=?
		ORDER BY f.updated_at DESC
		LIMIT ?
	`, userID, limit)
	if err != nil {
		return []FavoriteRow{}, err
	}
	defer rows.Close()
	out := []FavoriteRow{}
	for rows.Next() {
		var r FavoriteRow
		_ = rows.Scan(&r.SiteKey, &r.SiteName, &r.SpiderAPI, &r.SiteDetail, &r.ContentKey, &r.Poster, &r.Remark, &r.UpdatedAt)
		out = append(out, r)
	}
	return out, nil
}

func (d *DB) IsFavorited(userID int64, siteKey string, siteDetail string) (bool, error) {
	if d == nil || d.db == nil || userID <= 0 {
		return false, nil
	}
	sk := strings.TrimSpace(siteKey)
	vid := strings.TrimSpace(siteDetail)
	if sk == "" || vid == "" {
		return false, nil
	}
	siteKind, ownerID := d.resolveSiteKindAndOwner(userID, sk)
	var siteVideoID int64
	if err := d.db.QueryRow(`
		SELECT id FROM site_video WHERE site_kind=? AND owner_user_id=? AND site_key=? AND site_detail=? LIMIT 1
	`, siteKind, ownerID, sk, vid).Scan(&siteVideoID); err != nil {
		if err == sql.ErrNoRows {
			return false, nil
		}
		return false, err
	}
	var v int
	err := d.db.QueryRow(`SELECT 1 FROM user_favorite WHERE user_id=? AND site_video_id=? LIMIT 1`, userID, siteVideoID).Scan(&v)
	if err != nil {
		if err == sql.ErrNoRows {
			return false, nil
		}
		return false, err
	}
	return true, nil
}

func (d *DB) DeleteFavorite(userID int64, siteKey string, siteDetail string) (int64, error) {
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
	res, err := d.db.Exec(`DELETE FROM user_favorite WHERE user_id=? AND site_video_id=?`, userID, siteVideoID)
	if err != nil || res == nil {
		return 0, err
	}
	n, _ := res.RowsAffected()
	return n, nil
}

func (d *DB) UpsertFavorite(row FavoriteUpsert) error {
	if d == nil || d.db == nil {
		return errors.New("db nil")
	}
	if row.UserID <= 0 {
		return errors.New("invalid user id")
	}
	sk := strings.TrimSpace(row.SiteKey)
	vid := strings.TrimSpace(row.SiteDetail)
	contentKey := strings.TrimSpace(row.ContentKey)
	if sk == "" || vid == "" || contentKey == "" {
		return errors.New("invalid args")
	}
	now := row.UpdatedAt
	if now <= 0 {
		now = time.Now().Unix()
	}
	siteKind, ownerID := d.resolveSiteKindAndOwner(row.UserID, sk)

	tx, err := d.db.Begin()
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback() }()

	svid, err := d.upsertSiteVideo(tx, siteKind, ownerID, sk, vid, contentKey, row.Poster, row.Remark, now)
	if err != nil {
		return err
	}
	_, err = tx.Exec(`
		INSERT INTO user_favorite(user_id, site_video_id, updated_at)
		VALUES(?,?,?)
		ON CONFLICT(user_id, site_video_id) DO UPDATE SET updated_at=excluded.updated_at
	`, row.UserID, svid, now)
	if err != nil {
		return err
	}
	return tx.Commit()
}
