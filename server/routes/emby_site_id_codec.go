package routes

import (
	"encoding/base64"
	"encoding/json"
	"strings"
)

const (
	embySiteSeriesIDPrefix  = "site1_"
	embySiteSeasonIDPrefix  = "sitesea1_"
	embySiteEpisodeIDPrefix = "siteep1_"
)

type embySiteSeriesIDPayload struct {
	V       int    `json:"v"`
	SiteKey string `json:"sk"`
	Site    string `json:"sn"`
	SiteAPI string `json:"sa"`
	VideoID string `json:"vid"`
	Name    string `json:"n"`
	Pic     string `json:"p"`
	Remark  string `json:"r"`
}

type embySiteSeasonIDPayload struct {
	V       int    `json:"v"`
	SiteKey string `json:"sk"`
	Site    string `json:"sn"`
	SiteAPI string `json:"sa"`
	VideoID string `json:"vid"`
	Pan     int    `json:"pan"`
	Label   string `json:"l"`
	Pic     string `json:"p"`
	Remark  string `json:"r"`
}

type embySiteEpisodeIDPayload struct {
	V       int    `json:"v"`
	SiteKey string `json:"sk"`
	Site    string `json:"sn"`
	SiteAPI string `json:"sa"`
	VideoID string `json:"vid"`
	Pan     int    `json:"pan"`
	Ep      int    `json:"ep"`
	Flag    string `json:"f"`
	URL     string `json:"u"`
	Pic     string `json:"p"`
	Remark  string `json:"r"`
}

func embyEncodeSiteID(prefix string, payload any) string {
	b, err := json.Marshal(payload)
	if err != nil || len(b) == 0 {
		return ""
	}
	enc := base64.RawURLEncoding.EncodeToString(b)
	if enc == "" {
		return ""
	}
	return prefix + enc
}

func embyDecodeSiteID(prefix string, id string, out any) bool {
	raw := strings.TrimSpace(id)
	if !strings.HasPrefix(raw, prefix) {
		return false
	}
	enc := strings.TrimSpace(strings.TrimPrefix(raw, prefix))
	if enc == "" {
		return false
	}
	// Prevent pathological allocations from untrusted clients.
	if len(enc) > 16*1024 {
		return false
	}
	b, err := base64.RawURLEncoding.DecodeString(enc)
	if err != nil || len(b) == 0 {
		return false
	}
	if len(b) > 32*1024 {
		return false
	}
	if err := json.Unmarshal(b, out); err != nil {
		return false
	}
	return true
}

func embyEncodeSiteSeriesID(p embySiteSeriesIDPayload) string {
	p.V = 1
	p.SiteKey = strings.TrimSpace(p.SiteKey)
	p.Site = strings.TrimSpace(p.Site)
	p.SiteAPI = strings.TrimSpace(p.SiteAPI)
	p.VideoID = strings.TrimSpace(p.VideoID)
	p.Name = strings.TrimSpace(p.Name)
	p.Pic = strings.TrimSpace(p.Pic)
	p.Remark = strings.TrimSpace(p.Remark)
	if p.SiteKey == "" || p.SiteAPI == "" || p.VideoID == "" {
		return ""
	}
	return embyEncodeSiteID(embySiteSeriesIDPrefix, p)
}

func embyDecodeSiteSeriesID(id string) (embySiteSeriesIDPayload, bool) {
	var p embySiteSeriesIDPayload
	if !embyDecodeSiteID(embySiteSeriesIDPrefix, id, &p) {
		return embySiteSeriesIDPayload{}, false
	}
	p.SiteKey = strings.TrimSpace(p.SiteKey)
	p.SiteAPI = strings.TrimSpace(p.SiteAPI)
	p.VideoID = strings.TrimSpace(p.VideoID)
	if p.SiteKey == "" || p.SiteAPI == "" || p.VideoID == "" {
		return embySiteSeriesIDPayload{}, false
	}
	return p, true
}

func embyEncodeSiteSeasonID(p embySiteSeasonIDPayload) string {
	p.V = 1
	p.SiteKey = strings.TrimSpace(p.SiteKey)
	p.Site = strings.TrimSpace(p.Site)
	p.SiteAPI = strings.TrimSpace(p.SiteAPI)
	p.VideoID = strings.TrimSpace(p.VideoID)
	p.Label = strings.TrimSpace(p.Label)
	p.Pic = strings.TrimSpace(p.Pic)
	p.Remark = strings.TrimSpace(p.Remark)
	if p.SiteKey == "" || p.SiteAPI == "" || p.VideoID == "" || p.Pan <= 0 {
		return ""
	}
	return embyEncodeSiteID(embySiteSeasonIDPrefix, p)
}

func embyDecodeSiteSeasonID(id string) (embySiteSeasonIDPayload, bool) {
	var p embySiteSeasonIDPayload
	if !embyDecodeSiteID(embySiteSeasonIDPrefix, id, &p) {
		return embySiteSeasonIDPayload{}, false
	}
	p.SiteKey = strings.TrimSpace(p.SiteKey)
	p.SiteAPI = strings.TrimSpace(p.SiteAPI)
	p.VideoID = strings.TrimSpace(p.VideoID)
	if p.SiteKey == "" || p.SiteAPI == "" || p.VideoID == "" || p.Pan <= 0 {
		return embySiteSeasonIDPayload{}, false
	}
	return p, true
}

func embyEncodeSiteEpisodeID(p embySiteEpisodeIDPayload) string {
	p.V = 1
	p.SiteKey = strings.TrimSpace(p.SiteKey)
	p.Site = strings.TrimSpace(p.Site)
	p.SiteAPI = strings.TrimSpace(p.SiteAPI)
	p.VideoID = strings.TrimSpace(p.VideoID)
	p.Flag = strings.TrimSpace(p.Flag)
	p.URL = strings.TrimSpace(p.URL)
	p.Pic = strings.TrimSpace(p.Pic)
	p.Remark = strings.TrimSpace(p.Remark)
	if p.SiteKey == "" || p.SiteAPI == "" || p.VideoID == "" || p.Pan <= 0 || p.Ep <= 0 || p.Flag == "" || p.URL == "" {
		return ""
	}
	return embyEncodeSiteID(embySiteEpisodeIDPrefix, p)
}

func embyDecodeSiteEpisodeID(id string) (embySiteEpisodeIDPayload, bool) {
	var p embySiteEpisodeIDPayload
	if !embyDecodeSiteID(embySiteEpisodeIDPrefix, id, &p) {
		return embySiteEpisodeIDPayload{}, false
	}
	p.SiteKey = strings.TrimSpace(p.SiteKey)
	p.SiteAPI = strings.TrimSpace(p.SiteAPI)
	p.VideoID = strings.TrimSpace(p.VideoID)
	p.Flag = strings.TrimSpace(p.Flag)
	p.URL = strings.TrimSpace(p.URL)
	if p.SiteKey == "" || p.SiteAPI == "" || p.VideoID == "" || p.Pan <= 0 || p.Ep <= 0 || p.Flag == "" || p.URL == "" {
		return embySiteEpisodeIDPayload{}, false
	}
	return p, true
}

