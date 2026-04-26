package db

import (
	"errors"
	"strings"
	"time"
)

type SmartManualItem struct {
	ID          int64
	TMDBType    string
	TMDBID      int
	SiteKey     string
	SpiderAPI   string
	SiteDetail  string
	PanFlag     string
	SeasonHint  string
	ErrorCount  int
	AutoDisable bool
	Enabled     bool
	UpdatedAt   int64
}

func (d *DB) ListSmartManualItems(tmdbType string, tmdbID int) ([]SmartManualItem, error) {
	if d == nil || d.db == nil {
		return []SmartManualItem{}, nil
	}
	typ := normalizeSmartManualTMDBType(tmdbType)
	id := tmdbID
	if typ == "" || id <= 0 {
		return []SmartManualItem{}, nil
	}
	rows, err := d.db.Query(`
		SELECT id, tmdb_type, tmdb_id, site_key, spider_api, site_detail, pan_flag, season_hint, error_count, auto_disable, enabled, updated_at
		FROM smart_manual_item
		WHERE tmdb_type=? AND tmdb_id=?
		ORDER BY updated_at DESC, id DESC
	`, typ, id)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	out := make([]SmartManualItem, 0, 16)
	for rows.Next() {
		var item SmartManualItem
		var autoDisable int
		var enabled int
		_ = rows.Scan(&item.ID, &item.TMDBType, &item.TMDBID, &item.SiteKey, &item.SpiderAPI, &item.SiteDetail, &item.PanFlag, &item.SeasonHint, &item.ErrorCount, &autoDisable, &enabled, &item.UpdatedAt)
		item.TMDBType = normalizeSmartManualTMDBType(item.TMDBType)
		if item.ID <= 0 || item.TMDBType == "" || item.TMDBID <= 0 {
			continue
		}
		item.SiteKey = strings.TrimSpace(item.SiteKey)
		item.SpiderAPI = strings.TrimSpace(item.SpiderAPI)
		item.SiteDetail = strings.TrimSpace(item.SiteDetail)
		item.PanFlag = strings.TrimSpace(item.PanFlag)
		item.SeasonHint = strings.ToUpper(strings.TrimSpace(item.SeasonHint))
		if item.ErrorCount < 0 {
			item.ErrorCount = 0
		}
		item.AutoDisable = autoDisable == 1
		item.Enabled = enabled == 1
		out = append(out, item)
	}
	return out, nil
}

func (d *DB) AddSmartManualItem(tmdbType string, tmdbID int, siteKey string, spiderAPI string, siteDetail string, panFlag string, seasonHint string, autoDisable bool) error {
	if d == nil || d.db == nil {
		return nil
	}
	typ := normalizeSmartManualTMDBType(tmdbType)
	id := tmdbID
	if typ == "" || id <= 0 {
		return errors.New("invalid params")
	}
	ak := strings.TrimSpace(siteKey)
	api := strings.TrimSpace(spiderAPI)
	detail := strings.TrimSpace(siteDetail)
	pf := strings.TrimSpace(panFlag)
	if pf == "" && (ak == "" || api == "" || detail == "") {
		return errors.New("invalid params")
	}
	hint := strings.ToUpper(strings.TrimSpace(seasonHint))
	now := time.Now().Unix()
	auto := 0
	if autoDisable {
		auto = 1
	}
	_, err := d.db.Exec(`
		INSERT INTO smart_manual_item(tmdb_type, tmdb_id, site_key, spider_api, site_detail, pan_flag, season_hint, auto_disable, enabled, created_at, updated_at)
		VALUES(?, ?, ?, ?, ?, ?, ?, ?, 1, ?, ?)
	`, typ, id, ak, api, detail, pf, hint, auto, now, now)
	return err
}

func (d *DB) UpdateSmartManualItem(id int64, siteKey string, spiderAPI string, siteDetail string, panFlag string, seasonHint string, autoDisable bool) error {
	if d == nil || d.db == nil {
		return nil
	}
	if id <= 0 {
		return errors.New("invalid params")
	}
	ak := strings.TrimSpace(siteKey)
	api := strings.TrimSpace(spiderAPI)
	detail := strings.TrimSpace(siteDetail)
	pf := strings.TrimSpace(panFlag)
	hint := strings.ToUpper(strings.TrimSpace(seasonHint))
	auto := 0
	if autoDisable {
		auto = 1
	}
	now := time.Now().Unix()
	_, err := d.db.Exec(`
		UPDATE smart_manual_item
		SET site_key=?, spider_api=?, site_detail=?, pan_flag=?, season_hint=?, auto_disable=?, updated_at=?
		WHERE id=?
	`, ak, api, detail, pf, hint, auto, now, id)
	return err
}

func (d *DB) DeleteSmartManualItem(id int64) error {
	if d == nil || d.db == nil {
		return nil
	}
	if id <= 0 {
		return errors.New("invalid params")
	}
	_, err := d.db.Exec(`DELETE FROM smart_manual_item WHERE id=?`, id)
	return err
}

func (d *DB) ReportSmartManualItemResult(id int64, success bool) error {
	if d == nil || d.db == nil {
		return nil
	}
	if id <= 0 {
		return errors.New("invalid params")
	}
	ok := 0
	if success {
		ok = 1
	}
	now := time.Now().Unix()
	_, err := d.db.Exec(`
		UPDATE smart_manual_item
		SET
		  error_count = CASE
		    WHEN ? = 1 THEN 0
		    ELSE error_count + 1
		  END,
		  enabled = CASE
		    WHEN ? = 1 THEN enabled
		    WHEN auto_disable = 1 AND (error_count + 1) >= 3 THEN 0
		    ELSE enabled
		  END,
		  updated_at = ?
		WHERE id = ?
	`, ok, ok, now, id)
	return err
}
