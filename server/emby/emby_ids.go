package emby

import (
	"fmt"
	"strconv"
	"strings"
)

type embyItemID struct {
	Source   string // "tmdb" | "douban"
	Kind     string // "tv" | "movie" | "person"
	TMDBID   int
	DoubanID string
	Season   int
	Episode  int
	SubKind  string // "series" | "season" | "episode" | "movie" | "person"
}

func embyBuildSeriesID(tmdbID int) string {
	return fmt.Sprintf("tmdb_tv_%d", tmdbID)
}

func embyBuildSeasonID(tmdbID int, season int) string {
	return fmt.Sprintf("tmdb_tv_%d_s%02d", tmdbID, season)
}

func embyBuildEpisodeID(tmdbID int, season int, episode int) string {
	return fmt.Sprintf("tmdb_tv_%d_s%02d_e%03d", tmdbID, season, episode)
}

func embyBuildMovieID(tmdbID int) string {
	return fmt.Sprintf("tmdb_movie_%d", tmdbID)
}

// BuildEpisodeID exposes the canonical MeowFilm Emby/Jellyfin episode item id format for non-emby modules.
func BuildEpisodeID(tmdbID int, season int, episode int) string {
	return embyBuildEpisodeID(tmdbID, season, episode)
}

// BuildMovieID exposes the canonical MeowFilm Emby/Jellyfin movie item id format for non-emby modules.
func BuildMovieID(tmdbID int) string {
	return embyBuildMovieID(tmdbID)
}

func embyBuildPersonID(tmdbPersonID int) string {
	return fmt.Sprintf("tmdb_person_%d", tmdbPersonID)
}

func embyBuildDoubanSeriesID(doubanID string) string {
	return fmt.Sprintf("douban_tv_%s", strings.TrimSpace(doubanID))
}

func embyBuildDoubanMovieID(doubanID string) string {
	return fmt.Sprintf("douban_movie_%s", strings.TrimSpace(doubanID))
}

func embyParseItemID(id string) (*embyItemID, bool) {
	raw := strings.TrimSpace(id)
	if raw == "" {
		return nil, false
	}
	if strings.HasPrefix(raw, "tmdb_person_") {
		n, err := strconv.Atoi(strings.TrimPrefix(raw, "tmdb_person_"))
		if err != nil || n <= 0 {
			return nil, false
		}
		return &embyItemID{Source: "tmdb", Kind: "person", TMDBID: n, SubKind: "person"}, true
	}
	if strings.HasPrefix(raw, "tmdb_movie_") {
		n, err := strconv.Atoi(strings.TrimPrefix(raw, "tmdb_movie_"))
		if err != nil || n <= 0 {
			return nil, false
		}
		return &embyItemID{Source: "tmdb", Kind: "movie", TMDBID: n, SubKind: "movie"}, true
	}
	if strings.HasPrefix(raw, "tmdb_tv_") {
		rest := strings.TrimPrefix(raw, "tmdb_tv_")
		// series only: tmdb_tv_<id>
		if !strings.Contains(rest, "_s") {
			n, err := strconv.Atoi(rest)
			if err != nil || n <= 0 {
				return nil, false
			}
			return &embyItemID{Source: "tmdb", Kind: "tv", TMDBID: n, SubKind: "series"}, true
		}
		// season: tmdb_tv_<id>_s01
		// episode: tmdb_tv_<id>_s01_e002
		parts := strings.Split(rest, "_")
		if len(parts) < 2 {
			return nil, false
		}
		n, err := strconv.Atoi(parts[0])
		if err != nil || n <= 0 {
			return nil, false
		}
		seasonPart := parts[1]
		if !strings.HasPrefix(seasonPart, "s") {
			return nil, false
		}
		sn, err := strconv.Atoi(strings.TrimPrefix(seasonPart, "s"))
		if err != nil || sn < 0 {
			return nil, false
		}
		if len(parts) == 2 {
			return &embyItemID{Source: "tmdb", Kind: "tv", TMDBID: n, Season: sn, SubKind: "season"}, true
		}
		epPart := parts[2]
		if !strings.HasPrefix(epPart, "e") {
			return nil, false
		}
		en, err := strconv.Atoi(strings.TrimPrefix(epPart, "e"))
		if err != nil || en <= 0 {
			return nil, false
		}
		return &embyItemID{Source: "tmdb", Kind: "tv", TMDBID: n, Season: sn, Episode: en, SubKind: "episode"}, true
	}
	if strings.HasPrefix(raw, "douban_movie_") {
		did := strings.TrimSpace(strings.TrimPrefix(raw, "douban_movie_"))
		if did == "" {
			return nil, false
		}
		return &embyItemID{Source: "douban", Kind: "movie", DoubanID: did, SubKind: "movie"}, true
	}
	if strings.HasPrefix(raw, "douban_tv_") {
		did := strings.TrimSpace(strings.TrimPrefix(raw, "douban_tv_"))
		if did == "" {
			return nil, false
		}
		return &embyItemID{Source: "douban", Kind: "tv", DoubanID: did, SubKind: "series"}, true
	}
	return nil, false
}
