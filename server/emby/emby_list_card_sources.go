package emby

import (
	"strconv"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
)

func embyBuildListCardSourceFromID(database *db.DB, itemID string, parentID string, serverID string) (kind string, in embyListCardInput, ok bool) {
	raw := strings.TrimSpace(itemID)
	if raw == "" {
		return "", embyListCardInput{}, false
	}

	if siteVideoID, ok := embyParseSiteSeriesIDV2(raw); ok {
		if database == nil {
			return "", embyListCardInput{}, false
		}
		sv, err := database.GetSiteVideoByID(siteVideoID)
		if err != nil || sv == nil {
			return "", embyListCardInput{}, false
		}
		title := strings.TrimSpace(sv.Title)
		if title == "" {
			return "", embyListCardInput{}, false
		}
		return "series", embyListCardInput{
			ID:           raw,
			Name:         title,
			ImageTags:    map[string]any{"Primary": "site"},
			BackdropTags: []string{},
			ParentID:     parentID,
			ServerID:     serverID,
		}, true
	}

	parsed, ok := embyParseItemID(raw)
	if !ok || parsed == nil {
		return "", embyListCardInput{}, false
	}
	if err := embyNormalizeParsedToTMDB(database, parsed, true); err != nil {
		return "", embyListCardInput{}, false
	}

	switch parsed.Kind {
	case "movie":
		d, err := embyTMDBGetMovieDetail(database, parsed.TMDBID)
		if err != nil || d == nil {
			return "", embyListCardInput{}, false
		}
		return "movie", embyListCardInput{
			ID:              embyBuildMovieID(parsed.TMDBID),
			Name:            strings.TrimSpace(d.Title),
			ProductionYear:  d.Year,
			ProviderIDs:     map[string]any{"Tmdb": strconv.Itoa(parsed.TMDBID)},
			ImageTags:       map[string]any{"Primary": "tmdb"},
			BackdropTags:    []string{"tmdb"},
			ParentID:        parentID,
			ServerID:        serverID,
		}, true
	case "tv":
		if parsed.SubKind != "series" {
			return "", embyListCardInput{}, false
		}
		d, err := embyTMDBGetTVDetail(database, parsed.TMDBID)
		if err != nil || d == nil {
			return "", embyListCardInput{}, false
		}
		return "series", embyListCardInput{
			ID:              embyBuildSeriesID(parsed.TMDBID),
			Name:            strings.TrimSpace(d.Title),
			ProductionYear:  d.Year,
			ProviderIDs:     map[string]any{"Tmdb": strconv.Itoa(parsed.TMDBID)},
			ImageTags:       map[string]any{"Primary": "tmdb"},
			BackdropTags:    []string{"tmdb"},
			ParentID:        parentID,
			ServerID:        serverID,
		}, true
	default:
		return "", embyListCardInput{}, false
	}
}

func embyBuildListCardFromID(database *db.DB, itemID string, parentID string, serverID string) map[string]any {
	kind, in, ok := embyBuildListCardSourceFromID(database, itemID, parentID, serverID)
	if !ok {
		return nil
	}
	switch kind {
	case "movie":
		return embyBuildMovieListCard(in)
	case "series":
		return embyBuildSeriesListCard(in)
	default:
		return nil
	}
}
