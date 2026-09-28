package tvmeta

import (
	"encoding/json"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
	metadata_tmdb "github.com/jenfonro/meowfilm/server/metadata/tmdb"
)

type SeasonCount struct {
	SeasonNumber int `json:"season_number"`
	EpisodeCount int `json:"episode_count"`
}

type Response struct {
	TMDBID                  int           `json:"tmdb_id"`
	SeasonCount             int           `json:"season_count"`
	TMDBTotalEpisodeCount   int           `json:"tmdb_total_episode_count"`
	DoubanTotalEpisodeCount int           `json:"douban_total_episode_count"`
	DoubanLeads             bool          `json:"douban_leads"`
	TMDBSeasons             []SeasonCount `json:"tmdb_seasons"`
	DoubanSeasons           []SeasonCount `json:"douban_seasons"`
}

func GetTVMeta(database *db.DB, tmdbID int) (*Response, error) {
	if database == nil || tmdbID <= 0 {
		return &Response{}, nil
	}

	tmdbRows, err := database.ReadTMDBTVSeasonCounts(tmdbID)
	if err != nil {
		return nil, err
	}
	if len(tmdbRows) == 0 {
		if _, err := metadata_tmdb.GetRawDetailJSON(database, "tv", tmdbID); err != nil {
			return nil, err
		}
		tmdbRows, err = database.ReadTMDBTVSeasonCounts(tmdbID)
		if err != nil {
			return nil, err
		}
	}
	seasonCount, tmdbTotal, tmdbSeasons := summarizeTMDBRows(tmdbRows)
	title := readTMDBTitle(database, tmdbID)
	if strings.TrimSpace(title) == "" {
		if _, err := metadata_tmdb.GetRawDetailJSON(database, "tv", tmdbID); err != nil {
			return nil, err
		}
		title = readTMDBTitle(database, tmdbID)
	}

	doubanHints, err := getDoubanSeasonHints(database, tmdbID, title, tmdbTotal)
	if err != nil {
		return nil, err
	}
	doubanTotal, doubanSeasons := summarizeDoubanHints(doubanHints)

	return &Response{
		TMDBID:                  tmdbID,
		SeasonCount:             seasonCount,
		TMDBTotalEpisodeCount:   tmdbTotal,
		DoubanTotalEpisodeCount: doubanTotal,
		DoubanLeads:             doubanTotal > tmdbTotal,
		TMDBSeasons:             tmdbSeasons,
		DoubanSeasons:           doubanSeasons,
	}, nil
}

func summarizeTMDBRows(rows []db.TMDBTVSeasonCountRow) (int, int, []SeasonCount) {
	out := make([]SeasonCount, 0, len(rows))
	total := 0
	for _, row := range rows {
		if row.SeasonNumber <= 0 || row.EpisodeCount <= 0 {
			continue
		}
		out = append(out, SeasonCount{
			SeasonNumber: row.SeasonNumber,
			EpisodeCount: row.EpisodeCount,
		})
		total += row.EpisodeCount
	}
	return len(out), total, out
}

func summarizeDoubanHints(hints []db.TMDBSeasonHint) (int, []SeasonCount) {
	out := make([]SeasonCount, 0, len(hints))
	total := 0
	for _, h := range hints {
		if h.SeasonNumber <= 0 || h.EpisodeCount <= 0 {
			continue
		}
		out = append(out, SeasonCount{
			SeasonNumber: h.SeasonNumber,
			EpisodeCount: h.EpisodeCount,
		})
		total += h.EpisodeCount
	}
	return total, out
}

func readTMDBTitle(database *db.DB, tmdbID int) string {
	raw, err := database.ReadTMDBRawDetailJSON("tv", tmdbID)
	if err != nil || strings.TrimSpace(raw) == "" {
		return ""
	}
	var payload struct {
		Name         string `json:"name"`
		OriginalName string `json:"original_name"`
	}
	if err := json.Unmarshal([]byte(raw), &payload); err != nil {
		return ""
	}
	if strings.TrimSpace(payload.Name) != "" {
		return strings.TrimSpace(payload.Name)
	}
	return strings.TrimSpace(payload.OriginalName)
}
