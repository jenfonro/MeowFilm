package emby_service

import (
	"encoding/base64"
	"fmt"
	"strconv"
	"strings"
)

const (
	siteSeriesPrefix  = "site_"
	siteSeasonPrefix  = "sitepan_"
	siteEpisodePrefix = "siteep_"
)

type itemRef struct {
	Kind           string
	SubKind        string
	MediaType      string
	Source         string
	Variant        string
	RawID          string
	NumericID      int
	SiteKey        string
	SiteDetail     string
	SiteTitle      string
	SitePlayFlag   string
	SiteEpisodeURL string
	Pan            int
	Episode        int
}

type PlaybackItemRef = itemRef

func buildMovieID(tmdbID int) string {
	if tmdbID <= 0 {
		return ""
	}
	return fmt.Sprintf("tmdb_movie_%d", tmdbID)
}

func buildSeriesID(tmdbID int) string {
	if tmdbID <= 0 {
		return ""
	}
	return fmt.Sprintf("tmdb_tv_%d", tmdbID)
}

func buildSeasonID(tmdbID int, season int) string {
	if tmdbID <= 0 || season < 0 {
		return ""
	}
	return fmt.Sprintf("tmdb_tv_%d_s%02d", tmdbID, season)
}

func buildEpisodeID(tmdbID int, season int, episode int) string {
	if tmdbID <= 0 || season < 0 || episode <= 0 {
		return ""
	}
	return fmt.Sprintf("tmdb_tv_%d_s%02d_e%03d", tmdbID, season, episode)
}

func buildTMDBSettingsSeasonID(tmdbID int) string {
	if tmdbID <= 0 {
		return ""
	}
	return fmt.Sprintf("tmdb_tv_%d_settings", tmdbID)
}

func buildTMDBSettingsEpisodeID(tmdbID int, episode int) string {
	if tmdbID <= 0 || episode <= 0 {
		return ""
	}
	return fmt.Sprintf("tmdb_tv_%d_settings_e%03d", tmdbID, episode)
}

func buildSiteSeriesID(siteKey string, siteDetail string) string {
	sk := strings.TrimSpace(siteKey)
	sd := strings.TrimSpace(siteDetail)
	if sk == "" || sd == "" {
		return ""
	}
	return siteSeriesPrefix + encodeSiteIDPart(sk) + "." + encodeSiteIDPart(sd)
}

func buildSiteSeasonID(siteKey string, siteDetail string, pan int) string {
	sk := strings.TrimSpace(siteKey)
	sd := strings.TrimSpace(siteDetail)
	if sk == "" || sd == "" || pan <= 0 {
		return ""
	}
	return siteSeasonPrefix + encodeSiteIDPart(sk) + "." + encodeSiteIDPart(sd) + "." + strconv.Itoa(pan)
}

func buildSiteEpisodeID(siteKey string, siteDetail string, pan int, ep int, title string, flag string, episodeURL string) string {
	sk := strings.TrimSpace(siteKey)
	sd := strings.TrimSpace(siteDetail)
	tt := strings.TrimSpace(title)
	fg := strings.TrimSpace(flag)
	eu := strings.TrimSpace(episodeURL)
	if sk == "" || sd == "" || tt == "" || eu == "" || pan <= 0 || ep <= 0 {
		return ""
	}
	return siteEpisodePrefix + encodeSiteIDPart(sk) + "." + encodeSiteIDPart(sd) + "." + strconv.Itoa(pan) + "." + strconv.Itoa(ep) + "." + encodeSiteIDPart(tt) + "." + encodeSiteIDPart(fg) + "." + encodeSiteIDPart(eu)
}

func parseItemRef(raw string) *itemRef {
	id := strings.TrimSpace(raw)
	if id == "" {
		return nil
	}
	if strings.HasPrefix(id, "view_") {
		return &itemRef{Kind: "section", Source: "section", RawID: id}
	}
	if strings.HasPrefix(id, "tmdb_movie_") {
		n, err := strconv.Atoi(strings.TrimSpace(strings.TrimPrefix(id, "tmdb_movie_")))
		if err != nil || n <= 0 {
			return nil
		}
		return &itemRef{Kind: "item", SubKind: "movie", MediaType: "movie", Source: "tmdb", RawID: id, NumericID: n}
	}
	if strings.HasPrefix(id, "tmdb_tv_") {
		rest := strings.TrimSpace(strings.TrimPrefix(id, "tmdb_tv_"))
		parts := strings.Split(rest, "_")
		if len(parts) == 0 {
			return nil
		}
		n, err := strconv.Atoi(strings.TrimSpace(parts[0]))
		if err != nil || n <= 0 {
			return nil
		}
		ref := &itemRef{Kind: "item", SubKind: "series", MediaType: "tv", Source: "tmdb", RawID: id, NumericID: n}
		if len(parts) >= 2 && strings.EqualFold(strings.TrimSpace(parts[1]), "settings") {
			ref.SubKind = "season"
			ref.Variant = "settings"
			if len(parts) == 2 {
				return ref
			}
			if len(parts) == 3 && strings.HasPrefix(strings.ToLower(strings.TrimSpace(parts[2])), "e") {
				episode, err := strconv.Atoi(strings.TrimPrefix(strings.ToLower(strings.TrimSpace(parts[2])), "e"))
				if err != nil || episode <= 0 {
					return nil
				}
				ref.SubKind = "episode"
				ref.Episode = episode
				return ref
			}
			return nil
		}
		if len(parts) >= 2 && strings.HasPrefix(strings.ToLower(strings.TrimSpace(parts[1])), "s") {
			season, err := strconv.Atoi(strings.TrimPrefix(strings.ToLower(strings.TrimSpace(parts[1])), "s"))
			if err != nil || season < 0 {
				return nil
			}
			ref.SubKind = "season"
			ref.Pan = season
			if len(parts) >= 3 && strings.HasPrefix(strings.ToLower(strings.TrimSpace(parts[2])), "e") {
				episode, err := strconv.Atoi(strings.TrimPrefix(strings.ToLower(strings.TrimSpace(parts[2])), "e"))
				if err != nil || episode <= 0 {
					return nil
				}
				ref.SubKind = "episode"
				ref.Episode = episode
			}
		}
		return ref
	}
	return nil
}

func parseItemRefAny(raw string) *itemRef {
	if ref := parseItemRef(raw); ref != nil {
		return ref
	}
	// site_* ids are part of the main browsing chain. They are fully reversible
	// and intentionally do not depend on database mappings or old alternate formats.
	if siteKey, siteDetail, ok := parseSiteSeriesID(raw); ok {
		return &itemRef{Kind: "item", SubKind: "series", MediaType: "tv", Source: "site", RawID: strings.TrimSpace(raw), SiteKey: siteKey, SiteDetail: siteDetail}
	}
	if siteKey, siteDetail, pan, ok := parseSiteSeasonID(raw); ok {
		return &itemRef{Kind: "item", SubKind: "season", MediaType: "tv", Source: "site", RawID: strings.TrimSpace(raw), SiteKey: siteKey, SiteDetail: siteDetail, Pan: pan}
	}
	if siteKey, siteDetail, pan, ep, title, flag, episodeURL, ok := parseSiteEpisodeID(raw); ok {
		return &itemRef{Kind: "item", SubKind: "episode", MediaType: "tv", Source: "site", RawID: strings.TrimSpace(raw), SiteKey: siteKey, SiteDetail: siteDetail, SiteTitle: title, SitePlayFlag: flag, SiteEpisodeURL: episodeURL, Pan: pan, Episode: ep}
	}
	return nil
}

func parseSiteSeriesID(id string) (siteKey string, siteDetail string, ok bool) {
	raw := strings.TrimSpace(id)
	if !strings.HasPrefix(raw, siteSeriesPrefix) {
		return "", "", false
	}
	rest := strings.TrimSpace(strings.TrimPrefix(raw, siteSeriesPrefix))
	parts := strings.SplitN(rest, ".", 2)
	if len(parts) != 2 || strings.TrimSpace(parts[0]) == "" || strings.TrimSpace(parts[1]) == "" {
		return "", "", false
	}
	sk, ok1 := decodeSiteIDPart(parts[0])
	sd, ok2 := decodeSiteIDPart(parts[1])
	if !ok1 || !ok2 || strings.TrimSpace(sk) == "" || strings.TrimSpace(sd) == "" {
		return "", "", false
	}
	return strings.TrimSpace(sk), strings.TrimSpace(sd), true
}

func parseSiteSeasonID(id string) (siteKey string, siteDetail string, pan int, ok bool) {
	raw := strings.TrimSpace(id)
	if !strings.HasPrefix(raw, siteSeasonPrefix) {
		return "", "", 0, false
	}
	rest := strings.TrimSpace(strings.TrimPrefix(raw, siteSeasonPrefix))
	parts := strings.Split(rest, ".")
	if len(parts) != 3 {
		return "", "", 0, false
	}
	p, err := strconv.Atoi(strings.TrimSpace(parts[2]))
	if err != nil || p <= 0 {
		return "", "", 0, false
	}
	sk, ok1 := decodeSiteIDPart(parts[0])
	sd, ok2 := decodeSiteIDPart(parts[1])
	if !ok1 || !ok2 {
		return "", "", 0, false
	}
	if strings.TrimSpace(sk) == "" || strings.TrimSpace(sd) == "" {
		return "", "", 0, false
	}
	return strings.TrimSpace(sk), strings.TrimSpace(sd), p, true
}

func parseSiteEpisodeID(id string) (siteKey string, siteDetail string, pan int, ep int, title string, flag string, episodeURL string, ok bool) {
	raw := strings.TrimSpace(id)
	if !strings.HasPrefix(raw, siteEpisodePrefix) {
		return "", "", 0, 0, "", "", "", false
	}
	rest := strings.TrimSpace(strings.TrimPrefix(raw, siteEpisodePrefix))
	parts := strings.Split(rest, ".")
	if len(parts) != 7 {
		return "", "", 0, 0, "", "", "", false
	}
	panNo, err1 := strconv.Atoi(strings.TrimSpace(parts[2]))
	epNo, err2 := strconv.Atoi(strings.TrimSpace(parts[3]))
	if err1 != nil || err2 != nil || panNo <= 0 || epNo <= 0 {
		return "", "", 0, 0, "", "", "", false
	}
	sk, ok1 := decodeSiteIDPart(parts[0])
	sd, ok2 := decodeSiteIDPart(parts[1])
	tt, ok3 := decodeSiteIDPart(parts[4])
	fg, ok4 := decodeSiteIDPartAllowEmpty(parts[5])
	eu, ok5 := decodeSiteIDPart(parts[6])
	if !ok1 || !ok2 || !ok3 || !ok4 || !ok5 {
		return "", "", 0, 0, "", "", "", false
	}
	if strings.TrimSpace(sk) == "" || strings.TrimSpace(sd) == "" || strings.TrimSpace(tt) == "" || strings.TrimSpace(eu) == "" {
		return "", "", 0, 0, "", "", "", false
	}
	return strings.TrimSpace(sk), strings.TrimSpace(sd), panNo, epNo, strings.TrimSpace(tt), strings.TrimSpace(fg), strings.TrimSpace(eu), true
}

func encodeSiteIDPart(raw string) string {
	return base64.RawURLEncoding.EncodeToString([]byte(strings.TrimSpace(raw)))
}

func decodeSiteIDPart(raw string) (string, bool) {
	bs, err := base64.RawURLEncoding.DecodeString(strings.TrimSpace(raw))
	if err != nil {
		return "", false
	}
	out := strings.TrimSpace(string(bs))
	if out == "" {
		return "", false
	}
	return out, true
}

func decodeSiteIDPartAllowEmpty(raw string) (string, bool) {
	bs, err := base64.RawURLEncoding.DecodeString(strings.TrimSpace(raw))
	if err != nil {
		return "", false
	}
	return strings.TrimSpace(string(bs)), true
}
