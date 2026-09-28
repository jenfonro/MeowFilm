package emby_service

import (
	"encoding/base64"
	"strings"
)

func PrimaryTagForItem(itemID string) string {
	return StableMD5Hex(itemID + "|primary")
}

func SearchTMDBPrimaryTag(posterPath string) string {
	path := strings.TrimSpace(posterPath)
	if path == "" {
		return ""
	}
	return "search-tmdb:" + base64.RawURLEncoding.EncodeToString([]byte(path))
}

func SearchSitePrimaryTag(pic string) string {
	raw := strings.TrimSpace(pic)
	if raw == "" {
		return ""
	}
	return "search-site:" + base64.RawURLEncoding.EncodeToString([]byte(raw))
}

func DecodeSearchPrimaryTag(tag string) (kind string, raw string, ok bool) {
	value := strings.TrimSpace(tag)
	switch {
	case strings.HasPrefix(value, "search-tmdb:"):
		decoded, err := base64.RawURLEncoding.DecodeString(strings.TrimPrefix(value, "search-tmdb:"))
		if err != nil {
			return "", "", false
		}
		return "tmdb", strings.TrimSpace(string(decoded)), strings.TrimSpace(string(decoded)) != ""
	case strings.HasPrefix(value, "search-site:"):
		decoded, err := base64.RawURLEncoding.DecodeString(strings.TrimPrefix(value, "search-site:"))
		if err != nil {
			return "", "", false
		}
		return "site", strings.TrimSpace(string(decoded)), strings.TrimSpace(string(decoded)) != ""
	default:
		return "", "", false
	}
}

func LogoTagForItem(itemID string) string {
	return StableMD5Hex(itemID + "|logo")
}

func ImageTagsForItem(itemID string, includeLogo bool) ImageTagsDTO {
	out := ImageTagsDTO{Primary: PrimaryTagForItem(itemID)}
	if includeLogo {
		out.Logo = LogoTagForItem(itemID)
	}
	return out
}

func BackdropTagsOrEmpty(tags []string) []string {
	if len(tags) == 0 {
		return EmptyStrings()
	}
	return tags
}

func EmptyTVLatestUserData() TVLatestUserDataDTO {
	return TVLatestUserDataDTO{
		UnplayedItemCount:     0,
		PlaybackPositionTicks: 0,
		PlayCount:             0,
		IsFavorite:            false,
		Played:                false,
	}
}

func EmptySimpleUserData() SimpleUserDataDTO {
	return SimpleUserDataDTO{
		PlaybackPositionTicks: 0,
		PlayCount:             0,
		IsFavorite:            false,
		Played:                false,
	}
}

func EmptyMovieLatestUserData() MovieLatestUserDataDTO {
	return MovieLatestUserDataDTO{
		PlaybackPositionTicks: 0,
		PlayCount:             0,
		IsFavorite:            false,
		Played:                false,
	}
}
