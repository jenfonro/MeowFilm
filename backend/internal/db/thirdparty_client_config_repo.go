package db

import (
	"crypto/sha1"
	"database/sql"
	"encoding/hex"
	"encoding/json"
	"strings"
	"time"
)

type ThirdPartyClientHomeSection struct {
	ID         string `json:"id"`
	Name       string `json:"name"`
	Module     string `json:"module"`               // douban_tv | douban_movie | bangumi_anime | douban_variety | history | site_data
	MediaType  string `json:"mediaType"`            // tv | movie | mixed
	SiteKey    string `json:"siteKey,omitempty"`    // required when module=site_data
	CategoryID string `json:"categoryId,omitempty"` // required when module=site_data
	CardStyle  string `json:"cardStyle,omitempty"`  // tmdb | site (only for module=site_data)
}

func DefaultThirdPartyClientHomeSections() []ThirdPartyClientHomeSection {
	return []ThirdPartyClientHomeSection{
		{ID: "view_history", Name: "历史", Module: "history", MediaType: "mixed"},
		{ID: "view_tmdb_tv", Name: "剧集", Module: "douban_tv", MediaType: "tv"},
		{ID: "view_tmdb_movies", Name: "电影", Module: "douban_movie", MediaType: "movie"},
		{ID: "view_tmdb_anime", Name: "动漫", Module: "bangumi_anime", MediaType: "tv"},
		{ID: "view_tmdb_show", Name: "综艺", Module: "douban_variety", MediaType: "tv"},
	}
}

func normalizeThirdPartyClientHomeSections(in []ThirdPartyClientHomeSection) []ThirdPartyClientHomeSection {
	list := make([]ThirdPartyClientHomeSection, 0, len(in))
	seen := map[string]struct{}{}
	for _, s := range in {
		s.ID = strings.TrimSpace(s.ID)
		s.Name = strings.TrimSpace(s.Name)
		s.Module = strings.ToLower(strings.TrimSpace(s.Module))
		s.MediaType = strings.ToLower(strings.TrimSpace(s.MediaType))
		s.SiteKey = strings.TrimSpace(s.SiteKey)
		s.CategoryID = strings.TrimSpace(s.CategoryID)
		s.CardStyle = strings.ToLower(strings.TrimSpace(s.CardStyle))

		if s.Name == "" || s.Module == "" {
			continue
		}

		switch s.Module {
		case "douban_tv", "douban_movie", "bangumi_anime", "douban_variety", "history", "site_data":
		default:
			continue
		}

		if s.Module == "site_data" {
			if s.SiteKey == "" || s.CategoryID == "" {
				continue
			}
			if s.MediaType != "tv" && s.MediaType != "movie" {
				s.MediaType = "tv"
			}
			if s.CardStyle != "tmdb" && s.CardStyle != "site" {
				s.CardStyle = "tmdb"
			}
		} else if s.Module == "history" {
			s.MediaType = "mixed"
		} else {
			if s.MediaType != "tv" && s.MediaType != "movie" {
				switch s.Module {
				case "douban_movie":
					s.MediaType = "movie"
				default:
					s.MediaType = "tv"
				}
			}
			s.SiteKey = ""
			s.CategoryID = ""
			s.CardStyle = ""
		}

		if s.ID == "" {
			h := sha1.Sum([]byte(s.Name + "|" + s.Module + "|" + s.MediaType + "|" + s.SiteKey + "|" + s.CategoryID))
			s.ID = "view_custom_" + hex.EncodeToString(h[:8])
		}
		if !strings.HasPrefix(s.ID, "view_") {
			s.ID = "view_" + s.ID
		}
		if _, ok := seen[s.ID]; ok {
			continue
		}
		seen[s.ID] = struct{}{}
		list = append(list, s)
		if len(list) >= 24 {
			break
		}
	}
	if len(list) == 0 {
		return DefaultThirdPartyClientHomeSections()
	}
	return list
}

func (d *DB) ReadThirdPartyClientHomeSections() ([]ThirdPartyClientHomeSection, error) {
	if d == nil || d.db == nil {
		return DefaultThirdPartyClientHomeSections(), nil
	}
	var raw sql.NullString
	_ = d.db.QueryRow(`SELECT home_sections_json FROM app_third_party_client WHERE id=1 LIMIT 1`).Scan(&raw)
	text := strings.TrimSpace(raw.String)
	if text == "" {
		return DefaultThirdPartyClientHomeSections(), nil
	}

	var out []ThirdPartyClientHomeSection
	if err := json.Unmarshal([]byte(text), &out); err != nil {
		return DefaultThirdPartyClientHomeSections(), nil
	}

	return normalizeThirdPartyClientHomeSections(out), nil
}

func (d *DB) ReplaceThirdPartyClientHomeSections(sections []ThirdPartyClientHomeSection) error {
	if d == nil || d.db == nil {
		return nil
	}
	normalized := normalizeThirdPartyClientHomeSections(sections)
	b, _ := json.Marshal(normalized)
	now := time.Now().Unix()
	_, _ = d.db.Exec(`
		INSERT INTO app_third_party_client(id, home_sections_json, updated_at)
		VALUES(1, ?, ?)
		ON CONFLICT(id) DO UPDATE SET home_sections_json=excluded.home_sections_json, updated_at=excluded.updated_at
	`, string(b), now)
	return nil
}
