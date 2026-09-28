package emby_service

import (
	"fmt"
	"strings"
)

func BuildQuickNowPlayingDetailPayload(serverID string, itemID string, playing PlayingGuardEntry) any {
	id := strings.TrimSpace(itemID)
	if id == "" {
		return map[string]any{
			"Id":        "",
			"Name":      "",
			"Type":      "Video",
			"MediaType": "Video",
			"IsFolder":  false,
			"ServerId":  strings.TrimSpace(serverID),
			"UserData":  map[string]any{"Played": false},
		}
	}
	ref := parseItemRefAny(id)
	if ref == nil {
		return map[string]any{
			"Id":        id,
			"Name":      firstNonEmptyString(strings.TrimSpace(playing.NowItemName), id),
			"Type":      "Video",
			"MediaType": "Video",
			"IsFolder":  false,
			"ServerId":  strings.TrimSpace(serverID),
			"UserData":  map[string]any{"Played": false},
		}
	}
	switch {
	case ref.Source == "tmdb" && ref.SubKind == "episode":
		name := strings.TrimSpace(playing.NowItemName)
		if name == "" {
			name = fmt.Sprintf("S%02dE%02d", maxInt(0, ref.Pan), maxInt(0, ref.Episode))
		}
		return map[string]any{
			"Id":                id,
			"Name":              name,
			"SeriesName":        strings.TrimSpace(playing.SeriesName),
			"Type":              "Episode",
			"MediaType":         "Video",
			"IsFolder":          false,
			"ServerId":          strings.TrimSpace(serverID),
			"ParentIndexNumber": maxInt(0, ref.Pan),
			"IndexNumber":       maxInt(0, ref.Episode),
			"UserData":          map[string]any{"Played": false},
		}
	case ref.Source == "tmdb" && ref.SubKind == "season":
		name := strings.TrimSpace(playing.NowItemName)
		if name == "" {
			name = fmt.Sprintf("Season %d", maxInt(0, ref.Pan))
		}
		return map[string]any{
			"Id":                id,
			"Name":              name,
			"SeriesName":        strings.TrimSpace(playing.SeriesName),
			"Type":              "Season",
			"MediaType":         "Video",
			"IsFolder":          true,
			"ServerId":          strings.TrimSpace(serverID),
			"ParentIndexNumber": maxInt(0, ref.Pan),
			"UserData":          map[string]any{"Played": false},
		}
	case ref.Source == "tmdb" && ref.SubKind == "series":
		name := firstNonEmptyString(strings.TrimSpace(playing.SeriesName), strings.TrimSpace(playing.NowItemName), id)
		return map[string]any{
			"Id":        id,
			"Name":      name,
			"Type":      "Series",
			"MediaType": "Video",
			"IsFolder":  true,
			"ServerId":  strings.TrimSpace(serverID),
			"UserData":  map[string]any{"Played": false},
		}
	case ref.Source == "tmdb" && ref.SubKind == "movie":
		return map[string]any{
			"Id":        id,
			"Name":      firstNonEmptyString(strings.TrimSpace(playing.NowItemName), id),
			"Type":      "Movie",
			"MediaType": "Video",
			"IsFolder":  false,
			"ServerId":  strings.TrimSpace(serverID),
			"UserData":  map[string]any{"Played": false},
		}
	default:
		return map[string]any{
			"Id":        id,
			"Name":      firstNonEmptyString(strings.TrimSpace(playing.NowItemName), id),
			"Type":      "Video",
			"MediaType": "Video",
			"IsFolder":  false,
			"ServerId":  strings.TrimSpace(serverID),
			"UserData":  map[string]any{"Played": false},
		}
	}
}
