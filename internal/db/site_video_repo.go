package db

import (
	"database/sql"
	"errors"
	"strings"
	"time"
)

type SiteVideo struct {
	ID         int64
	SiteKind   string
	SiteKey    string
	SiteDetail string
	Title      string
	Poster     string
	Remark     string
	UpdatedAt  int64
}

func (d *DB) resolveSiteKindAndOwner(userID int64, siteKey string) (siteKind string, ownerUserID int64) {
	key := strings.TrimSpace(siteKey)
	if key == "" {
		return "global", 0
	}
	if strings.EqualFold(key, "emby") {
		return "emby", 0
	}
	return "global", 0
}

func (d *DB) UpsertSiteVideo(siteKey string, siteDetail string, title string, poster string, remark string, updatedAt int64) (siteVideoID int64, err error) {
	if d == nil || d.db == nil {
		return 0, nil
	}
	sk := strings.TrimSpace(siteKey)
	vid := strings.TrimSpace(siteDetail)
	t := strings.TrimSpace(title)
	if sk == "" || vid == "" || t == "" {
		return 0, errors.New("invalid args")
	}
	now := updatedAt
	if now <= 0 {
		now = time.Now().Unix()
	}
	siteKind, ownerID := d.resolveSiteKindAndOwner(0, sk)

	d.mu.Lock()
	tx, err := d.db.Begin()
	d.mu.Unlock()
	if err != nil {
		return 0, err
	}
	defer func() {
		if err != nil {
			_ = tx.Rollback()
			return
		}
		_ = tx.Commit()
	}()

	id, err := d.upsertSiteVideo(tx, siteKind, ownerID, sk, vid, t, poster, remark, now)
	if err != nil {
		return 0, err
	}
	return id, nil
}

func (d *DB) GetSiteVideoByKey(siteKey string, siteDetail string) (*SiteVideo, error) {
	if d == nil || d.db == nil {
		return nil, nil
	}
	sk := strings.TrimSpace(siteKey)
	vid := strings.TrimSpace(siteDetail)
	if sk == "" || vid == "" {
		return nil, nil
	}
	siteKind, ownerID := d.resolveSiteKindAndOwner(0, sk)
	var row SiteVideo
	row.SiteKind = siteKind
	row.SiteKey = sk
	row.SiteDetail = vid
	var poster sql.NullString
	var remark sql.NullString
	err := d.db.QueryRow(`
		SELECT id, title, poster, remark, updated_at
		FROM site_video
		WHERE site_kind=? AND owner_user_id=? AND site_key=? AND site_detail=?
		LIMIT 1
	`, strings.TrimSpace(siteKind), ownerID, sk, vid).Scan(&row.ID, &row.Title, &poster, &remark, &row.UpdatedAt)
	if err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return nil, nil
		}
		return nil, err
	}
	row.Poster = strings.TrimSpace(poster.String)
	row.Remark = strings.TrimSpace(remark.String)
	return &row, nil
}

func (d *DB) GetSiteVideoByID(id int64) (*SiteVideo, error) {
	if d == nil || d.db == nil || id <= 0 {
		return nil, nil
	}
	var row SiteVideo
	var poster sql.NullString
	var remark sql.NullString
	err := d.db.QueryRow(`
		SELECT site_kind, site_key, site_detail, title, poster, remark, updated_at
		FROM site_video
		WHERE id=?
		LIMIT 1
	`, id).Scan(&row.SiteKind, &row.SiteKey, &row.SiteDetail, &row.Title, &poster, &remark, &row.UpdatedAt)
	if err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return nil, nil
		}
		return nil, err
	}
	row.ID = id
	row.Poster = strings.TrimSpace(poster.String)
	row.Remark = strings.TrimSpace(remark.String)
	row.SiteKind = strings.TrimSpace(row.SiteKind)
	row.SiteKey = strings.TrimSpace(row.SiteKey)
	row.SiteDetail = strings.TrimSpace(row.SiteDetail)
	row.Title = strings.TrimSpace(row.Title)
	return &row, nil
}

func (d *DB) upsertSiteVideo(tx *sql.Tx, siteKind string, ownerUserID int64, siteKey string, siteDetail string, title string, poster string, remark string, updatedAt int64) (siteVideoID int64, err error) {
	if tx == nil {
		return 0, errors.New("tx nil")
	}
	sk := strings.TrimSpace(siteKey)
	vid := strings.TrimSpace(siteDetail)
	t := strings.TrimSpace(title)
	if sk == "" || vid == "" || t == "" {
		return 0, errors.New("invalid args")
	}
	now := updatedAt
	if now <= 0 {
		now = time.Now().Unix()
	}
	_, _ = tx.Exec(`
		INSERT INTO site_video(site_kind, owner_user_id, site_key, site_detail, title, poster, remark, updated_at)
		VALUES(?,?,?,?,?,?,?,?)
		ON CONFLICT(site_kind, owner_user_id, site_key, site_detail) DO UPDATE SET
		  title = excluded.title,
		  poster = CASE WHEN excluded.poster <> '' THEN excluded.poster ELSE site_video.poster END,
		  remark = excluded.remark,
		  updated_at = excluded.updated_at
	`, strings.TrimSpace(siteKind), ownerUserID, sk, vid, t, strings.TrimSpace(poster), strings.TrimSpace(remark), now)

	err = tx.QueryRow(`
		SELECT id
		FROM site_video
		WHERE site_kind=? AND owner_user_id=? AND site_key=? AND site_detail=?
		LIMIT 1
	`, strings.TrimSpace(siteKind), ownerUserID, sk, vid).Scan(&siteVideoID)
	return siteVideoID, err
}
