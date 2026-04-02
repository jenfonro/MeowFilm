package emby_service

import (
	"fmt"
	"regexp"
	"strconv"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/metadata/douban"
)

type movieLatestSource struct {
	ID              string
	Name            string
	DateCreated     string
	Container       string
	SortName        string
	PremiereDate    string
	MediaSources    []ResumeMediaSourceDTO
	Path            string
	OfficialRating  string
	Overview        string
	Genres          []string
	CommunityRating float64
	RunTimeTicks    int64
	Size            int64
	Bitrate         int
	ProductionYear  int
	ProviderIDs     map[string]any
	ParentID        string
	GenreItems      []NamedIDDTO
	AspectRatio     float64
	PrimaryTag      string
	LogoTag         string
	BackdropTags    []string
	PosterURL       string
}

type tvLatestSource struct {
	ID              string
	Name            string
	DateCreated     string
	SortName        string
	PremiereDate    string
	Path            string
	Overview        string
	Genres          []string
	RunTimeTicks    int64
	ProductionYear  int
	ProviderIDs     map[string]any
	ParentID        string
	GenreItems      []NamedIDDTO
	AspectRatio     float64
	PrimaryTag      string
	LogoTag         string
	BackdropTags    []string
	CommunityRating float64
	RecursiveCount  int
	ChildCount      int
	Status          string
	UnplayedCount   int
	PosterURL       string
}

type historyGroupRef struct {
	Kind    string
	TMDBID  int
	GroupID string
}

func BuildLatestPayload(database *db.DB, userID int64, serverID string, section db.ThirdPartyClientHomeSection, limit int) (any, error) {
	switch LatestSectionKind(section) {
	case "movie":
		rows, err := loadMovieLatest(database, section, limit)
		if err != nil {
			return nil, err
		}
		out := make([]MovieLatestItemDTO, 0, len(rows))
		for _, row := range rows {
			out = append(out, buildMovieLatestItemDTO(serverID, row))
		}
		return out, nil
	case "tv":
		rows, err := loadTVLatest(database, section, limit)
		if err != nil {
			return nil, err
		}
		out := make([]TVLatestItemDTO, 0, len(rows))
		for _, row := range rows {
			out = append(out, buildTVLatestItemDTO(serverID, row))
		}
		return out, nil
	case "mixed":
		return BuildHistoryLatestPayload(database, userID, serverID, limit)
	default:
		return EmptyAnySlice(), nil
	}
}

func BuildHistoryLatestPayload(database *db.DB, userID int64, serverID string, limit int) ([]any, error) {
	if database == nil || userID <= 0 {
		return EmptyAnySlice(), nil
	}
	rows, err := database.ListPlayHistory(userID, maxInt(limit*4, 20))
	if err != nil {
		return nil, err
	}
	out := make([]any, 0, limit)
	seen := map[string]struct{}{}
	for _, row := range rows {
		ref := historyRef(database, row)
		if strings.TrimSpace(ref.GroupID) == "" {
			continue
		}
		if _, ok := seen[ref.GroupID]; ok {
			continue
		}
		seen[ref.GroupID] = struct{}{}
		switch ref.Kind {
		case "movie":
			item, ok := buildHistoryMovieLatestItem(database, userID, serverID, row, ref.TMDBID)
			if ok {
				out = append(out, item)
			}
		case "tv":
			item, ok := buildHistoryTVLatestItem(database, userID, serverID, row, ref.TMDBID)
			if ok {
				out = append(out, item)
			}
		case "site":
			item, ok := buildHistorySiteLatestItem(database, serverID, row)
			if ok {
				out = append(out, item)
			}
		}
		if len(out) >= limit {
			break
		}
	}
	return out, nil
}

func loadMovieLatest(database *db.DB, section db.ThirdPartyClientHomeSection, limit int) ([]movieLatestSource, error) {
	return loadMovieLatestRange(database, section, 0, limit)
}

func loadMovieLatestRange(database *db.DB, section db.ThirdPartyClientHomeSection, start int, limit int) ([]movieLatestSource, error) {
	if LatestSectionKind(section) != "movie" || strings.EqualFold(strings.TrimSpace(section.Module), "site_data") {
		return []movieLatestSource{}, nil
	}
	hot, err := douban.FetchRecentHot(database, "movie", "热门", "全部", start, limit)
	if err != nil {
		return nil, err
	}
	out := make([]movieLatestSource, 0, len(hot))
	for _, item := range hot {
		tmdbID, _, _ := ResolveLatestMovieTMDB(database, item.Title, item.Year)
		if tmdbID <= 0 {
			continue
		}
		playbackID := buildMovieID(tmdbID)
		premiereDate := embyYearDate(item.Year)
		productionYear := item.Year
		dateCreated := EmbyZeroTimeString()
		overview := ""
		genres := EmptyStrings()
		genreItems := EmptyNamedIDs()
		providerIDs := EmptyLatestProviderIDs()
		communityRating := parseRating(item.Rate)
		posterURL := rewriteRedirectImageURL(database, item.Poster)
		if tmdbID > 0 {
			meta := LatestMovieMetadataFromTMDB(database, tmdbID, item.Poster)
			dateCreated = meta.DateCreated
			if strings.TrimSpace(meta.PremiereDate) != "" {
				premiereDate = meta.PremiereDate
			}
			if meta.ProductionYear > 0 {
				productionYear = meta.ProductionYear
			}
			overview = meta.Overview
			genres = meta.Genres
			genreItems = meta.GenreItems
			providerIDs = meta.ProviderIDs
			posterURL = meta.PosterURL
			if meta.CommunityRating > 0 {
				communityRating = meta.CommunityRating
			}
		}
		out = append(out, NormalizeMovieLatestSource(movieLatestSource{
			ID:              playbackID,
			Name:            item.Title,
			DateCreated:     dateCreated,
			Container:       "",
			SortName:        SortNameOrName(item.Title),
			PremiereDate:    premiereDate,
			MediaSources:    EmptyResumeMediaSources(),
			Path:            "",
			OfficialRating:  "",
			Overview:        overview,
			Genres:          genres,
			CommunityRating: communityRating,
			RunTimeTicks:    0,
			Size:            0,
			Bitrate:         0,
			ProductionYear:  productionYear,
			ProviderIDs:     providerIDs,
			ParentID:        strings.TrimSpace(section.ID),
			GenreItems:      genreItems,
			AspectRatio:     0.6666667,
			PrimaryTag:      StableMD5Hex(playbackID + "|primary"),
			LogoTag:         StableMD5Hex(playbackID + "|logo"),
			BackdropTags:    EmptyStrings(),
			PosterURL:       posterURL,
		}))
		if len(out) >= limit {
			break
		}
	}
	return out, nil
}

func loadTVLatest(database *db.DB, section db.ThirdPartyClientHomeSection, limit int) ([]tvLatestSource, error) {
	return loadTVLatestRange(database, section, 0, limit)
}

func loadTVLatestRange(database *db.DB, section db.ThirdPartyClientHomeSection, start int, limit int) ([]tvLatestSource, error) {
	if LatestSectionKind(section) != "tv" || strings.EqualFold(strings.TrimSpace(section.Module), "site_data") {
		return []tvLatestSource{}, nil
	}
	category, hotType := "tv", "tv"
	switch strings.ToLower(strings.TrimSpace(section.Module)) {
	case "bangumi_anime":
		hotType = "tv_animation"
	case "douban_variety":
		category, hotType = "show", "show"
	}
	hot, err := douban.FetchRecentHot(database, "tv", category, hotType, start, limit)
	if err != nil {
		return nil, err
	}
	out := make([]tvLatestSource, 0, len(hot))
	for _, item := range hot {
		tmdbID, _, _ := ResolveLatestTVTMDB(database, item.Title, item.Year)
		if tmdbID <= 0 {
			continue
		}
		playbackID := buildSeriesID(tmdbID)
		premiereDate := embyYearDate(item.Year)
		productionYear := item.Year
		dateCreated := EmbyZeroTimeString()
		status := inferTVStatusFromDouban(item.Subtitle)
		overview := ""
		genres := EmptyStrings()
		genreItems := EmptyNamedIDs()
		providerIDs := EmptyLatestProviderIDs()
		recursiveCount := 0
		communityRating := 0.0
		posterURL := rewriteRedirectImageURL(database, item.Poster)
		backdropTags := EmptyStrings()
		if tmdbID > 0 {
			meta := LatestTVMetadataFromTMDB(database, tmdbID, item.Poster)
			dateCreated = meta.DateCreated
			if strings.TrimSpace(meta.PremiereDate) != "" {
				premiereDate = meta.PremiereDate
			}
			if meta.ProductionYear > 0 {
				productionYear = meta.ProductionYear
			}
			if strings.TrimSpace(meta.Status) != "" {
				status = meta.Status
			}
			overview = meta.Overview
			genres = meta.Genres
			genreItems = meta.GenreItems
			providerIDs = meta.ProviderIDs
			recursiveCount = meta.RecursiveCount
			communityRating = meta.CommunityRating
			posterURL = meta.PosterURL
			backdropTags = meta.BackdropTags
		}
		out = append(out, NormalizeTVLatestSource(tvLatestSource{
			ID:              playbackID,
			Name:            item.Title,
			DateCreated:     dateCreated,
			SortName:        SortNameOrName(item.Title),
			PremiereDate:    premiereDate,
			Path:            "",
			Overview:        overview,
			Genres:          genres,
			RunTimeTicks:    0,
			ProductionYear:  productionYear,
			ProviderIDs:     providerIDs,
			ParentID:        strings.TrimSpace(section.ID),
			GenreItems:      genreItems,
			AspectRatio:     PosterAspectRatio(item.Poster),
			PrimaryTag:      StableMD5Hex(playbackID + "|primary"),
			LogoTag:         StableMD5Hex(playbackID + "|logo"),
			BackdropTags:    backdropTags,
			CommunityRating: communityRating,
			RecursiveCount:  recursiveCount,
			ChildCount:      parseDoubanSeasonCount(item.Title, item.Subtitle),
			Status:          status,
			UnplayedCount:   inferTVUnplayedCountFromDouban(item.Subtitle),
			PosterURL:       posterURL,
		}))
		if len(out) >= limit {
			break
		}
	}
	return out, nil
}

func buildMovieLatestItemDTO(serverID string, row movieLatestSource) MovieLatestItemDTO {
	row = NormalizeMovieLatestSource(row)
	state := MovieItemState(true, false)
	userData := EmptyMovieLatestUserData()
	if row.RunTimeTicks > 0 {
		userData.PlaybackPositionTicks = minInt64(row.RunTimeTicks/3, row.RunTimeTicks-1)
		percent := PlayedPercentage(userData.PlaybackPositionTicks, row.RunTimeTicks)
		if percent > 0 {
			userData.PlayedPercentage = &percent
		}
	}
	return MovieLatestItemDTO{
		Name:                    row.Name,
		ServerID:                strings.TrimSpace(serverID),
		ID:                      strings.TrimSpace(row.ID),
		Etag:                    StableItemEtag(row.ID),
		DateCreated:             row.DateCreated,
		Container:               strings.TrimSpace(row.Container),
		SortName:                SortNameOrName(row.SortName),
		CanDelete:               state.CanDelete,
		SupportsSync:            state.SupportsSync,
		PremiereDate:            strings.TrimSpace(row.PremiereDate),
		MediaSources:            row.MediaSources,
		Path:                    strings.TrimSpace(row.Path),
		OfficialRating:          strings.TrimSpace(row.OfficialRating),
		Overview:                strings.TrimSpace(row.Overview),
		Genres:                  row.Genres,
		CommunityRating:         row.CommunityRating,
		RunTimeTicks:            row.RunTimeTicks,
		Size:                    row.Size,
		Bitrate:                 row.Bitrate,
		ProductionYear:          row.ProductionYear,
		ProviderIDs:             row.ProviderIDs,
		IsFolder:                state.IsFolder,
		ParentID:                strings.TrimSpace(row.ParentID),
		Type:                    state.Type,
		GenreItems:              row.GenreItems,
		UserData:                userData,
		PrimaryImageAspectRatio: NormalizeAspectRatio(row.AspectRatio),
		ImageTags:               ImageTagsDTO{Primary: row.PrimaryTag, Logo: row.LogoTag},
		BackdropImageTags:       BackdropTagsOrEmpty(defaultBackdropTags(row.BackdropTags, row.ID)),
		MediaType:               MediaTypeVideo,
	}
}

func buildTVLatestItemDTO(serverID string, row tvLatestSource) TVLatestItemDTO {
	row = NormalizeTVLatestSource(row)
	state := SeriesItemState(true, false)
	return TVLatestItemDTO{
		Name:                    row.Name,
		ServerID:                strings.TrimSpace(serverID),
		ID:                      strings.TrimSpace(row.ID),
		Etag:                    StableItemEtag(row.ID),
		DateCreated:             row.DateCreated,
		SortName:                SortNameOrName(row.SortName),
		CanDelete:               state.CanDelete,
		SupportsSync:            state.SupportsSync,
		PremiereDate:            strings.TrimSpace(row.PremiereDate),
		Path:                    strings.TrimSpace(row.Path),
		Overview:                strings.TrimSpace(row.Overview),
		Genres:                  row.Genres,
		RunTimeTicks:            row.RunTimeTicks,
		ProductionYear:          row.ProductionYear,
		ProviderIDs:             row.ProviderIDs,
		IsFolder:                state.IsFolder,
		ParentID:                strings.TrimSpace(row.ParentID),
		Type:                    state.Type,
		GenreItems:              row.GenreItems,
		UserData:                TVLatestUserDataDTO{UnplayedItemCount: row.UnplayedCount, PlaybackPositionTicks: 0, PlayCount: 0, IsFavorite: false, Played: false},
		RecursiveItemCount:      row.RecursiveCount,
		ChildCount:              row.ChildCount,
		Status:                  strings.TrimSpace(row.Status),
		AirDays:                 EmptyStrings(),
		PrimaryImageAspectRatio: NormalizeAspectRatio(row.AspectRatio),
		ImageTags:               ImageTagsDTO{Primary: row.PrimaryTag, Logo: row.LogoTag},
		BackdropImageTags:       BackdropTagsOrEmpty(defaultBackdropTags(row.BackdropTags, row.ID)),
	}
}

func embyYearDate(year int) string {
	if year <= 0 {
		return ""
	}
	return fmt.Sprintf("%04d-01-01T00:00:00.0000000Z", year)
}

func parseRating(raw string) float64 {
	f, err := strconv.ParseFloat(strings.TrimSpace(raw), 64)
	if err != nil {
		return 0
	}
	return f
}

func inferTVStatusFromDouban(subtitle string) string {
	raw := strings.TrimSpace(subtitle)
	if raw == "" {
		return ""
	}
	if strings.Contains(raw, "更新") {
		return "Continuing"
	}
	if strings.Contains(raw, "完结") || strings.Contains(raw, "全") {
		return "Ended"
	}
	return ""
}

func inferTVUnplayedCountFromDouban(subtitle string) int {
	raw := strings.TrimSpace(subtitle)
	if raw == "" {
		return 0
	}
	if m := reHistoryTVSeasonTotal.FindStringSubmatch(raw); len(m) == 3 {
		if n := parsePositiveInt(m[2]); n > 0 {
			return n
		}
	}
	if m := reHistoryTVTotalOnly.FindStringSubmatch(raw); len(m) == 2 {
		if n := parsePositiveInt(m[1]); n > 0 {
			return n
		}
	}
	if m := reHistoryTVUpdateEpisode.FindStringSubmatch(raw); len(m) == 2 {
		if n := parsePositiveInt(m[1]); n > 0 {
			return n
		}
	}
	return 0
}

func defaultBackdropTags(tags []string, id string) []string {
	if len(tags) > 0 {
		return tags
	}
	return EmptyStrings()
}

func backdropTagsFromAsset(raw string) []string {
	if strings.TrimSpace(raw) == "" {
		return EmptyStrings()
	}
	return []string{StableMD5Hex(strings.TrimSpace(raw))}
}

func historyRef(database *db.DB, row db.PlayHistoryRow) historyGroupRef {
	typ := strings.ToLower(strings.TrimSpace(row.TMDBType))
	if (typ == "movie" || typ == "tv") && row.TMDBID > 0 {
		if typ == "movie" {
			return historyGroupRef{Kind: "movie", TMDBID: row.TMDBID, GroupID: buildMovieID(row.TMDBID)}
		}
		return historyGroupRef{Kind: "tv", TMDBID: row.TMDBID, GroupID: buildSeriesID(row.TMDBID)}
	}
	pid := strings.TrimSpace(row.PlaybackItemID)
	if pid != "" {
		if siteKey, siteDetail, _, _, _, _, _, ok := parseSiteEpisodeID(pid); ok {
			return historyGroupRef{Kind: "site", GroupID: buildSiteSeriesID(siteKey, siteDetail)}
		}
		if siteKey, siteDetail, _, ok := parseSiteSeasonID(pid); ok {
			return historyGroupRef{Kind: "site", GroupID: buildSiteSeriesID(siteKey, siteDetail)}
		}
		if siteKey, siteDetail, ok := parseSiteSeriesID(pid); ok {
			return historyGroupRef{Kind: "site", GroupID: buildSiteSeriesID(siteKey, siteDetail)}
		}
	}
	if strings.TrimSpace(row.SiteKey) != "" && strings.TrimSpace(row.SiteDetail) != "" {
		return historyGroupRef{Kind: "site", GroupID: buildSiteSeriesID(row.SiteKey, row.SiteDetail)}
	}
	return historyGroupRef{}
}

func buildHistoryMovieLatestItem(database *db.DB, userID int64, serverID string, row db.PlayHistoryRow, tmdbID int) (MovieLatestItemDTO, bool) {
	hist, _ := database.GetPlayHistoryLatestByTMDB(userID, "movie", tmdbID)
	playbackID := buildMovieID(tmdbID)
	name := strings.TrimSpace(row.ContentKey)
	if name == "" {
		return MovieLatestItemDTO{}, false
	}
	posterURL := rewriteRedirectImageURL(database, row.Poster)
	premiereDate := ""
	productionYear := 0
	overview := ""
	genres := EmptyStrings()
	genreItems := EmptyNamedIDs()
	meta := LatestMovieMetadataFromTMDB(database, tmdbID, row.Poster)
	posterURL = meta.PosterURL
	premiereDate = meta.PremiereDate
	productionYear = meta.ProductionYear
	overview = meta.Overview
	genres = meta.Genres
	genreItems = meta.GenreItems
	communityRating := meta.CommunityRating
	if strings.TrimSpace(posterURL) == "" {
		return MovieLatestItemDTO{}, false
	}
	path := ""
	container := ""
	mediaSources := EmptyResumeMediaSources()
	dateCreated := EmbyZeroTimeString()
	size := int64(0)
	bitrate := 0
	if hist != nil {
		dateCreated = resumeDateCreated(*hist)
		mediaSources = resumeMediaSources(StableMD5Hex(playbackID+"|media"), path, container, *hist)
	}
	source := NormalizeMovieLatestSource(movieLatestSource{
		ID:              playbackID,
		Name:            name,
		DateCreated:     dateCreated,
		Container:       containerCSV(container),
		SortName:        SortNameOrName(name),
		PremiereDate:    premiereDate,
		MediaSources:    mediaSources,
		Path:            path,
		OfficialRating:  "",
		Overview:        overview,
		Genres:          genres,
		CommunityRating: communityRating,
		RunTimeTicks:    0,
		Size:            size,
		Bitrate:         bitrate,
		ProductionYear:  productionYear,
		ProviderIDs:     meta.ProviderIDs,
		ParentID:        resumeRootParentID(database, "movie"),
		GenreItems:      genreItems,
		AspectRatio:     0.6666667,
		PrimaryTag:      StableMD5Hex(playbackID + "|primary"),
		LogoTag:         StableMD5Hex(playbackID + "|logo"),
		BackdropTags:    EmptyStrings(),
		PosterURL:       posterURL,
	})
	if hist != nil && hist.PlaybackRuntimeTicks > 0 {
		source.RunTimeTicks = hist.PlaybackRuntimeTicks
	}
	return buildMovieLatestItemDTO(serverID, source), true
}

func buildHistoryTVLatestItem(database *db.DB, userID int64, serverID string, row db.PlayHistoryRow, tmdbID int) (TVLatestItemDTO, bool) {
	hist, _ := database.GetPlayHistoryLatestByTMDB(userID, "tv", tmdbID)
	playbackID := buildSeriesID(tmdbID)
	name := strings.TrimSpace(row.ContentKey)
	if name == "" {
		return TVLatestItemDTO{}, false
	}
	progress := parseHistoryTVRemark(strings.TrimSpace(row.Remark), name)
	posterURL := rewriteRedirectImageURL(database, row.Poster)
	premiereDate := ""
	productionYear := 0
	overview := ""
	genres := EmptyStrings()
	genreItems := EmptyNamedIDs()
	meta := LatestTVMetadataFromTMDB(database, tmdbID, row.Poster)
	posterURL = meta.PosterURL
	premiereDate = meta.PremiereDate
	productionYear = meta.ProductionYear
	overview = meta.Overview
	genres = meta.Genres
	genreItems = meta.GenreItems
	if strings.TrimSpace(posterURL) == "" {
		return TVLatestItemDTO{}, false
	}
	source := NormalizeTVLatestSource(tvLatestSource{
		ID:              playbackID,
		Name:            name,
		DateCreated:     EmbyZeroTimeString(),
		SortName:        SortNameOrName(name),
		PremiereDate:    premiereDate,
		Path:            "",
		Overview:        overview,
		Genres:          genres,
		RunTimeTicks:    0,
		ProductionYear:  productionYear,
		ProviderIDs:     meta.ProviderIDs,
		ParentID:        resumeRootParentID(database, "tv"),
		GenreItems:      genreItems,
		AspectRatio:     0.6666667,
		PrimaryTag:      StableMD5Hex(playbackID + "|primary"),
		LogoTag:         StableMD5Hex(playbackID + "|logo"),
		BackdropTags:    meta.BackdropTags,
		CommunityRating: meta.CommunityRating,
		RecursiveCount:  progress.unplayedCount,
		ChildCount:      progress.childCount,
		Status:          progress.status,
		UnplayedCount:   progress.unplayedCount,
		PosterURL:       posterURL,
	})
	if hist != nil {
		source.DateCreated = resumeDateCreated(*hist)
	}
	if hist != nil && hist.PlaybackRuntimeTicks > 0 {
		source.RunTimeTicks = hist.PlaybackRuntimeTicks
	}
	return buildTVLatestItemDTO(serverID, source), true
}

func buildHistorySiteLatestItem(database *db.DB, serverID string, row db.PlayHistoryRow) (TVLatestItemDTO, bool) {
	if database == nil {
		return TVLatestItemDTO{}, false
	}
	id := buildSiteSeriesID(strings.TrimSpace(row.SiteKey), strings.TrimSpace(row.SiteDetail))
	if id == "" {
		return TVLatestItemDTO{}, false
	}
	name := strings.TrimSpace(row.ContentKey)
	if name == "" {
		return TVLatestItemDTO{}, false
	}
	posterURL := rewriteRedirectImageURL(database, row.Poster)
	if strings.TrimSpace(posterURL) == "" {
		return TVLatestItemDTO{}, false
	}
	source := NormalizeTVLatestSource(tvLatestSource{
		ID:             id,
		Name:           name,
		DateCreated:    resumeDateCreated(row),
		SortName:       SortNameOrName(name),
		PremiereDate:   "",
		Path:           "",
		Overview:       "",
		Genres:         EmptyStrings(),
		RunTimeTicks:   row.PlaybackRuntimeTicks,
		ProductionYear: 0,
		ProviderIDs:    EmptyAnyMap(),
		ParentID:       strings.TrimSpace(row.PlaybackItemID),
		GenreItems:     EmptyNamedIDs(),
		AspectRatio:    0.6666667,
		PrimaryTag:     StableMD5Hex(id + "|primary"),
		LogoTag:        StableMD5Hex(id + "|logo"),
		BackdropTags:   EmptyStrings(),
		RecursiveCount: 0,
		ChildCount:     0,
		Status:         "",
		UnplayedCount:  0,
		PosterURL:      posterURL,
	})
	return buildTVLatestItemDTO(serverID, source), true
}

type historyTVProgress struct {
	childCount    int
	status        string
	unplayedCount int
}

var (
	reDoubanTVSeasonCN         = regexp.MustCompile(`第\s*([0-9０-９一二三四五六七八九十百千两零〇]{1,16})\s*季`)
	reDoubanTVSeasonEN         = regexp.MustCompile(`(?i)\b(?:Season|S)\s*([0-9]{1,3})\b`)
	reDoubanTVSeasonTotal      = regexp.MustCompile(`共\s*([0-9０-９一二三四五六七八九十百千两零〇]{1,16})\s*季\s*([0-9０-９一二三四五六七八九十百千两零〇]{1,16})\s*集`)
	reDoubanTVYearFan          = regexp.MustCompile(`年番\s*([0-9０-９一二三四五六七八九十百千两零〇]{1,16})`)
	reHistoryTVSeasonEpisodeCN = regexp.MustCompile(`第\s*(\d{1,3})\s*季\s*第\s*(\d{1,5})\s*(?:集|话|回|期)`)
	reHistoryTVSeasonEpisodeSE = regexp.MustCompile(`(?i)\bS\s*(\d{1,3})\s*E\s*(\d{1,5})\b`)
	reHistoryTVUpdateEpisode   = regexp.MustCompile(`更新至\s*(\d{1,6})\s*(?:集|话|回|期)`)
	reHistoryTVTotalOnly       = regexp.MustCompile(`共\s*(\d{1,6})\s*(?:集|话|回|期)`)
	reHistoryTVSeasonTotal     = regexp.MustCompile(`共\s*(\d{1,3})\s*季\s*(\d{1,6})\s*集`)
	reHistoryTVTitleSeasonCN   = regexp.MustCompile(`第\s*(\d{1,3})\s*季`)
	reHistoryTVTitleSeasonSE   = regexp.MustCompile(`(?i)\bS\s*(\d{1,3})\b`)
	reHistoryTVTitleSeasonEN   = regexp.MustCompile(`(?i)\bSeason\s*(\d{1,3})\b`)
)

func parseHistoryTVRemark(remark string, title string) historyTVProgress {
	raw := strings.TrimSpace(remark)
	latestSeason, latestEpisode, latestKind := parseHistoryTVLatest(raw)
	totalSeasons, totalEpisodes := parseHistoryTVTotals(raw)
	titleSeason := parseHistoryTVTitleSeason(strings.TrimSpace(title))
	if latestKind == "unseasoned" {
		if titleSeason > 0 {
			latestSeason = titleSeason
		} else if totalSeasons <= 1 {
			latestSeason = 1
		} else {
			latestSeason = 0
			latestEpisode = 0
		}
	}
	childCount := maxInt(totalSeasons, latestSeason)
	status := ""
	if latestEpisode > 0 {
		status = "Continuing"
	}
	unplayed := 0
	if totalEpisodes > 0 {
		unplayed = totalEpisodes
	}
	return historyTVProgress{childCount: childCount, status: status, unplayedCount: unplayed}
}

func parseHistoryTVLatest(raw string) (int, int, string) {
	if raw == "" {
		return 0, 0, ""
	}
	if m := reHistoryTVSeasonEpisodeCN.FindStringSubmatch(raw); len(m) == 3 {
		return parsePositiveInt(m[1]), parsePositiveInt(m[2]), "seasoned"
	}
	if m := reHistoryTVSeasonEpisodeSE.FindStringSubmatch(raw); len(m) == 3 {
		return parsePositiveInt(m[1]), parsePositiveInt(m[2]), "seasoned"
	}
	if m := reHistoryTVUpdateEpisode.FindStringSubmatch(raw); len(m) == 2 {
		return 0, parsePositiveInt(m[1]), "unseasoned"
	}
	return 0, 0, ""
}

func parseHistoryTVTotals(raw string) (int, int) {
	if raw == "" {
		return 0, 0
	}
	if m := reHistoryTVSeasonTotal.FindStringSubmatch(raw); len(m) == 3 {
		return parsePositiveInt(m[1]), parsePositiveInt(m[2])
	}
	if m := reHistoryTVTotalOnly.FindStringSubmatch(raw); len(m) == 2 {
		return 1, parsePositiveInt(m[1])
	}
	return 0, 0
}

func parseHistoryTVTitleSeason(raw string) int {
	if raw == "" {
		return 0
	}
	if m := reHistoryTVTitleSeasonCN.FindStringSubmatch(raw); len(m) == 2 {
		return parsePositiveInt(m[1])
	}
	if m := reHistoryTVTitleSeasonSE.FindStringSubmatch(raw); len(m) == 2 {
		return parsePositiveInt(m[1])
	}
	if m := reHistoryTVTitleSeasonEN.FindStringSubmatch(raw); len(m) == 2 {
		return parsePositiveInt(m[1])
	}
	return 0
}

func parseDoubanSeasonCount(title string, subtitle string) int {
	values := []string{strings.TrimSpace(title), strings.TrimSpace(subtitle)}
	maxSeason := 0
	for _, raw := range values {
		if raw == "" {
			continue
		}
		if m := reDoubanTVSeasonCN.FindStringSubmatch(raw); len(m) == 2 {
			maxSeason = maxInt(maxSeason, parseLoosePositiveInt(m[1]))
		}
		if m := reDoubanTVSeasonEN.FindStringSubmatch(raw); len(m) == 2 {
			maxSeason = maxInt(maxSeason, parseLoosePositiveInt(m[1]))
		}
		if m := reDoubanTVSeasonTotal.FindStringSubmatch(raw); len(m) == 3 {
			maxSeason = maxInt(maxSeason, parseLoosePositiveInt(m[1]))
		}
		if m := reDoubanTVYearFan.FindStringSubmatch(raw); len(m) == 2 {
			maxSeason = maxInt(maxSeason, parseLoosePositiveInt(m[1]))
		}
	}
	if maxSeason <= 0 {
		return 1
	}
	return maxSeason
}

func parsePositiveInt(raw string) int {
	n, err := strconv.Atoi(strings.TrimSpace(raw))
	if err != nil || n <= 0 {
		return 0
	}
	return n
}

func parseLoosePositiveInt(raw string) int {
	s := normalizeDigits(strings.TrimSpace(raw))
	if s == "" {
		return 0
	}
	if n, err := strconv.Atoi(s); err == nil && n > 0 {
		return n
	}
	return parseChinesePositiveInt(s)
}

func normalizeDigits(raw string) string {
	if raw == "" {
		return ""
	}
	var b strings.Builder
	for _, r := range raw {
		if r >= '０' && r <= '９' {
			b.WriteRune(r - '０' + '0')
			continue
		}
		b.WriteRune(r)
	}
	return b.String()
}

func parseChinesePositiveInt(raw string) int {
	s := strings.TrimSpace(raw)
	if s == "" {
		return 0
	}
	values := map[rune]int{'零': 0, '〇': 0, '一': 1, '二': 2, '两': 2, '三': 3, '四': 4, '五': 5, '六': 6, '七': 7, '八': 8, '九': 9}
	total := 0
	current := 0
	for _, r := range s {
		switch r {
		case '十':
			if current == 0 {
				current = 1
			}
			total += current * 10
			current = 0
		default:
			v, ok := values[r]
			if !ok {
				return 0
			}
			current = v
		}
	}
	total += current
	if total <= 0 {
		return 0
	}
	return total
}
