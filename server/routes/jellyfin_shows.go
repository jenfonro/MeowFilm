package routes

import (
	"net/http"
	"sort"
	"strconv"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
)

func handleJellyfinShows(w http.ResponseWriter, r *http.Request, database *db.DB, serverID string, parts []string) {
	// GET /Shows/NextUp?UserId=...
	if len(parts) >= 1 && strings.EqualFold(parts[0], "NextUp") && r.Method == http.MethodGet {
		_, ok := jellyfinRequireUser(w, r, database)
		if !ok {
			return
		}
		// MVP: no real "next up" tracking; return empty.
		writeJSON(w, 200, map[string]any{
			"Items":            []any{},
			"StartIndex":       0,
			"TotalRecordCount": 0,
		})
		return
	}

	// /Shows/{id}/Seasons
	if len(parts) >= 2 && strings.EqualFold(parts[1], "Seasons") && r.Method == http.MethodGet {
		_, ok := jellyfinRequireUser(w, r, database)
		if !ok {
			return
		}
		seriesID := parts[0]
		parsed, ok := jellyfinParseItemID(seriesID)
		if !ok || parsed == nil || parsed.Kind != "tv" || parsed.SubKind != "series" {
			http.NotFound(w, r)
			return
		}
		d, err := jellyfinTMDBGetTVDetail(database, parsed.TMDBID)
		if err != nil || d == nil {
			jellyfinWriteError(w, 502, "TMDB 请求失败")
			return
		}
		seasons := make([]map[string]any, 0, len(d.Seasons))
		for _, s := range d.Seasons {
			if s.Season < 0 || s.EpisodeCount <= 0 {
				continue
			}
			name := "特别篇"
			if s.Season > 0 {
				name = "第" + intToCN(s.Season) + "季"
			}
			seasons = append(seasons, map[string]any{
				"Id":             jellyfinBuildSeasonID(parsed.TMDBID, s.Season),
				"Name":           name,
				"Type":           "Season",
				"IsFolder":       true,
				"SeriesId":       seriesID,
				"ParentId":       seriesID,
				"IndexNumber":    s.Season,
				"ProductionYear": d.Year,
				"ChildCount":     s.EpisodeCount,
				"ImageTags":      map[string]any{"Primary": "tmdb"},
				"UserData":       map[string]any{"Played": false},
			})
		}
		sort.Slice(seasons, func(i, j int) bool {
			return intVal(seasons[i]["IndexNumber"]) < intVal(seasons[j]["IndexNumber"])
		})
		writeJSON(w, 200, map[string]any{
			"Items":            seasons,
			"TotalRecordCount": len(seasons),
		})
		return
	}

	// /Shows/{id}/Episodes?SeasonId=...
	if len(parts) >= 2 && strings.EqualFold(parts[1], "Episodes") && r.Method == http.MethodGet {
		_, ok := jellyfinRequireUser(w, r, database)
		if !ok {
			return
		}
		seriesID := parts[0]
		parsed, ok := jellyfinParseItemID(seriesID)
		if !ok || parsed == nil || parsed.Kind != "tv" || parsed.SubKind != "series" {
			http.NotFound(w, r)
			return
		}
		seasonID := strings.TrimSpace(r.URL.Query().Get("SeasonId"))
		sParsed, ok := jellyfinParseItemID(seasonID)
		if !ok || sParsed == nil || sParsed.Kind != "tv" || sParsed.SubKind != "season" || sParsed.TMDBID != parsed.TMDBID {
			http.NotFound(w, r)
			return
		}
		seasonNo := sParsed.Season
		episodes, err := jellyfinTMDBGetTVSeasonEpisodes(database, parsed.TMDBID, seasonNo)
		if err != nil {
			jellyfinWriteError(w, 502, "TMDB 请求失败")
			return
		}
		out := make([]map[string]any, 0, len(episodes))
		for _, e := range episodes {
			if e.Episode <= 0 {
				continue
			}
			name := strings.TrimSpace(e.Name)
			if name == "" {
				name = "第" + intToCN(e.Episode) + "集"
			}
			out = append(out, map[string]any{
				"Id":                jellyfinBuildEpisodeID(parsed.TMDBID, seasonNo, e.Episode),
				"Name":              name,
				"Overview":          e.Overview,
				"Type":              "Episode",
				"IsFolder":          false,
				"SeriesId":          seriesID,
				"SeasonId":          seasonID,
				"ParentId":          seasonID,
				"IndexNumber":       e.Episode,
				"ParentIndexNumber": seasonNo,
				"ImageTags":         map[string]any{"Primary": "tmdb"},
				"UserData":          map[string]any{"Played": false},
			})
		}
		sort.Slice(out, func(i, j int) bool {
			return intVal(out[i]["IndexNumber"]) < intVal(out[j]["IndexNumber"])
		})
		writeJSON(w, 200, map[string]any{
			"Items":            out,
			"TotalRecordCount": len(out),
		})
		return
	}

	http.NotFound(w, r)
}

func intVal(v any) int {
	switch vv := v.(type) {
	case int:
		return vv
	case int64:
		return int(vv)
	case float64:
		return int(vv)
	default:
		return 0
	}
}

func intToCN(n int) string {
	if n <= 0 {
		return ""
	}
	cn := []string{"", "一", "二", "三", "四", "五", "六", "七", "八", "九", "十"}
	if n <= 10 {
		return cn[n]
	}
	// Keep simple for now; UI label isn't critical for Infuse.
	return strconv.Itoa(n)
}
