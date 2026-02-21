package db

import (
	"database/sql"
	"errors"
	"strings"
	"time"
)

type SmartMatchBlockKeyword struct {
	Keyword   string
	Count     int
	UpdatedAt int64
}

type SmartMatchBlockItem struct {
	Keyword   string
	SiteKey   string
	SpiderAPI string
	VideoID   string
	Poster    string
	UpdatedAt int64
}

func (d *DB) UpsertSmartMatchBlockItem(keyword string, siteKey string, spiderAPI string, videoID string, poster string) error {
	if d == nil || d.db == nil {
		return nil
	}
	k := strings.TrimSpace(keyword)
	sk := strings.TrimSpace(siteKey)
	sapi := strings.TrimSpace(spiderAPI)
	vid := strings.TrimSpace(videoID)
	p := strings.TrimSpace(poster)
	if k == "" || sk == "" || vid == "" {
		return errors.New("invalid params")
	}

	now := time.Now().Unix()
	tx, err := d.db.Begin()
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback() }()

	if _, err := tx.Exec(`
		INSERT INTO smart_match_block_keyword(keyword, created_at, updated_at)
		VALUES(?, ?, ?)
		ON CONFLICT(keyword) DO UPDATE SET updated_at=excluded.updated_at
	`, k, now, now); err != nil {
		return err
	}

	var keywordID int64
	if err := tx.QueryRow(`SELECT id FROM smart_match_block_keyword WHERE keyword=? LIMIT 1`, k).Scan(&keywordID); err != nil {
		return err
	}

	if _, err := tx.Exec(`
		INSERT INTO smart_match_block_item(keyword_id, site_key, spider_api, video_id, poster, created_at, updated_at)
		VALUES(?, ?, ?, ?, ?, ?, ?)
		ON CONFLICT(keyword_id, site_key, video_id) DO UPDATE SET
		  spider_api=excluded.spider_api,
		  poster=excluded.poster,
		  updated_at=excluded.updated_at
	`, keywordID, sk, sapi, vid, p, now, now); err != nil {
		return err
	}

	return tx.Commit()
}

func (d *DB) DeleteSmartMatchBlockItem(keyword string, siteKey string, videoID string) error {
	if d == nil || d.db == nil {
		return nil
	}
	k := strings.TrimSpace(keyword)
	sk := strings.TrimSpace(siteKey)
	vid := strings.TrimSpace(videoID)
	if k == "" || sk == "" || vid == "" {
		return errors.New("invalid params")
	}

	tx, err := d.db.Begin()
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback() }()

	var keywordID int64
	if err := tx.QueryRow(`SELECT id FROM smart_match_block_keyword WHERE keyword=? LIMIT 1`, k).Scan(&keywordID); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			return nil
		}
		return err
	}
	if _, err := tx.Exec(`DELETE FROM smart_match_block_item WHERE keyword_id=? AND site_key=? AND video_id=?`, keywordID, sk, vid); err != nil {
		return err
	}
	_, _ = tx.Exec(`DELETE FROM smart_match_block_keyword WHERE id=? AND NOT EXISTS(SELECT 1 FROM smart_match_block_item WHERE keyword_id=?)`, keywordID, keywordID)
	return tx.Commit()
}

func (d *DB) DeleteSmartMatchBlockKeyword(keyword string) error {
	if d == nil || d.db == nil {
		return nil
	}
	k := strings.TrimSpace(keyword)
	if k == "" {
		return errors.New("invalid params")
	}
	_, err := d.db.Exec(`DELETE FROM smart_match_block_keyword WHERE keyword=?`, k)
	return err
}

func (d *DB) ListSmartMatchBlockKeywords() ([]SmartMatchBlockKeyword, error) {
	if d == nil || d.db == nil {
		return []SmartMatchBlockKeyword{}, nil
	}
	rows, err := d.db.Query(`
		SELECT k.keyword, COUNT(i.id) AS cnt, k.updated_at
		FROM smart_match_block_keyword k
		LEFT JOIN smart_match_block_item i ON i.keyword_id = k.id
		GROUP BY k.id
		ORDER BY k.updated_at DESC, k.keyword ASC
	`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	out := []SmartMatchBlockKeyword{}
	for rows.Next() {
		var (
			kw string
			cnt int
			updated int64
		)
		_ = rows.Scan(&kw, &cnt, &updated)
		kw = strings.TrimSpace(kw)
		if kw == "" {
			continue
		}
		out = append(out, SmartMatchBlockKeyword{Keyword: kw, Count: cnt, UpdatedAt: updated})
	}
	return out, nil
}

func (d *DB) ListSmartMatchBlockItems(keyword string) ([]SmartMatchBlockItem, error) {
	if d == nil || d.db == nil {
		return []SmartMatchBlockItem{}, nil
	}
	k := strings.TrimSpace(keyword)
	if k == "" {
		return []SmartMatchBlockItem{}, nil
	}
	rows, err := d.db.Query(`
		SELECT k.keyword, i.site_key, i.spider_api, i.video_id, i.poster, i.updated_at
		FROM smart_match_block_item i
		INNER JOIN smart_match_block_keyword k ON k.id = i.keyword_id
		WHERE k.keyword = ?
		ORDER BY i.updated_at DESC, i.site_key ASC, i.video_id ASC
	`, k)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	out := []SmartMatchBlockItem{}
	for rows.Next() {
		var it SmartMatchBlockItem
		_ = rows.Scan(&it.Keyword, &it.SiteKey, &it.SpiderAPI, &it.VideoID, &it.Poster, &it.UpdatedAt)
		it.Keyword = strings.TrimSpace(it.Keyword)
		it.SiteKey = strings.TrimSpace(it.SiteKey)
		it.SpiderAPI = strings.TrimSpace(it.SpiderAPI)
		it.VideoID = strings.TrimSpace(it.VideoID)
		it.Poster = strings.TrimSpace(it.Poster)
		if it.Keyword == "" || it.SiteKey == "" || it.VideoID == "" {
			continue
		}
		out = append(out, it)
	}
	return out, nil
}

func (d *DB) HasSmartMatchBlockItem(keyword string, siteKey string, videoID string) bool {
	if d == nil || d.db == nil {
		return false
	}
	k := strings.TrimSpace(keyword)
	sk := strings.TrimSpace(siteKey)
	vid := strings.TrimSpace(videoID)
	if k == "" || sk == "" || vid == "" {
		return false
	}
	var n int
	_ = d.db.QueryRow(`
		SELECT COUNT(1)
		FROM smart_match_block_item i
		INNER JOIN smart_match_block_keyword k ON k.id = i.keyword_id
		WHERE k.keyword=? AND i.site_key=? AND i.video_id=?
		LIMIT 1
	`, k, sk, vid).Scan(&n)
	return n > 0
}
