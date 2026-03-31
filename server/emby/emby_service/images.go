package emby_service

import (
	"encoding/json"
	"strconv"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/metadata/douban"
	metadata_tmdb "github.com/jenfonro/meowfilm/server/metadata/tmdb"
	"github.com/jenfonro/meowfilm/server/smart"
)

func ResolvePrimaryImageTarget(database *db.DB, userID int64, itemID string, imageTag string) (string, error) {
	if database == nil {
		return "", nil
	}
	if kind, raw, ok := DecodeSearchPrimaryTag(imageTag); ok {
		switch kind {
		case "tmdb":
			return tmdbAssetURL(database, raw, "w500"), nil
		case "site":
			return rewriteRedirectImageURL(database, raw), nil
		}
		return "", nil
	}
	ref := parseItemRefAny(itemID)
	if ref == nil {
		if personID, err := strconv.Atoi(strings.TrimSpace(itemID)); err == nil && personID > 0 {
			return resolvePersonPrimaryImageTarget(database, personID), nil
		}
		return "", nil
	}
	if ref.Kind == "section" {
		return "/favicon.png", nil
	}
	if ref.Source == "tmdb" {
		return resolveTMDBPrimaryImageTarget(database, userID, ref), nil
	}
	if ref.Source == "site" {
		return resolveSitePrimaryImageURL(database, userID, ref.SiteKey, ref.SiteDetail), nil
	}
	return "", nil
}

func resolvePersonPrimaryImageTarget(database *db.DB, personID int) string {
	if database == nil || personID <= 0 {
		return ""
	}
	profile, err := smart.TMDBGetPersonProfile(database, personID)
	if err != nil {
		return ""
	}
	return tmdbAssetURL(database, strings.TrimSpace(profile), "w185")
}

func ResolveBackdropImageTarget(database *db.DB, itemID string, maxWidth string) (string, error) {
	if database == nil {
		return "", nil
	}
	ref := parseItemRefAny(itemID)
	if ref == nil || ref.Source != "tmdb" || ref.NumericID <= 0 {
		return "", nil
	}
	return resolveTMDBBackdropImageTarget(database, ref, maxWidth), nil
}

func ResolveLogoImageTarget(database *db.DB, itemID string, maxWidth string) (string, error) {
	if database == nil {
		return "", nil
	}
	ref := parseItemRefAny(itemID)
	if ref == nil || ref.Source != "tmdb" || ref.NumericID <= 0 {
		return "", nil
	}
	return resolveTMDBLogoImageTarget(database, ref, maxWidth), nil
}

func resolveTMDBPrimaryImageTarget(database *db.DB, userID int64, ref *itemRef) string {
	if strings.TrimSpace(ref.Variant) == "settings" {
		if target := resolveTMDBSettingsPrimaryImageTarget(ref); strings.TrimSpace(target) != "" {
			return target
		}
	}
	if ref.MediaType == "tv" {
		if target := resolveTMDBTVPrimaryImageTarget(database, ref); strings.TrimSpace(target) != "" {
			return target
		}
	}
	if target := resolveTMDBHistoryPoster(database, userID, ref); strings.TrimSpace(target) != "" {
		return target
	}
	switch ref.MediaType {
	case "movie":
		if detail, err := metadata_tmdb.GetMovieDetails(database, ref.NumericID); err == nil && detail != nil {
			return rewriteRedirectImageURL(database, strings.TrimSpace(detail.PosterPath))
		}
	case "tv":
		if detail, err := metadata_tmdb.GetDetailForBackend(database, "tv", ref.NumericID); err == nil && detail != nil {
			return rewriteRedirectImageURL(database, strings.TrimSpace(detail.PosterPath))
		}
	}
	return ""
}

func resolveTMDBSettingsPrimaryImageTarget(ref *itemRef) string {
	if ref == nil || ref.Source != "tmdb" || strings.TrimSpace(ref.Variant) != "settings" {
		return ""
	}
	base := strings.TrimSpace(resolveTMDBSettingsStaticBaseName(strings.TrimSpace(ref.RawID)))
	if base == "" {
		return ""
	}
	return "/emby/static/settings/images/" + base + ".png"
}

func resolveTMDBLogoImageTarget(database *db.DB, ref *itemRef, maxWidth string) string {
	if database == nil || ref == nil || ref.Source != "tmdb" || ref.NumericID <= 0 {
		return ""
	}
	targetType := ref.MediaType
	targetID := ref.NumericID
	if ref.MediaType == "tv" && (ref.SubKind == "season" || ref.SubKind == "episode") {
		targetType = "tv"
		targetID = ref.NumericID
	}
	logoPath, err := resolveTMDBLogoPath(database, targetType, targetID)
	if err != nil || strings.TrimSpace(logoPath) == "" {
		return ""
	}
	return tmdbAssetURL(database, logoPath, logoWidth(maxWidth))
}

func resolveTMDBLogoPath(database *db.DB, tmdbType string, tmdbID int) (string, error) {
	raw, err := metadata_tmdb.GetRawDetailJSON(database, tmdbType, tmdbID)
	if err != nil || len(raw) == 0 {
		return "", err
	}
	var payload struct {
		Images struct {
			Logos []struct {
				FilePath string  `json:"file_path"`
				ISO639_1 string  `json:"iso_639_1"`
				VoteAvg  float64 `json:"vote_average"`
				Width    int     `json:"width"`
			} `json:"logos"`
		} `json:"images"`
	}
	if err := json.Unmarshal(raw, &payload); err != nil {
		return "", err
	}
	return pickTMDBLogoPath(payload.Images.Logos), nil
}

func pickTMDBLogoPath(logos []struct {
	FilePath string  `json:"file_path"`
	ISO639_1 string  `json:"iso_639_1"`
	VoteAvg  float64 `json:"vote_average"`
	Width    int     `json:"width"`
}) string {
	bestPath := ""
	bestScore := -1.0
	bestWidth := -1
	for _, logo := range logos {
		path := strings.TrimSpace(logo.FilePath)
		if path == "" {
			continue
		}
		langScore := 0.0
		switch strings.ToLower(strings.TrimSpace(logo.ISO639_1)) {
		case "zh", "cn", "zh-cn", "zh-hans", "zh-hant":
			langScore = 30
		case "en":
			langScore = 20
		case "":
			langScore = 10
		default:
			langScore = 0
		}
		score := langScore + logo.VoteAvg
		if score > bestScore || (score == bestScore && logo.Width > bestWidth) {
			bestScore = score
			bestWidth = logo.Width
			bestPath = path
		}
	}
	return bestPath
}

func resolveTMDBTVSeriesPoster(database *db.DB, tmdbID int) string {
	if database == nil || tmdbID <= 0 {
		return ""
	}
	detail, err := metadata_tmdb.GetTVDetails(database, tmdbID)
	if err != nil || detail == nil {
		return ""
	}
	return rewriteRedirectImageURL(database, strings.TrimSpace(detail.PosterPath))
}

func resolveTMDBTVSeriesBackdrop(database *db.DB, tmdbID int) string {
	if database == nil || tmdbID <= 0 {
		return ""
	}
	detail, err := metadata_tmdb.GetTVDetails(database, tmdbID)
	if err != nil || detail == nil {
		return ""
	}
	return tmdbAssetURL(database, strings.TrimSpace(detail.BackdropPath), "w1280")
}

func resolveTMDBTVSeasonPoster(database *db.DB, tmdbID int, season int) string {
	if database == nil || tmdbID <= 0 {
		return ""
	}
	if season <= 0 {
		return resolveTMDBTVSeriesPoster(database, tmdbID)
	}
	if target := resolveTMDBTVSeasonPosterOnly(database, tmdbID, season); strings.TrimSpace(target) != "" {
		return target
	}
	return resolveTMDBTVSeriesPoster(database, tmdbID)
}

func resolveTMDBTVSeasonPosterOnly(database *db.DB, tmdbID int, season int) string {
	if database == nil || tmdbID <= 0 || season <= 0 {
		return ""
	}
	detail, err := metadata_tmdb.GetTVSeasonDetails(database, tmdbID, season)
	if err == nil && detail != nil {
		if target := rewriteRedirectImageURL(database, strings.TrimSpace(detail.PosterPath)); strings.TrimSpace(target) != "" {
			return target
		}
	}
	return ""
}

func resolveTMDBTVEpisodeImage(database *db.DB, tmdbID int, season int, episode int) string {
	if database == nil || tmdbID <= 0 || episode <= 0 {
		return ""
	}
	if season <= 0 {
		return resolveTMDBTVSeriesPoster(database, tmdbID)
	}
	detail, err := metadata_tmdb.GetTVSeasonDetails(database, tmdbID, season)
	if err == nil && detail != nil {
		for _, ep := range detail.Episodes {
			if ep.EpisodeNumber == episode {
				if target := rewriteRedirectImageURL(database, strings.TrimSpace(ep.StillPath)); strings.TrimSpace(target) != "" {
					return target
				}
				break
			}
		}
	}
	if target := resolveTMDBTVSeasonPosterOnly(database, tmdbID, season); strings.TrimSpace(target) != "" {
		return target
	}
	return resolveTMDBTVSeriesPoster(database, tmdbID)
}

func resolveTMDBHistoryPoster(database *db.DB, userID int64, ref *itemRef) string {
	if database == nil || ref == nil || ref.Source != "tmdb" || ref.NumericID <= 0 {
		return ""
	}
	if userID > 0 {
		if hist, err := database.GetPlayHistoryLatestByTMDB(userID, ref.MediaType, ref.NumericID); err == nil && hist != nil {
			if target := rewriteRedirectImageURL(database, strings.TrimSpace(hist.Poster)); strings.TrimSpace(target) != "" {
				return target
			}
		}
	}
	if poster, err := database.GetPlayHistoryLatestPosterByTMDB(ref.MediaType, ref.NumericID); err == nil {
		if target := rewriteRedirectImageURL(database, poster); strings.TrimSpace(target) != "" {
			return target
		}
	}
	return ""
}

func resolveTMDBTVPrimaryImageTarget(database *db.DB, ref *itemRef) string {
	if database == nil || ref == nil || ref.Source != "tmdb" || ref.MediaType != "tv" {
		return ""
	}
	switch ref.SubKind {
	case "episode":
		if strings.TrimSpace(ref.Variant) == "settings" {
			return resolveTMDBTVSeriesPoster(database, ref.NumericID)
		}
		return resolveTMDBTVEpisodeImage(database, ref.NumericID, ref.Pan, ref.Episode)
	case "season":
		if strings.TrimSpace(ref.Variant) == "settings" {
			return resolveTMDBTVSeriesPoster(database, ref.NumericID)
		}
		return resolveTMDBTVSeasonPoster(database, ref.NumericID, ref.Pan)
	case "series", "":
		return resolveTMDBTVSeriesPoster(database, ref.NumericID)
	}
	return ""
}

func resolveTMDBBackdropImageTarget(database *db.DB, ref *itemRef, maxWidth string) string {
	switch ref.MediaType {
	case "movie":
		if detail, err := metadata_tmdb.GetMovieDetails(database, ref.NumericID); err == nil && detail != nil {
			return tmdbAssetURL(database, strings.TrimSpace(detail.Backdrop), backdropWidth(maxWidth))
		}
	case "tv":
		if detail, err := metadata_tmdb.GetTVDetails(database, ref.NumericID); err == nil && detail != nil {
			return tmdbAssetURL(database, strings.TrimSpace(detail.BackdropPath), backdropWidth(maxWidth))
		}
	}
	return ""
}

func resolveSitePrimaryImageURL(database *db.DB, userID int64, siteKey string, siteDetail string) string {
	_ = database
	_ = userID
	_ = siteKey
	_ = siteDetail
	return ""
}

func rewriteRedirectImageURL(database *db.DB, raw string) string {
	target := strings.TrimSpace(raw)
	if target == "" || database == nil {
		return target
	}
	if strings.HasPrefix(target, "/") {
		return tmdbAssetURL(database, target, "w500")
	}
	cfg, err := database.ReadAppConfig()
	if err != nil {
		return target
	}
	if strings.Contains(strings.ToLower(target), "douban") || strings.Contains(strings.ToLower(target), "doubanio") {
		return douban.RewriteVideoPosterURL(target, cfg.DoubanImgProxy, cfg.DoubanImgCustom)
	}
	return target
}

func tmdbAssetURL(database *db.DB, raw string, width string) string {
	target := strings.TrimSpace(raw)
	if target == "" || database == nil {
		return ""
	}
	if strings.HasPrefix(target, "http://") || strings.HasPrefix(target, "https://") {
		return target
	}
	if !strings.HasPrefix(target, "/") {
		target = "/" + target
	}
	cfg, err := database.ReadAppConfig()
	if err != nil {
		return ""
	}
	base := strings.TrimRight(strings.TrimSpace(cfg.TMDBImgBase), "/")
	if base == "" {
		base = "https://image.tmdb.org"
	}
	if strings.TrimSpace(width) == "" {
		width = "w500"
	}
	return base + "/t/p/" + width + target
}

func backdropWidth(maxWidth string) string {
	if strings.TrimSpace(maxWidth) == "" || strings.TrimSpace(maxWidth) == "0" {
		return "w1280"
	}
	return "w1280"
}

func logoWidth(maxWidth string) string {
	if strings.TrimSpace(maxWidth) == "" || strings.TrimSpace(maxWidth) == "0" {
		return "w500"
	}
	return "w500"
}
