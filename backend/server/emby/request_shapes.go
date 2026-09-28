package emby

import (
	"net/http"
	"strings"

	"github.com/jenfonro/meowfilm/server/emby/emby_service"
)

type ItemQueryShape string
type SeasonQueryShape string
type EpisodeQueryShape string
type LatestQueryShape string

const (
	ItemQueryShapeTopLevel     ItemQueryShape = "top_level"
	ItemQueryShapeParentList   ItemQueryShape = "parent_list"
	ItemQueryShapeSearch       ItemQueryShape = "search"
	ItemQueryShapeUnknown      ItemQueryShape = "unknown"
	SeasonQueryShapeBasic      SeasonQueryShape = "basic"
	SeasonQueryShapeGenres     SeasonQueryShape = "genres_parent"
	SeasonQueryShapeRichDetail SeasonQueryShape = "rich_detail"
	EpisodeQueryShapeBasic     EpisodeQueryShape = "basic"
	EpisodeQueryShapeInfuse    EpisodeQueryShape = "infuse_media"
	EpisodeQueryShapeRich      EpisodeQueryShape = "rich_detail"
	LatestQueryShapeFull       LatestQueryShape = "full"
	LatestQueryShapeThin       LatestQueryShape = "thin"
)

func ResolveItemQueryShape(r *http.Request) ItemQueryShape {
	if r == nil || r.URL == nil {
		return ItemQueryShapeUnknown
	}
	query := r.URL.Query()
	parentID := strings.TrimSpace(query.Get("ParentId"))
	searchTerm := strings.TrimSpace(query.Get("SearchTerm"))
	if searchTerm != "" {
		return ItemQueryShapeSearch
	}
	if parentID != "" {
		return ItemQueryShapeParentList
	}
	return ItemQueryShapeTopLevel
}

func ResolveSeasonQueryShape(r *http.Request) SeasonQueryShape {
	fields := strings.ToLower(strings.TrimSpace(r.URL.Query().Get("Fields")))
	switch {
	case fields == "":
		return SeasonQueryShapeBasic
	case strings.Contains(fields, "genres") || strings.Contains(fields, "parentid"):
		return SeasonQueryShapeGenres
	default:
		return SeasonQueryShapeRichDetail
	}
}

func ResolveEpisodeQueryShape(r *http.Request) EpisodeQueryShape {
	fields := strings.ToLower(strings.TrimSpace(r.URL.Query().Get("Fields")))
	switch {
	case fields == "":
		return EpisodeQueryShapeBasic
	case strings.Contains(fields, "etag"):
		return EpisodeQueryShapeInfuse
	default:
		return EpisodeQueryShapeRich
	}
}

func ResolveLatestQueryShape(r *http.Request) LatestQueryShape {
	fields := strings.ToLower(strings.TrimSpace(r.URL.Query().Get("Fields")))
	if strings.Contains(fields, "basicsyncinfo") {
		return LatestQueryShapeThin
	}
	return LatestQueryShapeFull
}

func ResolveSearchQueryShape(r *http.Request) emby_service.SearchQueryShape {
	if r == nil || r.URL == nil {
		return emby_service.SearchQueryShapeLenna
	}
	fields := strings.ToLower(strings.TrimSpace(r.URL.Query().Get("Fields")))
	switch {
	case strings.Contains(fields, "etag"):
		return emby_service.SearchQueryShapeInfuse
	case strings.Contains(fields, "productionlocations") || strings.Contains(fields, "overview"):
		return emby_service.SearchQueryShapeFamily
	default:
		return emby_service.SearchQueryShapeLenna
	}
}

func WantsFamilySeasonPayload(r *http.Request) bool {
	fields := strings.ToLower(strings.TrimSpace(r.URL.Query().Get("Fields")))
	return strings.Contains(fields, "people") || strings.Contains(fields, "childcount") || strings.Contains(fields, "premieredate")
}

func WantsFamilyEpisodePayload(r *http.Request) bool {
	fields := strings.ToLower(strings.TrimSpace(r.URL.Query().Get("Fields")))
	return strings.Contains(fields, "chapters")
}
