package emby

import "strings"

type embyListCardInput struct {
	ID              string
	Name            string
	ProductionYear  int
	CommunityRating float64
	ProviderIDs     map[string]any
	ImageTags       map[string]any
	BackdropTags    []string
	ParentID        string
	ServerID        string
}

func embyBuildMovieListCard(in embyListCardInput) map[string]any {
	id := strings.TrimSpace(in.ID)
	name := strings.TrimSpace(in.Name)
	provider := in.ProviderIDs
	if provider == nil {
		provider = map[string]any{}
	}
	imageTags := in.ImageTags
	if imageTags == nil {
		imageTags = map[string]any{}
	}
	backdrop := in.BackdropTags
	if backdrop == nil {
		backdrop = []string{"tmdb"}
	}
	return map[string]any{
		"Id":                      id,
		"Name":                    name,
		"SortName":                name,
		"Type":                    "Movie",
		"MediaType":               "Video",
		"LocationType":            "Remote",
		"IsFolder":                false,
		"ProductionYear":          in.ProductionYear,
		"DateCreated":             embyNowISO(),
		"Etag":                    embyStableEtag(id),
		"Genres":                  []string{},
		"Overview":                "",
		"ParentId":                strings.TrimSpace(in.ParentID),
		"Path":                    "meowfilm://" + id,
		"RecursiveItemCount":      0,
		"ChildCount":              0,
		"MediaSources":            []any{},
		"AlternateMediaSources":   []any{},
		"CommunityRating":         in.CommunityRating,
		"ProviderIds":             provider,
		"ImageTags":               imageTags,
		"BackdropImageTags":       backdrop,
		"ServerId":                strings.TrimSpace(in.ServerID),
		"UserData":                map[string]any{"Played": false},
		"PrimaryImageAspectRatio": 0.6666667,
	}
}

func embyBuildSeriesListCard(in embyListCardInput) map[string]any {
	id := strings.TrimSpace(in.ID)
	name := strings.TrimSpace(in.Name)
	provider := in.ProviderIDs
	if provider == nil {
		provider = map[string]any{}
	}
	imageTags := in.ImageTags
	if imageTags == nil {
		imageTags = map[string]any{}
	}
	backdrop := in.BackdropTags
	if backdrop == nil {
		backdrop = []string{"tmdb"}
	}
	return map[string]any{
		"Id":                      id,
		"Name":                    name,
		"SortName":                name,
		"Type":                    "Series",
		"MediaType":               "Video",
		"LocationType":            "Remote",
		"IsFolder":                true,
		"ProductionYear":          in.ProductionYear,
		"DateCreated":             embyNowISO(),
		"Etag":                    embyStableEtag(id),
		"Genres":                  []string{},
		"Overview":                "",
		"ParentId":                strings.TrimSpace(in.ParentID),
		"Path":                    "meowfilm://" + id,
		"RecursiveItemCount":      0,
		"ChildCount":              0,
		"MediaSources":            []any{},
		"AlternateMediaSources":   []any{},
		"CommunityRating":         in.CommunityRating,
		"ProviderIds":             provider,
		"ImageTags":               imageTags,
		"BackdropImageTags":       backdrop,
		"ServerId":                strings.TrimSpace(in.ServerID),
		"UserData":                map[string]any{"Played": false},
		"PrimaryImageAspectRatio": 0.6666667,
	}
}
