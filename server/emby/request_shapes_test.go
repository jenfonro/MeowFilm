package emby

import (
	"net/http/httptest"
	"testing"

	"github.com/jenfonro/meowfilm/server/emby/emby_service"
)

func TestResolveItemQueryShape(t *testing.T) {
	tests := []struct {
		name string
		url  string
		want ItemQueryShape
	}{
		{name: "top level", url: "/Users/1/Items", want: ItemQueryShapeTopLevel},
		{name: "parent list", url: "/Users/1/Items?ParentId=view_tmdb_movies", want: ItemQueryShapeParentList},
		{name: "search wins", url: "/Users/1/Items?ParentId=view_tmdb_movies&SearchTerm=test", want: ItemQueryShapeSearch},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			req := httptest.NewRequest("GET", tt.url, nil)
			if got := ResolveItemQueryShape(req); got != tt.want {
				t.Fatalf("ResolveItemQueryShape() = %q, want %q", got, tt.want)
			}
		})
	}
}

func TestResolveSeasonQueryShape(t *testing.T) {
	tests := []struct {
		name string
		url  string
		want SeasonQueryShape
	}{
		{name: "basic", url: "/Shows/1/Seasons", want: SeasonQueryShapeBasic},
		{name: "genres", url: "/Shows/1/Seasons?Fields=Genres,ParentId", want: SeasonQueryShapeGenres},
		{name: "rich", url: "/Shows/1/Seasons?Fields=BasicSyncInfo,Overview", want: SeasonQueryShapeRichDetail},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			req := httptest.NewRequest("GET", tt.url, nil)
			if got := ResolveSeasonQueryShape(req); got != tt.want {
				t.Fatalf("ResolveSeasonQueryShape() = %q, want %q", got, tt.want)
			}
		})
	}
}

func TestResolveEpisodeQueryShape(t *testing.T) {
	tests := []struct {
		name string
		url  string
		want EpisodeQueryShape
	}{
		{name: "basic", url: "/Shows/1/Episodes", want: EpisodeQueryShapeBasic},
		{name: "infuse", url: "/Shows/1/Episodes?Fields=Etag,AlternateMediaSources", want: EpisodeQueryShapeInfuse},
		{name: "rich", url: "/Shows/1/Episodes?Fields=Overview,Path,MediaSources", want: EpisodeQueryShapeRich},
		{name: "lenna rich with alternate", url: "/Shows/1/Episodes?Fields=BasicSyncInfo,DateCreated,Path,People,SortName,MediaStreams,MediaSources,AlternateMediaSources", want: EpisodeQueryShapeRich},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			req := httptest.NewRequest("GET", tt.url, nil)
			if got := ResolveEpisodeQueryShape(req); got != tt.want {
				t.Fatalf("ResolveEpisodeQueryShape() = %q, want %q", got, tt.want)
			}
		})
	}
}

func TestResolveLatestQueryShape(t *testing.T) {
	tests := []struct {
		name string
		url  string
		want LatestQueryShape
	}{
		{name: "full by default", url: "/Users/1/Items/Latest?ParentId=view_tmdb_movies", want: LatestQueryShapeFull},
		{name: "thin with basicsyncinfo", url: "/Users/1/Items/Latest?ParentId=view_tmdb_movies&Fields=BasicSyncInfo,ProductionYear", want: LatestQueryShapeThin},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			req := httptest.NewRequest("GET", tt.url, nil)
			if got := ResolveLatestQueryShape(req); got != tt.want {
				t.Fatalf("ResolveLatestQueryShape() = %q, want %q", got, tt.want)
			}
		})
	}
}

func TestResolveSearchQueryShape(t *testing.T) {
	tests := []struct {
		name string
		url  string
		want emby_service.SearchQueryShape
	}{
		{name: "infuse rich", url: "/Users/1/Items?SearchTerm=test&Fields=DateCreated,Etag,Genres,MediaSources,AlternateMediaSources,Overview", want: emby_service.SearchQueryShapeInfuse},
		{name: "family thin", url: "/Users/1/Items?SearchTerm=test&Fields=PremiereDate,CommunityRating,ProductionLocations,Overview,ProductionYear", want: emby_service.SearchQueryShapeFamily},
		{name: "lenna thin", url: "/Users/1/Items?SearchTerm=test&Fields=BasicSyncInfo,CommunityRating,ProductionYear,EndDate,Container", want: emby_service.SearchQueryShapeLenna},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			req := httptest.NewRequest("GET", tt.url, nil)
			if got := ResolveSearchQueryShape(req); got != tt.want {
				t.Fatalf("ResolveSearchQueryShape() = %q, want %q", got, tt.want)
			}
		})
	}
}

func TestFamilyShowPayloadHints(t *testing.T) {
	seasonReq := httptest.NewRequest("GET", "/Shows/1/Seasons?Fields=BasicSyncInfo,Overview,PremiereDate,ChildCount,People", nil)
	if !WantsFamilySeasonPayload(seasonReq) {
		t.Fatal("WantsFamilySeasonPayload() = false, want true")
	}

	episodeReq := httptest.NewRequest("GET", "/Shows/1/Episodes?Fields=BasicSyncInfo,Overview,Path,MediaSources,Size,People,RunTimeTicks,Chapters,CanDownload", nil)
	if !WantsFamilyEpisodePayload(episodeReq) {
		t.Fatal("WantsFamilyEpisodePayload() = false, want true")
	}

	lennaEpisodeReq := httptest.NewRequest("GET", "/Shows/1/Episodes?Fields=BasicSyncInfo,RunTimeTicks,ProductionYear,DateCreated,Genres,Overview,ParentId,Path,People,ProviderIds,Studios,SortName,CommunityRating,OfficialRating,PremiereDate,CanDownload,MediaStreams,MediaSources,AlternateMediaSources", nil)
	if WantsFamilyEpisodePayload(lennaEpisodeReq) {
		t.Fatal("WantsFamilyEpisodePayload() = true, want false")
	}
}
