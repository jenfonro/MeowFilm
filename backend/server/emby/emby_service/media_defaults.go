package emby_service

import (
	"strconv"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
)

func EmptyStringMap() map[string]string {
	return map[string]string{}
}

func EmptyRequiredHTTPHeaders() map[string]string {
	return EmptyStringMap()
}

func EmptyAnyMap() map[string]any {
	return map[string]any{}
}

func ProviderIDsFromTMDBString(tmdbID int) map[string]string {
	if tmdbID <= 0 {
		return EmptyStringMap()
	}
	return map[string]string{"Tmdb": strconv.Itoa(tmdbID)}
}

func ProviderIDsFromTMDBAny(tmdbID int) map[string]any {
	if tmdbID <= 0 {
		return EmptyAnyMap()
	}
	return map[string]any{"Tmdb": strconv.Itoa(tmdbID)}
}

func GenresAndItemsFromDetail(detail *db.TMDBCachedDetail, mediaType string) ([]string, []NamedIDDTO) {
	if detail == nil {
		return EmptyStrings(), EmptyNamedIDs()
	}
	genres := GenreNamesFromIDs(mediaType, detail.GenreIDs)
	return genres, NamedGenreItems(genres)
}

func EmptyGenresAndItems() ([]string, []NamedIDDTO) {
	return EmptyStrings(), EmptyNamedIDs()
}

func SortNameOrName(name string) string {
	return strings.TrimSpace(name)
}
