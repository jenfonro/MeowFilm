package emby

import (
	"strconv"
	"strings"
)

const (
	embySiteSeriesV2Prefix  = "sitev_"
	embySiteSeasonV2Prefix  = "sitepan_"
	embySiteEpisodeV2Prefix = "siteep_"
)

func embyBuildSiteSeriesIDV2(siteVideoID int64) string {
	if siteVideoID <= 0 {
		return ""
	}
	return embySiteSeriesV2Prefix + strconv.FormatInt(siteVideoID, 10)
}

func embyParseSiteSeriesIDV2(id string) (siteVideoID int64, ok bool) {
	raw := strings.TrimSpace(id)
	if !strings.HasPrefix(raw, embySiteSeriesV2Prefix) {
		return 0, false
	}
	n := strings.TrimSpace(strings.TrimPrefix(raw, embySiteSeriesV2Prefix))
	if n == "" {
		return 0, false
	}
	v, err := strconv.ParseInt(n, 10, 64)
	if err != nil || v <= 0 {
		return 0, false
	}
	return v, true
}

func embyBuildSiteSeasonIDV2(siteVideoID int64, pan int) string {
	if siteVideoID <= 0 || pan <= 0 {
		return ""
	}
	return embySiteSeasonV2Prefix + strconv.FormatInt(siteVideoID, 10) + "_" + strconv.Itoa(pan)
}

func embyParseSiteSeasonIDV2(id string) (siteVideoID int64, pan int, ok bool) {
	raw := strings.TrimSpace(id)
	if !strings.HasPrefix(raw, embySiteSeasonV2Prefix) {
		return 0, 0, false
	}
	s := strings.TrimSpace(strings.TrimPrefix(raw, embySiteSeasonV2Prefix))
	parts := strings.Split(s, "_")
	if len(parts) != 2 {
		return 0, 0, false
	}
	vid, err := strconv.ParseInt(strings.TrimSpace(parts[0]), 10, 64)
	if err != nil || vid <= 0 {
		return 0, 0, false
	}
	p, err := strconv.Atoi(strings.TrimSpace(parts[1]))
	if err != nil || p <= 0 {
		return 0, 0, false
	}
	return vid, p, true
}

func embyBuildSiteEpisodeIDV2(siteVideoID int64, pan int, ep int) string {
	if siteVideoID <= 0 || pan <= 0 || ep <= 0 {
		return ""
	}
	return embySiteEpisodeV2Prefix + strconv.FormatInt(siteVideoID, 10) + "_" + strconv.Itoa(pan) + "_" + strconv.Itoa(ep)
}

func embyParseSiteEpisodeIDV2(id string) (siteVideoID int64, pan int, ep int, ok bool) {
	raw := strings.TrimSpace(id)
	if !strings.HasPrefix(raw, embySiteEpisodeV2Prefix) {
		return 0, 0, 0, false
	}
	s := strings.TrimSpace(strings.TrimPrefix(raw, embySiteEpisodeV2Prefix))
	parts := strings.Split(s, "_")
	if len(parts) != 3 {
		return 0, 0, 0, false
	}
	vid, err := strconv.ParseInt(strings.TrimSpace(parts[0]), 10, 64)
	if err != nil || vid <= 0 {
		return 0, 0, 0, false
	}
	panV, err := strconv.Atoi(strings.TrimSpace(parts[1]))
	if err != nil || panV <= 0 {
		return 0, 0, 0, false
	}
	epV, err := strconv.Atoi(strings.TrimSpace(parts[2]))
	if err != nil || epV <= 0 {
		return 0, 0, 0, false
	}
	return vid, panV, epV, true
}

