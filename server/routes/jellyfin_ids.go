package routes

import (
	"fmt"
	"strconv"
	"strings"
)

type jellyfinItemID struct {
	Source   string // "tmdb" | "douban"
	Kind     string // "tv" | "movie" | "person"
	TMDBID   int
	DoubanID string
	Season   int
	Episode  int
	SubKind  string // "series" | "season" | "episode" | "movie" | "person"
}

func jellyfinBuildSeriesID(tmdbID int) string {
	return fmt.Sprintf("tmdb_tv_%d", tmdbID)
}

func jellyfinBuildSeasonID(tmdbID int, season int) string {
	return fmt.Sprintf("tmdb_tv_%d_s%02d", tmdbID, season)
}

func jellyfinBuildEpisodeID(tmdbID int, season int, episode int) string {
	return fmt.Sprintf("tmdb_tv_%d_s%02d_e%03d", tmdbID, season, episode)
}

func jellyfinBuildMovieID(tmdbID int) string {
	return fmt.Sprintf("tmdb_movie_%d", tmdbID)
}

func jellyfinBuildPersonID(tmdbPersonID int) string {
	return fmt.Sprintf("tmdb_person_%d", tmdbPersonID)
}

func jellyfinBuildDoubanSeriesID(doubanID string) string {
	return fmt.Sprintf("douban_tv_%s", strings.TrimSpace(doubanID))
}

func jellyfinBuildDoubanMovieID(doubanID string) string {
	return fmt.Sprintf("douban_movie_%s", strings.TrimSpace(doubanID))
}

func jellyfinParseItemID(id string) (*jellyfinItemID, bool) {
	raw := strings.TrimSpace(id)
	if raw == "" {
		return nil, false
	}
	if strings.HasPrefix(raw, "tmdb_person_") {
		n, err := strconv.Atoi(strings.TrimPrefix(raw, "tmdb_person_"))
		if err != nil || n <= 0 {
			return nil, false
		}
		return &jellyfinItemID{Source: "tmdb", Kind: "person", TMDBID: n, SubKind: "person"}, true
	}
	if strings.HasPrefix(raw, "tmdb_movie_") {
		n, err := strconv.Atoi(strings.TrimPrefix(raw, "tmdb_movie_"))
		if err != nil || n <= 0 {
			return nil, false
		}
		return &jellyfinItemID{Source: "tmdb", Kind: "movie", TMDBID: n, SubKind: "movie"}, true
	}
	if strings.HasPrefix(raw, "tmdb_tv_") {
		rest := strings.TrimPrefix(raw, "tmdb_tv_")
		// series only: tmdb_tv_<id>
		if !strings.Contains(rest, "_s") {
			n, err := strconv.Atoi(rest)
			if err != nil || n <= 0 {
				return nil, false
			}
			return &jellyfinItemID{Source: "tmdb", Kind: "tv", TMDBID: n, SubKind: "series"}, true
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
			return &jellyfinItemID{Source: "tmdb", Kind: "tv", TMDBID: n, Season: sn, SubKind: "season"}, true
		}
		epPart := parts[2]
		if !strings.HasPrefix(epPart, "e") {
			return nil, false
		}
		en, err := strconv.Atoi(strings.TrimPrefix(epPart, "e"))
		if err != nil || en <= 0 {
			return nil, false
		}
		return &jellyfinItemID{Source: "tmdb", Kind: "tv", TMDBID: n, Season: sn, Episode: en, SubKind: "episode"}, true
	}
	if strings.HasPrefix(raw, "douban_movie_") {
		did := strings.TrimSpace(strings.TrimPrefix(raw, "douban_movie_"))
		if did == "" {
			return nil, false
		}
		return &jellyfinItemID{Source: "douban", Kind: "movie", DoubanID: did, SubKind: "movie"}, true
	}
	if strings.HasPrefix(raw, "douban_tv_") {
		did := strings.TrimSpace(strings.TrimPrefix(raw, "douban_tv_"))
		if did == "" {
			return nil, false
		}
		return &jellyfinItemID{Source: "douban", Kind: "tv", DoubanID: did, SubKind: "series"}, true
	}
	return nil, false
}
