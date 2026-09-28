package emby_service

import (
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
	metadata_tmdb "github.com/jenfonro/meowfilm/server/metadata/tmdb"
)

type latestMovieMetadata struct {
	PosterURL       string
	DateCreated     string
	Overview        string
	Genres          []string
	GenreItems      []NamedIDDTO
	ProviderIDs     map[string]any
	PremiereDate    string
	ProductionYear  int
	CommunityRating float64
	BackdropTags    []string
}

type latestTVMetadata struct {
	PosterURL       string
	DateCreated     string
	Overview        string
	Genres          []string
	GenreItems      []NamedIDDTO
	ProviderIDs     map[string]any
	PremiereDate    string
	ProductionYear  int
	CommunityRating float64
	Status          string
	RecursiveCount  int
	BackdropTags    []string
}

func EmptyLatestProviderIDs() map[string]any {
	return EmptyAnyMap()
}

func EmptyLatestGenreItems() []NamedIDDTO {
	return EmptyNamedIDs()
}

func NormalizeMovieLatestSource(row movieLatestSource) movieLatestSource {
	if strings.TrimSpace(row.DateCreated) == "" {
		row.DateCreated = EmbyZeroTimeString()
	}
	row.SortName = SortNameOrName(firstNonEmptyLatest(row.SortName, row.Name))
	if row.MediaSources == nil {
		row.MediaSources = EmptyResumeMediaSources()
	}
	if row.Genres == nil {
		row.Genres = EmptyStrings()
	}
	if row.ProviderIDs == nil {
		row.ProviderIDs = EmptyLatestProviderIDs()
	}
	if row.GenreItems == nil {
		row.GenreItems = EmptyLatestGenreItems()
	}
	if row.BackdropTags == nil {
		row.BackdropTags = EmptyStrings()
	}
	return row
}

func NormalizeTVLatestSource(row tvLatestSource) tvLatestSource {
	if strings.TrimSpace(row.DateCreated) == "" {
		row.DateCreated = EmbyZeroTimeString()
	}
	row.SortName = SortNameOrName(firstNonEmptyLatest(row.SortName, row.Name))
	if row.Genres == nil {
		row.Genres = EmptyStrings()
	}
	if row.ProviderIDs == nil {
		row.ProviderIDs = EmptyLatestProviderIDs()
	}
	if row.GenreItems == nil {
		row.GenreItems = EmptyLatestGenreItems()
	}
	if row.BackdropTags == nil {
		row.BackdropTags = EmptyStrings()
	}
	return row
}

func LatestMovieMetadataFromTMDB(database *db.DB, tmdbID int, fallbackPoster string) latestMovieMetadata {
	out := latestMovieMetadata{
		PosterURL:       rewriteRedirectImageURL(database, fallbackPoster),
		DateCreated:     EmbyZeroTimeString(),
		Overview:        "",
		Genres:          EmptyStrings(),
		GenreItems:      EmptyLatestGenreItems(),
		ProviderIDs:     ProviderIDsFromTMDBAny(tmdbID),
		PremiereDate:    "",
		ProductionYear:  0,
		CommunityRating: 0,
		BackdropTags:    EmptyStrings(),
	}
	if tmdbID <= 0 || database == nil {
		return out
	}
	if cachedDetail, err := database.ReadTMDBCachedDetail("movie", tmdbID, "zh-CN"); err == nil && cachedDetail != nil {
		if target := rewriteRedirectImageURL(database, strings.TrimSpace(cachedDetail.PosterPath)); strings.TrimSpace(target) != "" {
			out.PosterURL = target
		}
		out.PremiereDate = preciseDateString(cachedDetail.Release)
		out.ProductionYear = metadata_tmdb.ParseYearFromDate(cachedDetail.Release)
		out.Overview = strings.TrimSpace(cachedDetail.Overview)
		out.Genres, out.GenreItems = GenresAndItemsFromDetail(cachedDetail, "movie")
		out.BackdropTags = backdropTagsFromAsset(cachedDetail.Backdrop)
		if cachedDetail.LastRefreshAt > 0 {
			out.DateCreated, _ = ProtocolDatePairFromUnix(cachedDetail.LastRefreshAt)
		}
		if cachedDetail.VoteAverage > 0 {
			out.CommunityRating = cachedDetail.VoteAverage
		}
	}
	return out
}

func LatestTVMetadataFromTMDB(database *db.DB, tmdbID int, fallbackPoster string) latestTVMetadata {
	out := latestTVMetadata{
		PosterURL:       rewriteRedirectImageURL(database, fallbackPoster),
		DateCreated:     EmbyZeroTimeString(),
		Overview:        "",
		Genres:          EmptyStrings(),
		GenreItems:      EmptyLatestGenreItems(),
		ProviderIDs:     ProviderIDsFromTMDBAny(tmdbID),
		PremiereDate:    "",
		ProductionYear:  0,
		CommunityRating: 0,
		Status:          "",
		RecursiveCount:  0,
		BackdropTags:    EmptyStrings(),
	}
	if tmdbID <= 0 || database == nil {
		return out
	}
	if cachedDetail, err := database.ReadTMDBCachedDetail("tv", tmdbID, "zh-CN"); err == nil && cachedDetail != nil {
		if target := rewriteRedirectImageURL(database, strings.TrimSpace(cachedDetail.PosterPath)); strings.TrimSpace(target) != "" {
			out.PosterURL = target
		}
		out.PremiereDate = preciseDateString(cachedDetail.FirstAir)
		out.ProductionYear = metadata_tmdb.ParseYearFromDate(cachedDetail.FirstAir)
		out.Status = strings.TrimSpace(cachedDetail.Status)
		out.Overview = strings.TrimSpace(cachedDetail.Overview)
		out.Genres, out.GenreItems = GenresAndItemsFromDetail(cachedDetail, "tv")
		out.BackdropTags = backdropTagsFromAsset(cachedDetail.Backdrop)
		out.RecursiveCount = cachedDetail.EpisodeCount
		if cachedDetail.VoteAverage > 0 {
			out.CommunityRating = cachedDetail.VoteAverage
		}
		if cachedDetail.LastRefreshAt > 0 {
			out.DateCreated, _ = ProtocolDatePairFromUnix(cachedDetail.LastRefreshAt)
		}
	}
	return out
}

func LatestPathAndMediaSources(itemID string, row *db.PlayHistoryRow) (container string, path string, mediaSources []ResumeMediaSourceDTO) {
	container = ""
	path = ""
	mediaSources = EmptyResumeMediaSources()
	if row == nil {
		return
	}
	mediaSources = resumeMediaSources(StableMD5Hex(itemID+"|media"), path, container, *row)
	return
}

func ResolveLatestTVTMDB(database *db.DB, title string, year int) (int, string, error) {
	return resolveLatestTMDB(database, "tv", title, year)
}

func ResolveLatestMovieTMDB(database *db.DB, title string, year int) (int, string, error) {
	return resolveLatestTMDB(database, "movie", title, year)
}

func resolveLatestTMDB(database *db.DB, mediaType string, title string, year int) (int, string, error) {
	candidates := metadata_tmdb.NormalizeTitleCandidates(mediaType, title)
	tmdbID, matched, err := metadata_tmdb.ResolveByTitlesFromCache(database, mediaType, candidates, year, "zh-CN")
	if err != nil || tmdbID > 0 {
		return tmdbID, matched, err
	}
	if year > 0 {
		tmdbID, matched, err = metadata_tmdb.ResolveByTitlesFromCache(database, mediaType, candidates, 0, "zh-CN")
		if err != nil || tmdbID > 0 {
			return tmdbID, matched, err
		}
	}
	for _, candidate := range candidates {
		if _, err := metadata_tmdb.SearchMulti(database, candidate); err != nil {
			return 0, candidate, err
		}
	}
	tmdbID, matched, err = metadata_tmdb.ResolveByTitlesFromCache(database, mediaType, candidates, year, "zh-CN")
	if err != nil || tmdbID > 0 {
		return tmdbID, matched, err
	}
	if year > 0 {
		return metadata_tmdb.ResolveByTitlesFromCache(database, mediaType, candidates, 0, "zh-CN")
	}
	return 0, "", nil
}

func firstNonEmptyLatest(values ...string) string {
	for _, v := range values {
		if strings.TrimSpace(v) != "" {
			return strings.TrimSpace(v)
		}
	}
	return ""
}
