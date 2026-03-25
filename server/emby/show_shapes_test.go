package emby

import (
	"net/http/httptest"
	"testing"

	embysvc "github.com/jenfonro/meowfilm/server/emby/emby_service"
)

func TestBuildShowSeasonsByShapeRoutesByQuery(t *testing.T) {
	tests := []struct {
		name     string
		url      string
		typeName string
	}{
		{name: "basic", url: "/Shows/1/Seasons", typeName: "emby_service.SeasonsBasicResponseDTO"},
		{name: "genres", url: "/Shows/1/Seasons?Fields=Genres,ParentId", typeName: "emby_service.SeasonsResponseDTO"},
		{name: "lenna rich", url: "/Shows/1/Seasons?Fields=BasicSyncInfo,CommunityRating,ProductionYear,EndDate,Container,Overview", typeName: "emby_service.SeasonsRichResponseDTO"},
		{name: "family rich", url: "/Shows/1/Seasons?Fields=BasicSyncInfo,Overview,PremiereDate,ChildCount,People", typeName: "emby_service.SeasonsFamilyResponseDTO"},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			req := httptest.NewRequest("GET", tt.url, nil)
			got, _, _ := buildShowSeasonsByShape(req, nil, 0, "", "")
			switch tt.typeName {
			case "emby_service.SeasonsBasicResponseDTO":
				if _, ok := got.(embysvc.SeasonsBasicResponseDTO); !ok {
					t.Fatalf("got type %T, want SeasonsBasicResponseDTO", got)
				}
			case "emby_service.SeasonsResponseDTO":
				if _, ok := got.(embysvc.SeasonsResponseDTO); !ok {
					t.Fatalf("got type %T, want SeasonsResponseDTO", got)
				}
			case "emby_service.SeasonsRichResponseDTO":
				if _, ok := got.(embysvc.SeasonsRichResponseDTO); !ok {
					t.Fatalf("got type %T, want SeasonsRichResponseDTO", got)
				}
			case "emby_service.SeasonsFamilyResponseDTO":
				if _, ok := got.(embysvc.SeasonsFamilyResponseDTO); !ok {
					t.Fatalf("got type %T, want SeasonsFamilyResponseDTO", got)
				}
			}
		})
	}
}

func TestBuildShowEpisodesByShapeRoutesByQuery(t *testing.T) {
	tests := []struct {
		name     string
		url      string
		typeName string
	}{
		{name: "basic", url: "/Shows/1/Episodes", typeName: "emby_service.EpisodesBasicResponseDTO"},
		{name: "infuse", url: "/Shows/1/Episodes?Fields=Etag,AlternateMediaSources", typeName: "emby_service.EpisodesInfuseResponseDTO"},
		{name: "lenna rich", url: "/Shows/1/Episodes?Fields=BasicSyncInfo,RunTimeTicks,ProductionYear,DateCreated,Genres,Overview,ParentId,Path,People,ProviderIds,Studios,SortName,CommunityRating,OfficialRating,PremiereDate,CanDownload,MediaStreams,MediaSources,AlternateMediaSources", typeName: "emby_service.EpisodesRichResponseDTO"},
		{name: "family rich", url: "/Shows/1/Episodes?Fields=BasicSyncInfo,Overview,Path,MediaSources,Size,People,RunTimeTicks,Chapters,CanDownload", typeName: "emby_service.EpisodesResponseDTO"},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			req := httptest.NewRequest("GET", tt.url, nil)
			got, _, _ := buildShowEpisodesByShape(req, nil, 0, "", "", "")
			switch tt.typeName {
			case "emby_service.EpisodesBasicResponseDTO":
				if _, ok := got.(embysvc.EpisodesBasicResponseDTO); !ok {
					t.Fatalf("got type %T, want EpisodesBasicResponseDTO", got)
				}
			case "emby_service.EpisodesInfuseResponseDTO":
				if _, ok := got.(embysvc.EpisodesInfuseResponseDTO); !ok {
					t.Fatalf("got type %T, want EpisodesInfuseResponseDTO", got)
				}
			case "emby_service.EpisodesRichResponseDTO":
				if _, ok := got.(embysvc.EpisodesRichResponseDTO); !ok {
					t.Fatalf("got type %T, want EpisodesRichResponseDTO", got)
				}
			case "emby_service.EpisodesResponseDTO":
				if _, ok := got.(embysvc.EpisodesResponseDTO); !ok {
					t.Fatalf("got type %T, want EpisodesResponseDTO", got)
				}
			}
		})
	}
}
