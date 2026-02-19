package db

import (
	"database/sql"
	"errors"
	"strings"
	"time"
)

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

func (d *DB) upsertSiteVideo(tx *sql.Tx, siteKind string, ownerUserID int64, siteKey string, videoID string, title string, poster string, remark string, updatedAt int64) (siteVideoID int64, err error) {
	if tx == nil {
		return 0, errors.New("tx nil")
	}
	sk := strings.TrimSpace(siteKey)
	vid := strings.TrimSpace(videoID)
	t := strings.TrimSpace(title)
	if sk == "" || vid == "" || t == "" {
		return 0, errors.New("invalid args")
	}
	now := updatedAt
	if now <= 0 {
		now = time.Now().Unix()
	}
	_, _ = tx.Exec(`
		INSERT INTO site_video(site_kind, owner_user_id, site_key, video_id, title, poster, remark, updated_at)
		VALUES(?,?,?,?,?,?,?,?)
		ON CONFLICT(site_kind, owner_user_id, site_key, video_id) DO UPDATE SET
		  title = excluded.title,
		  poster = CASE WHEN excluded.poster <> '' THEN excluded.poster ELSE site_video.poster END,
		  remark = excluded.remark,
		  updated_at = excluded.updated_at
	`, strings.TrimSpace(siteKind), ownerUserID, sk, vid, t, strings.TrimSpace(poster), strings.TrimSpace(remark), now)

	err = tx.QueryRow(`
		SELECT id
		FROM site_video
		WHERE site_kind=? AND owner_user_id=? AND site_key=? AND video_id=?
		LIMIT 1
	`, strings.TrimSpace(siteKind), ownerUserID, sk, vid).Scan(&siteVideoID)
	return siteVideoID, err
}
