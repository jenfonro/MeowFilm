package emby_service

import (
	"fmt"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
)

func BuildExternalURLs(mediaType string, tmdbID int) []ExternalURLDTO {
	typ := strings.ToLower(strings.TrimSpace(mediaType))
	if tmdbID <= 0 || (typ != "movie" && typ != "tv") {
		return []ExternalURLDTO{}
	}
	return []ExternalURLDTO{
		{
			Name: "TheMovieDb",
			URL:  fmt.Sprintf("https://www.themoviedb.org/%s/%d", map[string]string{"movie": "movie", "tv": "tv"}[typ], tmdbID),
		},
		{
			Name: "Trakt",
			URL:  fmt.Sprintf("https://trakt.tv/search/tmdb/%d?id_type=%s", tmdbID, map[string]string{"movie": "movie", "tv": "show"}[typ]),
		},
	}
}

func BuildSyntheticChapters(runtime int64) []DetailChapterDTO {
	if runtime <= 0 {
		return EmptyDetailChapters()
	}
	const chapterTick = int64(5 * 60 * 10_000_000)
	out := EmptyDetailChapters()
	for start, idx := int64(0), 0; start < runtime; start, idx = start+chapterTick, idx+1 {
		out = append(out, DetailChapterDTO{
			StartPositionTicks: start,
			Name:               fmt.Sprintf("Chapter %d", idx+1),
			MarkerType:         "Chapter",
			ChapterIndex:       idx,
		})
	}
	return out
}

func EmptyDetailChapters() []DetailChapterDTO {
	return []DetailChapterDTO{}
}

func EmptyDetailMediaSources() []DetailMediaSourceDTO {
	return []DetailMediaSourceDTO{}
}

func EmptyResumeMediaSources() []ResumeMediaSourceDTO {
	return []ResumeMediaSourceDTO{}
}

func RuntimeTicksFromEpisode(ep db.TMDBCachedSeasonEpisode) int64 {
	if ep.Runtime <= 0 {
		return 0
	}
	return int64(ep.Runtime) * 60 * 10_000_000
}

func ResumeRuntime(snap db.PlayHistorySnapshot) int64 {
	runtime := snap.Runtime
	pos := maxInt64(0, snap.Pos)
	if runtime <= 0 && pos > 0 {
		runtime = pos + int64(60*10_000_000)
	}
	return runtime
}

func PlayedPercentage(pos int64, runtime int64) float64 {
	if runtime <= 0 || pos <= 0 {
		return 0
	}
	return (float64(pos) / float64(runtime)) * 100
}

func PlayedPercentagePtr(pos int64, runtime int64) *float64 {
	pct := PlayedPercentage(pos, runtime)
	if pct <= 0 {
		return nil
	}
	return &pct
}
