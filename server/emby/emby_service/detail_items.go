package emby_service

import (
	"fmt"
	"path/filepath"
	"strconv"
	"strings"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/cache"
	"github.com/jenfonro/meowfilm/server/catpawrunner"
	metadata_tmdb "github.com/jenfonro/meowfilm/server/metadata/tmdb"
	"github.com/jenfonro/meowfilm/server/smart"
)

func BuildUserItemDetailPayload(database *db.DB, userID int64, serverID string, itemID string) (any, bool, error) {
	ref := parseItemRefAny(itemID)
	if ref == nil {
		return nil, false, nil
	}
	if ref.Source == "site" {
		switch ref.SubKind {
		case "series":
			return buildSiteSeriesDetailPayload(database, userID, serverID, ref)
		case "episode":
			return buildSiteEpisodeDetailPayload(database, userID, serverID, ref)
		default:
			return nil, false, nil
		}
	}
	if ref.Source != "tmdb" {
		return nil, false, nil
	}
	switch {
	case ref.MediaType == "movie" && ref.SubKind == "movie":
		return buildMovieDetailPayload(database, userID, serverID, ref)
	case ref.MediaType == "tv" && ref.SubKind == "series":
		return buildSeriesDetailPayload(database, userID, serverID, ref)
	case ref.MediaType == "tv" && ref.SubKind == "episode":
		return buildEpisodeDetailPayload(database, userID, serverID, ref)
	default:
		return nil, false, nil
	}
}

type siteDetailMeta struct {
	Name     string
	Pic      string
	Remark   string
	Overview string
	Year     int
}

func buildSiteSeriesDetailPayload(database *db.DB, userID int64, serverID string, ref *itemRef) (any, bool, error) {
	if database == nil || ref == nil {
		return nil, false, nil
	}
	meta, err := fetchSiteDetailMeta(database, userID, ref.SiteKey, ref.SiteDetail)
	if err != nil {
		return nil, false, err
	}
	overview := strings.TrimSpace(meta.Overview)
	itemID := buildSiteSeriesID(strings.TrimSpace(ref.SiteKey), strings.TrimSpace(ref.SiteDetail))
	childCount := 0
	if pans, err := fetchResolvedSiteDetailPans(database, userID, ref.SiteKey, ref.SiteDetail); err == nil {
		childCount = len(pans)
	}
	updatedAt := time.Now().Unix()
	genres, genreItems := EmptyGenresAndItems()
	dateCreated, dateModified := ProtocolDatePairFromUnix(updatedAt)
	state := SeriesItemState(true, false)
	return SeriesDetailItemDTO{
		Name:                    "",
		ServerID:                strings.TrimSpace(serverID),
		ID:                      itemID,
		Etag:                    StableItemEtag(itemID),
		DateCreated:             dateCreated,
		DateModified:            dateModified,
		CanDelete:               state.CanDelete,
		CanDownload:             state.CanDownload,
		PresentationUniqueKey:   StablePresentationUniqueKey(itemID),
		SupportsSync:            state.SupportsSync,
		SortName:                SortNameOrName(""),
		ForcedSortName:          "",
		PremiereDate:            yearDateString(meta.Year),
		ExternalURLs:            EmptyExternalURLs(),
		Path:                    "",
		Overview:                overview,
		Taglines:                EmptyStrings(),
		Genres:                  genres,
		RunTimeTicks:            0,
		FileName:                "",
		ProductionYear:          meta.Year,
		RemoteTrailers:          EmptyRemoteTrailers(),
		ProviderIDs:             EmptyStringMap(),
		IsFolder:                state.IsFolder,
		ParentID:                "",
		Type:                    state.Type,
		People:                  EmptyPeople(),
		Studios:                 EmptyNamedIDs(),
		GenreItems:              genreItems,
		TagItems:                EmptyNamedIDs(),
		LocalTrailerCount:       0,
		UserData:                EmptyTVLatestUserData(),
		ChildCount:              childCount,
		DisplayPreferencesID:    StableDisplayPreferencesID(itemID),
		Status:                  "",
		AirDays:                 EmptyStrings(),
		PrimaryImageAspectRatio: 0.6666667,
		DisplayOrder:            "Aired",
		ImageTags:               ImageTagsForItem(itemID, true),
		BackdropImageTags:       EmptyStrings(),
		LockedFields:            EmptyLockedFields(),
		LockData:                false,
	}, true, nil
}

func buildSiteEpisodeDetailPayload(database *db.DB, userID int64, serverID string, ref *itemRef) (any, bool, error) {
	if database == nil || ref == nil || strings.TrimSpace(ref.SiteKey) == "" || strings.TrimSpace(ref.SiteDetail) == "" || strings.TrimSpace(ref.SiteTitle) == "" || ref.Pan <= 0 || ref.Episode <= 0 {
		return nil, false, nil
	}
	meta, err := fetchSiteDetailMeta(database, userID, ref.SiteKey, ref.SiteDetail)
	if err != nil {
		return nil, false, err
	}
	seriesName := strings.TrimSpace(ref.SiteTitle)
	if seriesName == "" {
		seriesName = resolveSiteSeriesName(database, ref.SiteKey, ref.SiteDetail, meta)
	}
	if seriesName == "" {
		seriesName = strings.TrimSpace(meta.Name)
	}
	if seriesName == "" {
		return nil, false, nil
	}
	ep := catpawrunner.Episode{
		Name: "",
		URL:  strings.TrimSpace(ref.SiteEpisodeURL),
		Flag: strings.TrimSpace(ref.SitePlayFlag),
	}
	panMock := strings.TrimSpace(smart.PanMockProviderFromLabel(strings.TrimSpace(ref.SitePlayFlag))) != ""
	name := siteEpisodeDisplayName(ep, strings.TrimSpace(ref.SitePlayFlag), panMock, seriesName, ref.Episode)
	if name == "" {
		name = siteEpisodeFileName(ep, "", ref.Episode)
	}
	rawFileName := siteEpisodeFileName(ep, name, ref.Episode)
	container := siteEpisodeContainerFromName(rawFileName)
	fileName := siteEpisodePlayableFileName(rawFileName, container)
	path := VirtualEpisodePath(seriesName, meta.Year, ref.Pan, fileName)
	itemID := strings.TrimSpace(ref.RawID)
	row := resolveSiteEpisodeHistoryRowForDetail(database, userID, ref, ep)
	seriesID := buildSiteSeriesID(ref.SiteKey, ref.SiteDetail)
	seasonID := buildSiteSeasonID(ref.SiteKey, ref.SiteDetail, ref.Pan)
	dateCreated := ProtocolCreatedDate(0)
	dateModified := dateCreated
	if dateModified == "" {
		dateModified = EmbyZeroTimeString()
	}
	chapters := EmptyDetailChapters()
	runtime := int64(0)
	if row != nil {
		runtime = maxInt64(0, row.PlaybackRuntimeTicks)
		if runtime <= 0 && row.PlaybackPositionTicks > 0 {
			runtime = row.PlaybackPositionTicks + int64(60*10_000_000)
		}
	}
	mediaSources := detailMediaSources(itemID, path, fileName, runtime, firstNonEmptyString(container, "mp4"), chapters)
	mediaStreams := EmptyAnySlice()
	if len(mediaSources) > 0 && mediaSources[0].MediaStreams != nil {
		mediaStreams = mediaSources[0].MediaStreams
	}
	fileBase := filepath.Base(strings.TrimSpace(path))
	if fileBase == "." || fileBase == "/" || fileBase == "" {
		fileBase = strings.TrimSpace(name)
	}
	return EpisodeDetailItemDTO{
		Name:                    name,
		ServerID:                strings.TrimSpace(serverID),
		ID:                      itemID,
		Etag:                    StableItemEtag(itemID),
		DateCreated:             dateCreated,
		DateModified:            dateModified,
		CanDelete:               true,
		CanDownload:             true,
		PresentationUniqueKey:   StablePresentationUniqueKey(itemID),
		SupportsSync:            true,
		Container:               firstNonEmptyString(container, "mp4"),
		SortName:                SortNameOrName(name),
		ForcedSortName:          SortNameOrName(name),
		PremiereDate:            "",
		ExternalURLs:            EmptyExternalURLs(),
		MediaSources:            mediaSources,
		AlternateMediaSources:   EmptyAnySlice(),
		Path:                    path,
		Overview:                "",
		Taglines:                EmptyStrings(),
		Genres:                  EmptyStrings(),
		CommunityRating:         0,
		OfficialRating:          "",
		RunTimeTicks:            runtime,
		Size:                    0,
		FileName:                fileBase,
		Bitrate:                 0,
		ProductionYear:          meta.Year,
		IndexNumber:             ref.Episode,
		ParentIndexNumber:       ref.Pan,
		RemoteTrailers:          EmptyRemoteTrailers(),
		ProviderIDs:             EmptyAnyMap(),
		IsFolder:                false,
		ParentID:                seasonID,
		Type:                    "Episode",
		People:                  EmptyPeople(),
		Studios:                 EmptyNamedIDs(),
		GenreItems:              EmptyNamedIDs(),
		TagItems:                EmptyNamedIDs(),
		ParentLogoItemID:        seriesID,
		ParentBackdropItemID:    seriesID,
		ParentBackdropImageTags: EmptyStrings(),
		LocalTrailerCount:       0,
		UserData:                BuildMovieDetailUserData(row),
		SeriesName:              seriesName,
		SeriesID:                seriesID,
		SeasonID:                seasonID,
		DisplayPreferencesID:    StableDisplayPreferencesID(itemID),
		PrimaryImageAspectRatio: nextUpPrimaryAspectRatio,
		SeriesPrimaryImageTag:   SeriesPrimaryImageTag(seriesID),
		SeasonName:              fmt.Sprintf("Season %d", ref.Pan),
		MediaStreams:            mediaStreams,
		ImageTags:               ImageTagsForItem(itemID, true),
		BackdropImageTags:       EmptyStrings(),
		ParentLogoImageTag:      ParentLogoImageTag(seriesID),
		Chapters:                chapters,
		MediaType:               "Video",
		LockedFields:            EmptyLockedFields(),
		LockData:                false,
		Width:                   0,
		Height:                  0,
	}, true, nil
}

func resolveSiteEpisodeHistoryRowForDetail(database *db.DB, userID int64, ref *itemRef, ep catpawrunner.Episode) *db.PlayHistoryRow {
	if database == nil || ref == nil || userID <= 0 {
		return nil
	}
	itemID := strings.TrimSpace(ref.RawID)
	if itemID != "" {
		if row, _ := database.GetPlayHistoryLatestByPlaybackItemID(userID, itemID); row != nil {
			return row
		}
	}
	hist, _ := database.GetPlayHistoryLatestBySiteVideo(userID, ref.SiteKey, ref.SiteDetail)
	if !siteHistoryMatchesEpisodeRef(hist, ref, ep) {
		return nil
	}
	return hist
}

func siteHistoryMatchesEpisodeRef(hist *db.PlayHistoryRow, ref *itemRef, ep catpawrunner.Episode) bool {
	if hist == nil || ref == nil {
		return false
	}
	if strings.TrimSpace(hist.PlaybackItemID) != "" {
		if current := parseItemRefAny(strings.TrimSpace(hist.PlaybackItemID)); current != nil &&
			current.Source == "site" &&
			current.SubKind == "episode" &&
			strings.TrimSpace(current.SiteKey) == strings.TrimSpace(ref.SiteKey) &&
			strings.TrimSpace(current.SiteDetail) == strings.TrimSpace(ref.SiteDetail) &&
			current.Pan == ref.Pan &&
			current.Episode == ref.Episode {
			return true
		}
	}
	if strings.TrimSpace(hist.SiteEpisodeFile) != "" && siteHistoryEpisodeFileMatches(strings.TrimSpace(hist.SiteEpisodeFile), ep, ref.Episode) {
		return true
	}
	if hist.SiteEpisodeIndex > 0 && hist.SiteEpisodeIndex == ref.Episode {
		return true
	}
	return false
}

func fetchSiteDetailMeta(database *db.DB, userID int64, siteKey string, siteDetail string) (siteDetailMeta, error) {
	if database == nil || strings.TrimSpace(siteKey) == "" || strings.TrimSpace(siteDetail) == "" {
		return siteDetailMeta{}, nil
	}
	spiderAPI := strings.TrimSpace(smart.ResolveSpiderAPIBySiteKey(database, siteKey))
	if spiderAPI == "" {
		return siteDetailMeta{}, nil
	}
	apiBase := strings.TrimSpace(smart.ResolveCatApiBaseForUser(database, &smart.User{ID: fmt.Sprintf("%d", userID)}))
	if apiBase == "" {
		return siteDetailMeta{}, nil
	}
	raw, err := cache.RequestSpiderDetailDirect(apiBase, spiderAPI, strings.TrimSpace(siteDetail))
	if err != nil || raw == nil {
		return siteDetailMeta{}, err
	}
	return extractSiteDetailMeta(raw), nil
}

func extractSiteDetailMeta(raw map[string]any) siteDetailMeta {
	pick := func(m map[string]any) siteDetailMeta {
		if m == nil {
			return siteDetailMeta{}
		}
		name := strings.TrimSpace(anyString(m["vod_name"]))
		if name == "" {
			name = strings.TrimSpace(anyString(m["name"]))
		}
		pic := strings.TrimSpace(anyString(m["vod_pic"]))
		if pic == "" {
			pic = strings.TrimSpace(anyString(m["pic"]))
		}
		remark := strings.TrimSpace(anyString(m["vod_remarks"]))
		if remark == "" {
			remark = strings.TrimSpace(anyString(m["remark"]))
		}
		overview := strings.TrimSpace(anyString(m["vod_content"]))
		if overview == "" {
			overview = strings.TrimSpace(anyString(m["content"]))
		}
		year, _ := strconv.Atoi(strings.TrimSpace(anyString(m["vod_year"])))
		return siteDetailMeta{Name: name, Pic: pic, Remark: remark, Overview: overview, Year: year}
	}
	if v, ok := raw["list"].([]any); ok && len(v) > 0 {
		if m, ok := v[0].(map[string]any); ok {
			return pick(m)
		}
	}
	if d, ok := raw["data"].(map[string]any); ok {
		if v, ok := d["list"].([]any); ok && len(v) > 0 {
			if m, ok := v[0].(map[string]any); ok {
				return pick(m)
			}
		}
	}
	if m, ok := raw["vod"].(map[string]any); ok {
		return pick(m)
	}
	return siteDetailMeta{}
}

func anyString(v any) string {
	if v == nil {
		return ""
	}
	switch vv := v.(type) {
	case string:
		return vv
	case fmt.Stringer:
		return vv.String()
	default:
		return fmt.Sprintf("%v", v)
	}
}

func buildMovieDetailPayload(database *db.DB, userID int64, serverID string, ref *itemRef) (any, bool, error) {
	detail, err := metadata_tmdb.GetMovieDetails(database, ref.NumericID)
	if err != nil || detail == nil {
		return nil, false, err
	}
	credits, _ := smart.TMDBGetCredits(database, "movie", ref.NumericID)
	row, _ := database.GetPlayHistoryLatestByTMDB(userID, "movie", ref.NumericID)
	itemID := buildMovieID(ref.NumericID)
	nowStamp := detailTime(row)
	title := strings.TrimSpace(detail.Title)
	cachedDetail, _ := metadata_tmdb.GetDetailForBackend(database, "movie", ref.NumericID)
	genres, genreItems := GenresAndItemsFromDetail(cachedDetail, "movie")
	runtime := int64(0)
	if row != nil && row.PlaybackRuntimeTicks > 0 {
		runtime = row.PlaybackRuntimeTicks
	}
	chapters := BuildSyntheticChapters(runtime)
	fileName := fileNameFromHistory(row, title)
	container := detectResumeContainer(fileName)
	path := RealMediaPathOrEmpty(filePathFromHistory(row))
	if path == "" {
		path = VirtualMoviePath(title, metadata_tmdb.ParseYearFromDate(detail.ReleaseDate), fileName)
	}
	dateCreated, dateModified := ProtocolDatePairFromUnix(nowStamp.Unix())
	state := MovieItemState(true, true)
	return MovieDetailItemDTO{
		Name:                    title,
		OriginalTitle:           title,
		ServerID:                strings.TrimSpace(serverID),
		ID:                      itemID,
		Etag:                    StableItemEtag(itemID),
		DateCreated:             dateCreated,
		DateModified:            dateModified,
		CanDelete:               state.CanDelete,
		CanDownload:             state.CanDownload,
		PresentationUniqueKey:   StablePresentationUniqueKey(itemID),
		SupportsSync:            state.SupportsSync,
		Container:               container,
		SortName:                SortNameOrName(title),
		ForcedSortName:          title,
		PremiereDate:            preciseDateString(detail.ReleaseDate),
		ExternalURLs:            BuildExternalURLs("movie", ref.NumericID),
		MediaSources:            detailMediaSources(itemID, path, title, runtime, container, chapters),
		ProductionLocations:     EmptyStrings(),
		Path:                    path,
		Overview:                strings.TrimSpace(detail.Overview),
		Taglines:                EmptyStrings(),
		Genres:                  genres,
		RunTimeTicks:            runtime,
		FileName:                fileName,
		ProductionYear:          metadata_tmdb.ParseYearFromDate(detail.ReleaseDate),
		RemoteTrailers:          EmptyRemoteTrailers(),
		ProviderIDs:             ProviderIDsFromTMDBString(ref.NumericID),
		ParentID:                "",
		Type:                    state.Type,
		People:                  buildPeople(credits),
		Studios:                 EmptyNamedIDs(),
		GenreItems:              genreItems,
		TagItems:                EmptyNamedIDs(),
		LocalTrailerCount:       0,
		UserData:                BuildMovieDetailUserData(row),
		DisplayPreferencesID:    StableDisplayPreferencesID(itemID),
		PrimaryImageAspectRatio: PosterAspectRatio(detail.PosterPath),
		MediaStreams:            EmptyAnySlice(),
		PartCount:               1,
		ImageTags:               ImageTagsForItem(itemID, true),
		BackdropImageTags:       BackdropTagsOrEmpty(backdropTagsFromAsset(detail.Backdrop)),
		Chapters:                chapters,
		MediaType:               MediaTypeVideo,
		LockedFields:            EmptyLockedFields(),
		LockData:                false,
		Width:                   0,
		Height:                  0,
	}, true, nil
}

func buildSeriesDetailPayload(database *db.DB, userID int64, serverID string, ref *itemRef) (any, bool, error) {
	detail, err := metadata_tmdb.GetDetailForBackend(database, "tv", ref.NumericID)
	if err != nil || detail == nil {
		return nil, false, err
	}
	credits, _ := smart.TMDBGetCredits(database, "tv", ref.NumericID)
	row, _ := database.GetPlayHistoryLatestByTMDB(userID, "tv", ref.NumericID)
	itemID := buildSeriesID(ref.NumericID)
	nowStamp := detailTime(row)
	title := strings.TrimSpace(detail.Title)
	genres, genreItems := GenresAndItemsFromDetail(detail, "tv")
	runtime := int64(0)
	if row != nil && row.PlaybackRuntimeTicks > 0 {
		runtime = row.PlaybackRuntimeTicks
	}
	childCount := len(detail.Seasons) + 1
	userData := BuildSeriesUserData(row)
	if nextUp, err := loadTVNextUpView(database, ref.NumericID, row, 1); err == nil && nextUp != nil && len(nextUp.Candidates) > 0 {
		userData.UnplayedItemCount = len(nextUp.Candidates)
	}
	path := ""
	if path == "." || path == "" {
		path = VirtualSeriesPath(title, parseTMDBCachedYear(detail))
	}
	if path == "." {
		path = ""
	}
	dateCreated, dateModified := ProtocolDatePairFromUnix(nowStamp.Unix())
	state := SeriesItemState(true, false)
	return SeriesDetailItemDTO{
		Name:                    title,
		ServerID:                strings.TrimSpace(serverID),
		ID:                      itemID,
		Etag:                    StableItemEtag(itemID),
		DateCreated:             dateCreated,
		DateModified:            dateModified,
		CanDelete:               state.CanDelete,
		CanDownload:             state.CanDownload,
		PresentationUniqueKey:   StablePresentationUniqueKey(itemID),
		SupportsSync:            state.SupportsSync,
		SortName:                SortNameOrName(title),
		ForcedSortName:          title,
		PremiereDate:            preciseDateString(strings.TrimSpace(detail.FirstAir)),
		ExternalURLs:            BuildExternalURLs("tv", ref.NumericID),
		Path:                    path,
		Overview:                strings.TrimSpace(detail.Overview),
		Taglines:                EmptyStrings(),
		Genres:                  genres,
		RunTimeTicks:            runtime,
		FileName:                title,
		ProductionYear:          parseTMDBCachedYear(detail),
		RemoteTrailers:          EmptyRemoteTrailers(),
		ProviderIDs:             ProviderIDsFromTMDBString(ref.NumericID),
		IsFolder:                state.IsFolder,
		ParentID:                "",
		Type:                    state.Type,
		People:                  buildPeople(credits),
		Studios:                 EmptyNamedIDs(),
		GenreItems:              genreItems,
		TagItems:                EmptyNamedIDs(),
		LocalTrailerCount:       0,
		UserData:                userData,
		ChildCount:              childCount,
		DisplayPreferencesID:    StableDisplayPreferencesID(itemID),
		Status:                  strings.TrimSpace(detail.Status),
		AirDays:                 EmptyStrings(),
		PrimaryImageAspectRatio: PosterAspectRatio(detail.PosterPath),
		DisplayOrder:            "Aired",
		ImageTags:               ImageTagsForItem(itemID, true),
		BackdropImageTags:       BackdropTagsOrEmpty(backdropTagsFromAsset(detail.Backdrop)),
		LockedFields:            EmptyLockedFields(),
		LockData:                false,
	}, true, nil
}

func buildEpisodeDetailPayload(database *db.DB, userID int64, serverID string, ref *itemRef) (any, bool, error) {
	if ref == nil || ref.NumericID <= 0 || ref.Pan < 0 || ref.Episode <= 0 {
		return nil, false, nil
	}
	seriesRef := &itemRef{
		Kind:      "item",
		SubKind:   "series",
		MediaType: "tv",
		Source:    "tmdb",
		RawID:     buildSeriesID(ref.NumericID),
		NumericID: ref.NumericID,
	}
	var (
		items []episodeListSource
		ok    bool
		err   error
	)
	row, _ := database.GetPlayHistoryLatestByTMDB(userID, "tv", ref.NumericID)
	includeUnaired := row != nil && row.PreOrder
	if strings.TrimSpace(ref.Variant) == "settings" {
		items, ok, err = buildTMDBSettingsEpisodeSources(database, userID, serverID, seriesRef)
	} else {
		items, ok, err = buildTMDBSeasonEpisodeSources(database, userID, serverID, seriesRef, ref.Pan, includeUnaired)
	}
	if err != nil || !ok {
		return nil, ok, err
	}
	for _, item := range items {
		if item.IndexNumber != ref.Episode {
			continue
		}
		if !playHistoryMatchesTMDBEpisode(row, ref.NumericID, ref.Pan, ref.Episode) {
			row = nil
		}
		state := EpisodeItemState(true, true)
		dateModified := item.DateCreated
		if dateModified == "" {
			dateModified = EmbyZeroTimeString()
		}
		fileName := filepath.Base(strings.TrimSpace(item.Path))
		if fileName == "." || fileName == "/" || fileName == "" {
			fileName = episodeFileName(db.TMDBCachedSeasonEpisode{Name: item.Name}, ref.Pan)
		}
		return EpisodeDetailItemDTO{
			Name:                    item.Name,
			ServerID:                item.ServerID,
			ID:                      item.ID,
			Etag:                    item.Etag,
			DateCreated:             item.DateCreated,
			DateModified:            dateModified,
			CanDelete:               state.CanDelete,
			CanDownload:             state.CanDownload,
			PresentationUniqueKey:   StablePresentationUniqueKey(item.ID),
			SupportsSync:            state.SupportsSync,
			Container:               item.Container,
			SortName:                item.SortName,
			ForcedSortName:          item.SortName,
			PremiereDate:            item.PremiereDate,
			ExternalURLs:            EmptyExternalURLs(),
			MediaSources:            item.MediaSources,
			AlternateMediaSources:   item.AlternateMediaSources,
			Path:                    item.Path,
			Overview:                item.Overview,
			Taglines:                EmptyStrings(),
			Genres:                  item.Genres,
			CommunityRating:         item.CommunityRating,
			OfficialRating:          item.OfficialRating,
			RunTimeTicks:            item.RunTimeTicks,
			Size:                    item.Size,
			FileName:                fileName,
			Bitrate:                 item.Bitrate,
			ProductionYear:          item.ProductionYear,
			IndexNumber:             item.IndexNumber,
			ParentIndexNumber:       item.ParentIndexNumber,
			RemoteTrailers:          EmptyRemoteTrailers(),
			ProviderIDs:             item.ProviderIDs,
			IsFolder:                item.IsFolder,
			ParentID:                item.ParentID,
			Type:                    item.Type,
			People:                  item.People,
			Studios:                 item.Studios,
			GenreItems:              item.GenreItems,
			TagItems:                EmptyNamedIDs(),
			ParentLogoItemID:        item.ParentLogoItemID,
			ParentBackdropItemID:    item.ParentBackdropItemID,
			ParentBackdropImageTags: item.ParentBackdropImageTags,
			LocalTrailerCount:       0,
			UserData:                BuildMovieDetailUserData(row),
			SeriesName:              item.SeriesName,
			SeriesID:                item.SeriesID,
			SeasonID:                item.SeasonID,
			DisplayPreferencesID:    StableDisplayPreferencesID(item.ID),
			PrimaryImageAspectRatio: nextUpPrimaryAspectRatio,
			SeriesPrimaryImageTag:   item.SeriesPrimaryImageTag,
			SeasonName:              item.SeasonName,
			MediaStreams:            item.MediaStreams,
			ImageTags:               item.ImageTags,
			BackdropImageTags:       item.BackdropImageTags,
			ParentLogoImageTag:      item.ParentLogoImageTag,
			Chapters:                item.Chapters,
			MediaType:               item.MediaType,
			LockedFields:            EmptyLockedFields(),
			LockData:                false,
			Width:                   0,
			Height:                  0,
		}, true, nil
	}
	return nil, false, nil
}

func parseTMDBCachedYear(detail *db.TMDBCachedDetail) int {
	if detail == nil {
		return 0
	}
	raw := strings.TrimSpace(detail.FirstAir)
	if raw == "" {
		raw = strings.TrimSpace(detail.Release)
	}
	if len(raw) < 4 {
		return 0
	}
	year, err := strconv.Atoi(raw[:4])
	if err != nil || year <= 0 {
		return 0
	}
	return year
}

func buildPeople(credits *smart.TMDBCredits) []PersonDTO {
	if credits == nil {
		return []PersonDTO{}
	}
	out := make([]PersonDTO, 0, len(credits.Cast)+len(credits.Crew))
	for _, cast := range credits.Cast {
		p := PersonDTO{
			Name: cast.Name,
			ID:   strconv.Itoa(cast.ID),
			Role: cast.Role,
			Type: "Actor",
		}
		if strings.TrimSpace(cast.Profile) != "" {
			p.PrimaryImageTag = StableMD5Hex(fmt.Sprintf("person:%d", cast.ID))
		}
		out = append(out, p)
	}
	for _, crew := range credits.Crew {
		roleType := ""
		switch strings.ToLower(strings.TrimSpace(crew.Job)) {
		case "director":
			roleType = "Director"
		case "writer", "screenplay":
			roleType = "Writer"
		case "producer", "executive producer":
			roleType = "Producer"
		}
		if roleType == "" {
			continue
		}
		p := PersonDTO{
			Name: crew.Name,
			ID:   strconv.Itoa(crew.ID),
			Role: crew.Job,
			Type: roleType,
		}
		if strings.TrimSpace(crew.Profile) != "" {
			p.PrimaryImageTag = StableMD5Hex(fmt.Sprintf("person:%d", crew.ID))
		}
		out = append(out, p)
	}
	return out
}

func detailMediaSources(itemID string, path string, name string, runtime int64, container string, chapters []DetailChapterDTO) []DetailMediaSourceDTO {
	if strings.TrimSpace(path) == "" {
		return EmptyDetailMediaSources()
	}
	return []DetailMediaSourceDTO{buildDetailMediaSource(itemID, path, name, runtime, container, chapters)}
}

func buildDetailMediaSource(itemID string, path string, name string, runtime int64, container string, chapters []DetailChapterDTO) DetailMediaSourceDTO {
	return DetailMediaSourceDTO{
		Chapters:                   chapters,
		Protocol:                   "File",
		ID:                         StableMD5Hex(itemID + "|mediasource"),
		Path:                       strings.TrimSpace(path),
		Type:                       "Default",
		Container:                  container,
		Size:                       0,
		Name:                       strings.TrimSpace(name),
		IsRemote:                   false,
		HasMixedProtocols:          false,
		RunTimeTicks:               runtime,
		SupportsTranscoding:        true,
		SupportsDirectStream:       true,
		SupportsDirectPlay:         true,
		IsInfiniteStream:           false,
		RequiresOpening:            false,
		RequiresClosing:            false,
		RequiresLooping:            false,
		SupportsProbing:            true,
		MediaStreams:               EmptyAnySlice(),
		Formats:                    EmptyAnySlice(),
		Bitrate:                    0,
		RequiredHTTPHeaders:        EmptyRequiredHTTPHeaders(),
		AddAPIKeyToDirectStreamURL: false,
		ReadAtNativeFramerate:      false,
		DefaultAudioStreamIndex:    0,
		ItemID:                     itemID,
	}
}

func yearDateString(year int) string {
	if year <= 0 {
		return ""
	}
	return fmt.Sprintf("%04d-01-01T00:00:00.0000000Z", year)
}

func preciseDateString(raw string) string {
	s := strings.TrimSpace(raw)
	if s == "" {
		return ""
	}
	if len(s) >= 10 {
		if t, err := time.Parse("2006-01-02", s[:10]); err == nil {
			return t.Format("2006-01-02T15:04:05.0000000Z")
		}
	}
	year := 0
	if len(s) >= 4 {
		if y, err := strconv.Atoi(s[:4]); err == nil && y > 0 {
			year = y
		}
	}
	return yearDateString(year)
}

func detailTime(row *db.PlayHistoryRow) time.Time {
	if row != nil && row.UpdatedAt > 0 {
		return time.Unix(row.UpdatedAt, 0)
	}
	return time.Time{}
}

func fileNameFromHistory(row *db.PlayHistoryRow, fallback string) string {
	if row != nil {
		if name := strings.TrimSpace(row.SiteEpisodeFile); name != "" {
			return name
		}
	}
	base := strings.TrimSpace(fallback)
	if base == "" {
		base = "video"
	}
	return base + filepath.Ext(".mp4")
}

func filePathFromHistory(row *db.PlayHistoryRow) string {
	if row == nil {
		return ""
	}
	return strings.TrimSpace(row.SiteEpisodeFile)
}
