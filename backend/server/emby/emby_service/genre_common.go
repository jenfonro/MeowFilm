package emby_service

import (
	"strconv"
	"strings"
)

type GenreSeed struct {
	ID   string
	Name string
}

var movieGenreSeeds = []GenreSeed{
	{ID: "388", Name: "动画"},
	{ID: "389", Name: "动作"},
	{ID: "370", Name: "惊悚"},
	{ID: "369", Name: "剧情"},
	{ID: "390", Name: "冒险"},
	{ID: "391", Name: "奇幻"},
}

var tvGenreSeeds = []GenreSeed{
	{ID: "183", Name: "Action"},
	{ID: "182", Name: "Adventure"},
	{ID: "181", Name: "Animation"},
	{ID: "201", Name: "Anime"},
	{ID: "403", Name: "Drama"},
	{ID: "180", Name: "Fantasy"},
	{ID: "184", Name: "Martial Arts"},
}

func ResolveGenreSeeds(includeItemTypes string) []GenreSeed {
	include := strings.ToLower(strings.TrimSpace(includeItemTypes))
	if include == "" {
		return []GenreSeed{}
	}
	out := make([]GenreSeed, 0, len(movieGenreSeeds)+len(tvGenreSeeds))
	seen := map[string]struct{}{}
	for _, part := range strings.Split(include, ",") {
		switch strings.TrimSpace(part) {
		case "movie":
			for _, seed := range movieGenreSeeds {
				if _, ok := seen[seed.ID]; ok {
					continue
				}
				seen[seed.ID] = struct{}{}
				out = append(out, seed)
			}
		case "series":
			for _, seed := range tvGenreSeeds {
				if _, ok := seen[seed.ID]; ok {
					continue
				}
				seen[seed.ID] = struct{}{}
				out = append(out, seed)
			}
		}
	}
	return out
}

func GenreNamesFromIDs(mediaType string, genreIDs []int) []string {
	if len(genreIDs) == 0 {
		return EmptyStrings()
	}
	lookup := map[int]string{}
	for _, seed := range ResolveGenreSeeds(resolveGenreSeedIncludeType(mediaType)) {
		if id, err := strconv.Atoi(seed.ID); err == nil {
			lookup[id] = seed.Name
		}
	}
	out := make([]string, 0, len(genreIDs))
	for _, id := range genreIDs {
		if name := strings.TrimSpace(lookup[id]); name != "" {
			out = append(out, name)
		}
	}
	return out
}

func NamedGenreItems(genres []string) []NamedIDDTO {
	if len(genres) == 0 {
		return []NamedIDDTO{}
	}
	out := make([]NamedIDDTO, 0, len(genres))
	for _, genre := range genres {
		if name := strings.TrimSpace(genre); name != "" {
			out = append(out, NamedIDDTO{
				Name: name,
				ID:   StableMD5Hex("genre|" + name),
			})
		}
	}
	return out
}

func NonEmptyStrings(in []string) []string {
	if len(in) == 0 {
		return EmptyStrings()
	}
	out := make([]string, 0, len(in))
	for _, item := range in {
		if v := strings.TrimSpace(item); v != "" {
			out = append(out, v)
		}
	}
	return out
}

func resolveGenreSeedIncludeType(mediaType string) string {
	switch strings.ToLower(strings.TrimSpace(mediaType)) {
	case "movie":
		return "movie"
	default:
		return "series"
	}
}
