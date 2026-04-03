package emby_service

import (
	"fmt"
	"path/filepath"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/cache"
	"github.com/jenfonro/meowfilm/server/catpawrunner"
	metadata_tmdb "github.com/jenfonro/meowfilm/server/metadata/tmdb"
	"github.com/jenfonro/meowfilm/server/smart"
)

type seasonListSource struct {
	Name                    string
	ServerID                string
	ID                      string
	SupportsSync            bool
	PremiereDate            string
	Overview                string
	CommunityRating         float64
	ProductionYear          int
	EndDate                 string
	Container               string
	Genres                  []string
	IndexNumber             int
	IsFolder                bool
	ParentID                string
	Type                    string
	GenreItems              []NamedIDDTO
	People                  []PersonDTO
	ParentLogoItemID        string
	ParentBackdropItemID    string
	ParentBackdropImageTags []string
	UserData                TVLatestUserDataDTO
	ChildCount              int
	SeriesName              string
	SeriesID                string
	SeriesPrimaryImageTag   string
	ImageTags               ImageTagsDTO
	BackdropImageTags       []string
	ParentLogoImageTag      string
}

type episodeListSource struct {
	Name                    string
	ServerID                string
	ID                      string
	Etag                    string
	DateCreated             string
	CanDownload             bool
	SupportsSync            bool
	Container               string
	SortName                string
	PremiereDate            string
	MediaSources            []DetailMediaSourceDTO
	AlternateMediaSources   []any
	Path                    string
	Overview                string
	Genres                  []string
	CommunityRating         float64
	OfficialRating          string
	RunTimeTicks            int64
	Size                    int64
	Bitrate                 int
	ProductionYear          int
	IndexNumber             int
	ParentIndexNumber       int
	ProviderIDs             map[string]any
	IsFolder                bool
	ParentID                string
	Type                    string
	People                  []PersonDTO
	Studios                 []NamedIDDTO
	GenreItems              []NamedIDDTO
	ParentLogoItemID        string
	ParentBackdropItemID    string
	ParentBackdropImageTags []string
	UserData                SimpleUserDataDTO
	SeriesName              string
	SeriesID                string
	SeasonID                string
	SeriesPrimaryImageTag   string
	SeasonName              string
	MediaStreams            []any
	ImageTags               ImageTagsDTO
	BackdropImageTags       []string
	ParentLogoImageTag      string
	Chapters                []DetailChapterDTO
	MediaType               string
}

const tmdbSettingsSeasonName = "设置"

type tmdbSettingsEpisodeDef struct {
	EpisodeNo int
	Name      string
	Overview  string
}

func tmdbSettingsEpisodes() []tmdbSettingsEpisodeDef {
	return []tmdbSettingsEpisodeDef{
		{EpisodeNo: 1, Name: "换源", Overview: "手动切换当前剧集片源。"},
		{EpisodeNo: 2, Name: "片源错误", Overview: "标记当前剧集片源存在问题。"},
		{EpisodeNo: 3, Name: "重置换源", Overview: "清空当前剧集的临时换源列表。"},
		{EpisodeNo: 4, Name: "开启点映", Overview: "开启当前剧集的未播集显示。"},
	}
}

func BuildShowNextUpPayload(database *db.DB, userID int64, serverID string, seriesID string, limit int) (NextUpResponseDTO, bool, error) {
	ref := parseItemRefAny(seriesID)
	if ref != nil && ref.Source == "site" && ref.SubKind == "series" {
		return buildSiteShowNextUpPayload(database, userID, serverID, ref, limit)
	}
	if ref == nil || ref.Source != "tmdb" || ref.MediaType != "tv" || ref.SubKind != "series" {
		return NextUpResponseDTO{Items: []NextUpItemDTO{}, TotalRecordCount: 0}, false, nil
	}
	if limit <= 0 {
		limit = 1
	}
	hist, _ := database.GetPlayHistoryLatestByTMDB(userID, "tv", ref.NumericID)
	if hist == nil {
		return NextUpResponseDTO{Items: []NextUpItemDTO{}, TotalRecordCount: 0}, true, nil
	}
	view, err := loadTVNextUpView(database, ref.NumericID, hist, limit)
	if err != nil || view == nil || view.Series == nil {
		return NextUpResponseDTO{Items: []NextUpItemDTO{}, TotalRecordCount: 0}, false, err
	}
	seriesItemID := buildSeriesID(ref.NumericID)
	parentBackdropTags := backdropTagsFromAsset(view.Series.Backdrop)
	seriesName := strings.TrimSpace(view.Series.Title)
	state := EpisodeItemState(true, false)
	items := make([]NextUpItemDTO, 0, len(view.Candidates))
	for _, candidate := range view.Candidates {
		seasonName := strings.TrimSpace(candidate.SeasonName)
		itemID := buildEpisodeID(ref.NumericID, candidate.Season, candidate.Episode.EpisodeNumber)
		items = append(items, NextUpItemDTO{
			Name:                    episodeName(candidate.Episode),
			ServerID:                strings.TrimSpace(serverID),
			ID:                      itemID,
			CanDelete:               state.CanDelete,
			SupportsSync:            state.SupportsSync,
			PremiereDate:            preciseDateString(strings.TrimSpace(candidate.Episode.AirDate)),
			RunTimeTicks:            RuntimeTicksFromEpisode(candidate.Episode),
			IndexNumber:             candidate.Episode.EpisodeNumber,
			ParentIndexNumber:       candidate.Season,
			IsFolder:                state.IsFolder,
			Type:                    state.Type,
			ParentLogoItemID:        seriesItemID,
			ParentBackdropItemID:    seriesItemID,
			ParentBackdropImageTags: BackdropTagsOrEmpty(parentBackdropTags),
			UserData:                BuildNextUpUserData(candidate.History),
			SeriesName:              seriesName,
			SeriesID:                seriesItemID,
			SeasonID:                buildSeasonID(ref.NumericID, candidate.Season),
			PrimaryImageAspectRatio: nextUpPrimaryAspectRatio,
			SeriesPrimaryImageTag:   SeriesPrimaryImageTag(seriesItemID),
			SeasonName:              seasonName,
			ImageTags:               ImageTagsForItem(itemID, false),
			BackdropImageTags:       BackdropTagsOrEmpty(parentBackdropTags),
			ParentLogoImageTag:      ParentLogoImageTag(seriesItemID),
			MediaType:               MediaTypeVideo,
		})
	}
	return NextUpResponseDTO{Items: items, TotalRecordCount: len(items)}, true, nil
}

func buildSiteShowNextUpPayload(database *db.DB, userID int64, serverID string, ref *itemRef, limit int) (NextUpResponseDTO, bool, error) {
	if database == nil || ref == nil || strings.TrimSpace(ref.SiteKey) == "" || strings.TrimSpace(ref.SiteDetail) == "" {
		return NextUpResponseDTO{Items: []NextUpItemDTO{}, TotalRecordCount: 0}, false, nil
	}
	if limit <= 0 {
		limit = 1
	}
	meta, err := fetchSiteDetailMeta(database, userID, ref.SiteKey, ref.SiteDetail)
	if err != nil {
		return NextUpResponseDTO{Items: []NextUpItemDTO{}, TotalRecordCount: 0}, false, err
	}
	pans, err := fetchResolvedSiteDetailPans(database, userID, ref.SiteKey, ref.SiteDetail)
	if err != nil {
		return NextUpResponseDTO{Items: []NextUpItemDTO{}, TotalRecordCount: 0}, false, err
	}
	type siteEpisodeCursor struct {
		Season     int
		SeasonName string
		EpisodeNo  int
		Episode    catpawrunner.Episode
		ItemID     string
	}
	all := make([]siteEpisodeCursor, 0)
	itemIDs := make([]string, 0)
	for i, pan := range pans {
		seasonNo := i + 1
		seasonName := strings.TrimSpace(pan.DisplayLabel)
		for j, ep := range pan.Episodes {
			epNo := j + 1
			title := strings.TrimSpace(resolveSiteSeriesName(database, ref.SiteKey, ref.SiteDetail, meta))
			if title == "" {
				title = strings.TrimSpace(meta.Name)
			}
			itemID := buildSiteEpisodeID(ref.SiteKey, ref.SiteDetail, seasonNo, epNo, title, firstNonEmptyString(strings.TrimSpace(ep.Flag), strings.TrimSpace(pan.RawLabel)), strings.TrimSpace(ep.URL))
			if itemID == "" {
				continue
			}
			all = append(all, siteEpisodeCursor{
				Season:     seasonNo,
				SeasonName: seasonName,
				EpisodeNo:  epNo,
				Episode:    ep,
				ItemID:     itemID,
			})
			itemIDs = append(itemIDs, itemID)
		}
	}
	if len(all) == 0 {
		return NextUpResponseDTO{Items: []NextUpItemDTO{}, TotalRecordCount: 0}, true, nil
	}
	snaps, err := database.GetPlayHistorySnapshotsByPlaybackItemIDs(userID, itemIDs)
	if err != nil {
		return NextUpResponseDTO{Items: []NextUpItemDTO{}, TotalRecordCount: 0}, false, err
	}
	var latest *siteEpisodeCursor
	var latestSnap db.PlayHistorySnapshot
	for i := range all {
		snap, ok := snaps[all[i].ItemID]
		if !ok || snap.Updated <= 0 {
			continue
		}
		if latest == nil || snap.Updated > latestSnap.Updated {
			latest = &all[i]
			latestSnap = snap
		}
	}
	if latest == nil {
		return NextUpResponseDTO{Items: []NextUpItemDTO{}, TotalRecordCount: 0}, true, nil
	}
	startIdx := 0
	for i := range all {
		if all[i].ItemID != latest.ItemID {
			continue
		}
		startIdx = i
		if siteSnapshotShouldAdvance(latestSnap) && i+1 < len(all) {
			startIdx = i + 1
		}
		break
	}
	seriesItemID := buildSiteSeriesID(ref.SiteKey, ref.SiteDetail)
	seriesName := resolveSiteSeriesName(database, ref.SiteKey, ref.SiteDetail, meta)
	parentBackdropTags := EmptyStrings()
	state := EpisodeItemState(true, false)
	items := make([]NextUpItemDTO, 0, limit)
	for i := startIdx; i < len(all) && len(items) < limit; i++ {
		cursor := all[i]
		snap := snaps[cursor.ItemID]
		name := siteEpisodeDisplayName(cursor.Episode, pans[cursor.Season-1].RawLabel, pans[cursor.Season-1].PanMock, seriesName, cursor.EpisodeNo)
		items = append(items, NextUpItemDTO{
			Name:                    name,
			ServerID:                strings.TrimSpace(serverID),
			ID:                      cursor.ItemID,
			CanDelete:               state.CanDelete,
			SupportsSync:            state.SupportsSync,
			PremiereDate:            "",
			RunTimeTicks:            maxInt64(0, snap.Runtime),
			IndexNumber:             cursor.EpisodeNo,
			ParentIndexNumber:       cursor.Season,
			IsFolder:                state.IsFolder,
			Type:                    state.Type,
			ParentLogoItemID:        seriesItemID,
			ParentBackdropItemID:    seriesItemID,
			ParentBackdropImageTags: BackdropTagsOrEmpty(parentBackdropTags),
			UserData:                BuildNextUpUserDataFromSnapshot(snap),
			SeriesName:              seriesName,
			SeriesID:                seriesItemID,
			SeasonID:                buildSiteSeasonID(ref.SiteKey, ref.SiteDetail, cursor.Season),
			PrimaryImageAspectRatio: nextUpPrimaryAspectRatio,
			SeriesPrimaryImageTag:   SeriesPrimaryImageTag(seriesItemID),
			SeasonName:              cursor.SeasonName,
			ImageTags:               ImageTagsForItem(cursor.ItemID, false),
			BackdropImageTags:       EmptyStrings(),
			ParentLogoImageTag:      ParentLogoImageTag(seriesItemID),
			MediaType:               MediaTypeVideo,
		})
	}
	return NextUpResponseDTO{Items: items, TotalRecordCount: len(items)}, true, nil
}

func BuildShowSeasonsPayload(database *db.DB, userID int64, serverID string, seriesID string) (SeasonsResponseDTO, bool, error) {
	items, ok, err := buildShowSeasonSources(database, userID, serverID, seriesID)
	if err != nil || !ok {
		return SeasonsResponseDTO{Items: []SeasonListItemDTO{}, TotalRecordCount: 0}, ok, err
	}
	return SeasonsResponseDTO{Items: renderSeasonGenresItems(items), TotalRecordCount: len(items)}, true, nil
}

func BuildShowSeasonsBasicPayload(database *db.DB, userID int64, serverID string, seriesID string) (SeasonsBasicResponseDTO, bool, error) {
	items, ok, err := buildShowSeasonSources(database, userID, serverID, seriesID)
	if err != nil || !ok {
		return SeasonsBasicResponseDTO{Items: []SeasonBasicItemDTO{}, TotalRecordCount: 0}, ok, err
	}
	return SeasonsBasicResponseDTO{Items: renderSeasonBasicItems(items), TotalRecordCount: len(items)}, true, nil
}

func BuildShowSeasonsLennaPayload(database *db.DB, userID int64, serverID string, seriesID string) (SeasonsLennaResponseDTO, bool, error) {
	items, ok, err := buildShowSeasonSources(database, userID, serverID, seriesID)
	if err != nil || !ok {
		return SeasonsLennaResponseDTO{Items: []SeasonLennaItemDTO{}, TotalRecordCount: 0}, ok, err
	}
	return SeasonsLennaResponseDTO{Items: renderSeasonLennaItems(items), TotalRecordCount: len(items)}, true, nil
}

func BuildShowSeasonsFamilyPayload(database *db.DB, userID int64, serverID string, seriesID string) (SeasonsFamilyResponseDTO, bool, error) {
	items, ok, err := buildShowSeasonSources(database, userID, serverID, seriesID)
	if err != nil || !ok {
		return SeasonsFamilyResponseDTO{Items: []SeasonFamilyItemDTO{}, TotalRecordCount: 0}, ok, err
	}
	return SeasonsFamilyResponseDTO{Items: renderSeasonFamilyItems(items), TotalRecordCount: len(items)}, true, nil
}

func BuildShowSeasonsRichPayload(database *db.DB, userID int64, serverID string, seriesID string) (SeasonsLennaResponseDTO, bool, error) {
	return BuildShowSeasonsLennaPayload(database, userID, serverID, seriesID)
}

func buildShowSeasonSources(database *db.DB, userID int64, serverID string, seriesID string) ([]seasonListSource, bool, error) {
	ref := parseItemRefAny(seriesID)
	if ref != nil && ref.Source == "site" && ref.SubKind == "series" {
		return buildSiteShowSeasonSources(database, userID, serverID, ref)
	}
	if ref == nil || ref.Source != "tmdb" || ref.MediaType != "tv" || ref.SubKind != "series" {
		return []seasonListSource{}, false, nil
	}
	view, err := loadTVSeasonListView(database, ref.NumericID, false)
	if err != nil || view == nil || view.Series == nil {
		return []seasonListSource{}, false, err
	}
	seriesName := strings.TrimSpace(view.Series.Title)
	parentLogoItemID := buildSeriesID(ref.NumericID)
	parentBackdropTags := backdropTagsFromAsset(view.Series.Backdrop)
	genres := resumeGenres(view.Series, "tv")
	genreItems := NamedGenreItems(genres)
	state := SeasonItemState()
	items := make([]seasonListSource, 0, len(view.Series.Seasons))
	for _, season := range view.Series.Seasons {
		if season.SeasonNumber <= 0 {
			continue
		}
		seasonID := buildSeasonID(ref.NumericID, season.SeasonNumber)
		seasonDetail := view.SeasonDetails[season.SeasonNumber]
		seasonName := ""
		if seasonDetail != nil {
			seasonName = strings.TrimSpace(seasonDetail.Name)
		}
		if seasonName == "" {
			seasonName = strings.TrimSpace(season.Name)
		}
		userData := EmptyTVLatestUserData()
		if seasonDetail != nil {
			userData.UnplayedItemCount = len(seasonDetailEpisodes(seasonDetail))
		} else if season.EpisodeCount > 0 {
			userData.UnplayedItemCount = season.EpisodeCount
		}
		items = append(items, seasonListSource{
			Name:                    seasonName,
			ServerID:                strings.TrimSpace(serverID),
			ID:                      seasonID,
			SupportsSync:            true,
			PremiereDate:            seasonPremiereDate(season, seasonDetail),
			Overview:                seasonOverview(season, seasonDetail),
			CommunityRating:         0,
			ProductionYear:          seasonProductionYear(season, view.Series, seasonDetail),
			EndDate:                 "",
			Container:               "",
			Genres:                  genres,
			IndexNumber:             season.SeasonNumber,
			IsFolder:                state.IsFolder,
			ParentID:                parentLogoItemID,
			Type:                    state.Type,
			GenreItems:              genreItems,
			People:                  EmptyPeople(),
			ParentLogoItemID:        parentLogoItemID,
			ParentBackdropItemID:    parentLogoItemID,
			ParentBackdropImageTags: BackdropTagsOrEmpty(parentBackdropTags),
			UserData:                userData,
			ChildCount:              maxInt(userData.UnplayedItemCount, season.EpisodeCount),
			SeriesName:              seriesName,
			SeriesID:                parentLogoItemID,
			SeriesPrimaryImageTag:   SeriesPrimaryImageTag(parentLogoItemID),
			ImageTags:               ImageTagsForItem(seasonID, false),
			BackdropImageTags:       BackdropTagsOrEmpty(parentBackdropTags),
			ParentLogoImageTag:      ParentLogoImageTag(parentLogoItemID),
		})
	}
	items = append(items, buildTMDBSettingsSeasonSource(serverID, ref.NumericID, seriesName, parentLogoItemID, parentBackdropTags, len(items)+1)...)
	return items, true, nil
}

func BuildShowEpisodesPayload(database *db.DB, userID int64, serverID string, seriesID string, seasonID string) (EpisodesResponseDTO, bool, error) {
	items, ok, err := buildShowEpisodeSources(database, userID, serverID, seriesID, seasonID)
	if err != nil || !ok {
		return EpisodesResponseDTO{Items: []EpisodeListItemDTO{}, TotalRecordCount: 0}, ok, err
	}
	return EpisodesResponseDTO{Items: renderEpisodeRichAsLegacyItems(items), TotalRecordCount: len(items)}, true, nil
}

func BuildShowEpisodesBasicPayload(database *db.DB, userID int64, serverID string, seriesID string, seasonID string) (EpisodesBasicResponseDTO, bool, error) {
	items, ok, err := buildShowEpisodeSources(database, userID, serverID, seriesID, seasonID)
	if err != nil || !ok {
		return EpisodesBasicResponseDTO{Items: []EpisodeBasicItemDTO{}, TotalRecordCount: 0}, ok, err
	}
	return EpisodesBasicResponseDTO{Items: renderEpisodeBasicItems(items), TotalRecordCount: len(items)}, true, nil
}

func BuildShowEpisodesInfusePayload(database *db.DB, userID int64, serverID string, seriesID string, seasonID string) (EpisodesInfuseResponseDTO, bool, error) {
	items, ok, err := buildShowEpisodeSources(database, userID, serverID, seriesID, seasonID)
	if err != nil || !ok {
		return EpisodesInfuseResponseDTO{Items: []EpisodeInfuseItemDTO{}, TotalRecordCount: 0}, ok, err
	}
	return EpisodesInfuseResponseDTO{Items: renderEpisodeInfuseItems(items), TotalRecordCount: len(items)}, true, nil
}

func BuildShowEpisodesLennaPayload(database *db.DB, userID int64, serverID string, seriesID string, seasonID string) (EpisodesLennaResponseDTO, bool, error) {
	items, ok, err := buildShowEpisodeSources(database, userID, serverID, seriesID, seasonID)
	if err != nil || !ok {
		return EpisodesLennaResponseDTO{Items: []EpisodeLennaItemDTO{}, TotalRecordCount: 0}, ok, err
	}
	return EpisodesLennaResponseDTO{Items: renderEpisodeLennaItems(items), TotalRecordCount: len(items)}, true, nil
}

func BuildShowEpisodesRichPayload(database *db.DB, userID int64, serverID string, seriesID string, seasonID string) (EpisodesLennaResponseDTO, bool, error) {
	return BuildShowEpisodesLennaPayload(database, userID, serverID, seriesID, seasonID)
}

func BuildShowEpisodesFamilyPayload(database *db.DB, userID int64, serverID string, seriesID string, seasonID string) (EpisodesResponseDTO, bool, error) {
	return BuildShowEpisodesPayload(database, userID, serverID, seriesID, seasonID)
}

func buildShowEpisodeSources(database *db.DB, userID int64, serverID string, seriesID string, seasonID string) ([]episodeListSource, bool, error) {
	seriesRef := parseItemRefAny(seriesID)
	seasonRef := parseItemRefAny(seasonID)
	if seriesRef != nil && seasonRef != nil && seriesRef.Source == "site" && seasonRef.Source == "site" && seriesRef.SubKind == "series" && seasonRef.SubKind == "season" {
		return buildSiteShowEpisodeSources(database, userID, serverID, seriesRef, seasonRef)
	}
	if seriesRef == nil || seriesRef.Source != "tmdb" || seriesRef.MediaType != "tv" || seriesRef.SubKind != "series" {
		return []episodeListSource{}, false, nil
	}
	hist, _ := database.GetPlayHistoryLatestByTMDB(userID, "tv", seriesRef.NumericID)
	includeUnaired := hist != nil && hist.PreOrder
	if seasonRef == nil {
		return buildTMDBAllEpisodeSources(database, userID, serverID, seriesRef, includeUnaired)
	}
	if seasonRef.Source == "tmdb" && seasonRef.MediaType == "tv" && seasonRef.SubKind == "season" && seasonRef.NumericID == seriesRef.NumericID && seasonRef.Variant == "settings" {
		return buildTMDBSettingsEpisodeSources(database, userID, serverID, seriesRef)
	}
	if seasonRef.Source != "tmdb" || seasonRef.MediaType != "tv" || seasonRef.SubKind != "season" || seasonRef.NumericID != seriesRef.NumericID {
		return []episodeListSource{}, false, nil
	}
	return buildTMDBSeasonEpisodeSources(database, userID, serverID, seriesRef, seasonRef.Pan, includeUnaired)
}

func buildTMDBAllEpisodeSources(database *db.DB, userID int64, serverID string, seriesRef *itemRef, includeUnaired bool) ([]episodeListSource, bool, error) {
	if seriesRef == nil || seriesRef.NumericID <= 0 {
		return []episodeListSource{}, false, nil
	}
	seriesView, err := loadTVSeasonListView(database, seriesRef.NumericID, includeUnaired)
	if err != nil || seriesView == nil || seriesView.Series == nil {
		return []episodeListSource{}, false, err
	}
	items := make([]episodeListSource, 0)
	for _, season := range seriesView.Series.Seasons {
		if season.SeasonNumber <= 0 {
			continue
		}
		seasonItems, ok, err := buildTMDBSeasonEpisodeSources(database, userID, serverID, seriesRef, season.SeasonNumber, includeUnaired)
		if err != nil {
			return []episodeListSource{}, false, err
		}
		if !ok {
			continue
		}
		items = append(items, seasonItems...)
	}
	return items, true, nil
}

func buildTMDBSeasonEpisodeSources(database *db.DB, userID int64, serverID string, seriesRef *itemRef, seasonNo int, includeUnaired bool) ([]episodeListSource, bool, error) {
	if seriesRef == nil || seriesRef.NumericID <= 0 || seasonNo <= 0 {
		return []episodeListSource{}, false, nil
	}
	view, err := loadTVSeasonEpisodesView(database, seriesRef.NumericID, seasonNo, includeUnaired)
	if err != nil || view == nil || view.Series == nil || view.Season == nil {
		return []episodeListSource{}, false, err
	}
	seriesName := strings.TrimSpace(view.Series.Title)
	seriesItemID := buildSeriesID(seriesRef.NumericID)
	seasonItemID := buildSeasonID(seriesRef.NumericID, seasonNo)
	seasonName := strings.TrimSpace(view.Season.Name)
	parentBackdropTags := backdropTagsFromAsset(view.Series.Backdrop)
	seriesGenres, seriesGenreItems := GenresAndItemsFromDetail(view.Series, "tv")
	seriesProviderIDs := ProviderIDsFromTMDBAny(seriesRef.NumericID)
	seriesCommunityRating := 0.0
	seriesYear := 0
	if view.Series != nil {
		seriesCommunityRating = view.Series.VoteAverage
		seriesYear = parseTMDBCachedYear(view.Series)
	}
	items := make([]episodeListSource, 0, len(view.Season.Episodes))
	state := EpisodeItemState(false, true)
	for _, ep := range view.Season.Episodes {
		if ep.EpisodeNumber <= 0 {
			continue
		}
		itemID := buildEpisodeID(seriesRef.NumericID, seasonNo, ep.EpisodeNumber)
		runtime := RuntimeTicksFromEpisode(ep)
		chapters := BuildSyntheticChapters(runtime)
		hist, _ := database.GetPlayHistoryLatestByTMDB(userID, "tv", seriesRef.NumericID)
		if !playHistoryMatchesTMDBEpisode(hist, seriesRef.NumericID, seasonNo, ep.EpisodeNumber) {
			hist = nil
		}
		dateCreated := ProtocolCreatedDate(0)
		if hist != nil && hist.UpdatedAt > 0 {
			dateCreated = ProtocolCreatedDate(hist.UpdatedAt)
		} else if view.Series.LastRefreshAt > 0 {
			dateCreated = ProtocolCreatedDate(view.Series.LastRefreshAt)
		}
		path := ""
		container := "mp4"
		fileName := episodeFileName(ep, seasonNo)
		if hist != nil {
			base := strings.TrimSpace(hist.SiteEpisodeFile)
			if base != "" {
				fileName = base
			}
		}
		if path == "" {
			if filepath.Ext(strings.TrimSpace(fileName)) == "" {
				fileName = fileName + "." + container
			}
			path = VirtualEpisodePath(seriesName, seriesYear, seasonNo, fileName)
		}
		mediaSources := detailMediaSources(itemID, path, fileName, runtime, container, chapters)
		mediaStreams := EmptyAnySlice()
		if len(mediaSources) > 0 && mediaSources[0].MediaStreams != nil {
			mediaStreams = mediaSources[0].MediaStreams
		}
		episodeBackdropTags := backdropTagsFromAsset(strings.TrimSpace(ep.StillPath))
		if len(episodeBackdropTags) == 0 {
			episodeBackdropTags = BackdropTagsOrEmpty(parentBackdropTags)
		}
		items = append(items, episodeListSource{
			Name:                    episodeName(ep),
			ServerID:                strings.TrimSpace(serverID),
			ID:                      itemID,
			Etag:                    StableItemEtag(itemID),
			DateCreated:             dateCreated,
			CanDownload:             state.CanDownload,
			SupportsSync:            state.SupportsSync,
			Container:               container,
			SortName:                SortNameOrName(episodeName(ep)),
			PremiereDate:            preciseDateString(strings.TrimSpace(ep.AirDate)),
			MediaSources:            mediaSources,
			AlternateMediaSources:   EmptyAnySlice(),
			Path:                    path,
			Overview:                strings.TrimSpace(ep.Overview),
			Genres:                  seriesGenres,
			CommunityRating:         seriesCommunityRating,
			OfficialRating:          "",
			RunTimeTicks:            runtime,
			Size:                    0,
			Bitrate:                 0,
			ProductionYear:          YearFromDate(strings.TrimSpace(ep.AirDate)),
			IndexNumber:             ep.EpisodeNumber,
			ParentIndexNumber:       seasonNo,
			ProviderIDs:             seriesProviderIDs,
			IsFolder:                state.IsFolder,
			ParentID:                seasonItemID,
			Type:                    state.Type,
			People:                  EmptyPeople(),
			Studios:                 EmptyNamedIDs(),
			GenreItems:              seriesGenreItems,
			ParentLogoItemID:        seriesItemID,
			ParentBackdropItemID:    seriesItemID,
			ParentBackdropImageTags: BackdropTagsOrEmpty(parentBackdropTags),
			UserData:                BuildEpisodeSimpleUserData(hist),
			SeriesName:              seriesName,
			SeriesID:                seriesItemID,
			SeasonID:                seasonItemID,
			SeriesPrimaryImageTag:   SeriesPrimaryImageTag(seriesItemID),
			SeasonName:              seasonName,
			MediaStreams:            mediaStreams,
			ImageTags:               ImageTagsForItem(itemID, false),
			BackdropImageTags:       episodeBackdropTags,
			ParentLogoImageTag:      ParentLogoImageTag(seriesItemID),
			Chapters:                chapters,
			MediaType:               MediaTypeVideo,
		})
	}
	return items, true, nil
}

func buildTMDBSettingsSeasonSource(serverID string, tmdbID int, seriesName string, parentLogoItemID string, parentBackdropTags []string, indexNumber int) []seasonListSource {
	if tmdbID <= 0 {
		return []seasonListSource{}
	}
	seasonID := buildTMDBSettingsSeasonID(tmdbID)
	state := SeasonItemState()
	return []seasonListSource{{
		Name:                    tmdbSettingsSeasonName,
		ServerID:                strings.TrimSpace(serverID),
		ID:                      seasonID,
		SupportsSync:            true,
		PremiereDate:            "",
		Overview:                "片源设置与反馈。",
		CommunityRating:         0,
		ProductionYear:          0,
		EndDate:                 "",
		Container:               "",
		Genres:                  EmptyStrings(),
		IndexNumber:             maxInt(1, indexNumber),
		IsFolder:                state.IsFolder,
		ParentID:                parentLogoItemID,
		Type:                    state.Type,
		GenreItems:              EmptyNamedIDs(),
		People:                  EmptyPeople(),
		ParentLogoItemID:        parentLogoItemID,
		ParentBackdropItemID:    parentLogoItemID,
		ParentBackdropImageTags: BackdropTagsOrEmpty(parentBackdropTags),
		UserData:                TVLatestUserDataDTO{UnplayedItemCount: len(tmdbSettingsEpisodes())},
		ChildCount:              len(tmdbSettingsEpisodes()),
		SeriesName:              strings.TrimSpace(seriesName),
		SeriesID:                parentLogoItemID,
		SeriesPrimaryImageTag:   SeriesPrimaryImageTag(parentLogoItemID),
		ImageTags:               ImageTagsForItem(seasonID, false),
		BackdropImageTags:       BackdropTagsOrEmpty(parentBackdropTags),
		ParentLogoImageTag:      ParentLogoImageTag(parentLogoItemID),
	}}
}

func buildTMDBSettingsEpisodeSources(database *db.DB, userID int64, serverID string, seriesRef *itemRef) ([]episodeListSource, bool, error) {
	if seriesRef == nil || seriesRef.NumericID <= 0 {
		return []episodeListSource{}, false, nil
	}
	detail, err := metadata_tmdb.GetDetailForBackend(database, "tv", seriesRef.NumericID)
	if err != nil || detail == nil {
		return []episodeListSource{}, false, err
	}
	seriesName := strings.TrimSpace(detail.Title)
	seriesYear := parseTMDBCachedYear(detail)
	settingsSeasonIndex := len(detail.Seasons) + 1
	seriesItemID := buildSeriesID(seriesRef.NumericID)
	seasonItemID := buildTMDBSettingsSeasonID(seriesRef.NumericID)
	parentBackdropTags := backdropTagsFromAsset(detail.Backdrop)
	genres, genreItems := GenresAndItemsFromDetail(detail, "tv")
	state := EpisodeItemState(false, true)
	episodes := tmdbSettingsEpisodes()
	if hist, _ := database.GetPlayHistoryLatestByTMDB(userID, "tv", seriesRef.NumericID); hist != nil && hist.PreOrder {
		for i := range episodes {
			if episodes[i].EpisodeNo != 4 {
				continue
			}
			episodes[i].Name = "关闭点映"
			episodes[i].Overview = "关闭当前剧集的未播集显示。"
			break
		}
	}
	items := make([]episodeListSource, 0, len(episodes))
	for _, ep := range episodes {
		itemID := buildTMDBSettingsEpisodeID(seriesRef.NumericID, ep.EpisodeNo)
		fileName := strings.TrimSpace(ep.Name) + ".mp4"
		path := VirtualSettingsEpisodePath(seriesName, seriesYear, fileName)
		chapters := EmptyDetailChapters()
		mediaSources := detailMediaSources(itemID, path, fileName, 0, "mp4", chapters)
		mediaStreams := EmptyAnySlice()
		if len(mediaSources) > 0 && mediaSources[0].MediaStreams != nil {
			mediaStreams = mediaSources[0].MediaStreams
		}
		items = append(items, episodeListSource{
			Name:                    strings.TrimSpace(ep.Name),
			ServerID:                strings.TrimSpace(serverID),
			ID:                      itemID,
			Etag:                    StableItemEtag(itemID),
			DateCreated:             ProtocolCreatedDate(0),
			CanDownload:             state.CanDownload,
			SupportsSync:            state.SupportsSync,
			Container:               "mp4",
			SortName:                SortNameOrName(strings.TrimSpace(ep.Name)),
			PremiereDate:            "",
			MediaSources:            mediaSources,
			AlternateMediaSources:   EmptyAnySlice(),
			Path:                    path,
			Overview:                strings.TrimSpace(ep.Overview),
			Genres:                  genres,
			CommunityRating:         0,
			OfficialRating:          "",
			RunTimeTicks:            0,
			Size:                    0,
			Bitrate:                 0,
			ProductionYear:          seriesYear,
			IndexNumber:             ep.EpisodeNo,
			ParentIndexNumber:       maxInt(1, settingsSeasonIndex),
			ProviderIDs:             ProviderIDsFromTMDBAny(seriesRef.NumericID),
			IsFolder:                state.IsFolder,
			ParentID:                seasonItemID,
			Type:                    state.Type,
			People:                  EmptyPeople(),
			Studios:                 EmptyNamedIDs(),
			GenreItems:              genreItems,
			ParentLogoItemID:        seriesItemID,
			ParentBackdropItemID:    seriesItemID,
			ParentBackdropImageTags: BackdropTagsOrEmpty(parentBackdropTags),
			UserData:                EmptySimpleUserData(),
			SeriesName:              seriesName,
			SeriesID:                seriesItemID,
			SeasonID:                seasonItemID,
			SeriesPrimaryImageTag:   SeriesPrimaryImageTag(seriesItemID),
			SeasonName:              tmdbSettingsSeasonName,
			MediaStreams:            mediaStreams,
			ImageTags:               ImageTagsForItem(itemID, false),
			BackdropImageTags:       BackdropTagsOrEmpty(parentBackdropTags),
			ParentLogoImageTag:      ParentLogoImageTag(seriesItemID),
			Chapters:                chapters,
			MediaType:               MediaTypeVideo,
		})
	}
	return items, true, nil
}

func buildSiteShowSeasonSources(database *db.DB, userID int64, serverID string, ref *itemRef) ([]seasonListSource, bool, error) {
	if database == nil || ref == nil || strings.TrimSpace(ref.SiteKey) == "" || strings.TrimSpace(ref.SiteDetail) == "" {
		return []seasonListSource{}, false, nil
	}
	pans, err := fetchResolvedSiteDetailPans(database, userID, ref.SiteKey, ref.SiteDetail)
	if err != nil {
		return []seasonListSource{}, false, err
	}
	seriesID := buildSiteSeriesID(ref.SiteKey, ref.SiteDetail)
	items := make([]seasonListSource, 0, len(pans))
	for i, pan := range pans {
		seasonNo := i + 1
		seasonID := buildSiteSeasonID(ref.SiteKey, ref.SiteDetail, seasonNo)
		seasonName := strings.TrimSpace(pan.DisplayLabel)
		genres, genreItems := EmptyGenresAndItems()
		state := SeasonItemState()
		items = append(items, seasonListSource{
			Name:                    seasonName,
			ServerID:                strings.TrimSpace(serverID),
			ID:                      seasonID,
			SupportsSync:            true,
			PremiereDate:            "",
			Overview:                "",
			CommunityRating:         0,
			ProductionYear:          0,
			EndDate:                 "",
			Container:               "",
			Genres:                  genres,
			IndexNumber:             seasonNo,
			IsFolder:                state.IsFolder,
			ParentID:                seriesID,
			Type:                    state.Type,
			GenreItems:              genreItems,
			People:                  EmptyPeople(),
			ParentLogoItemID:        seriesID,
			ParentBackdropItemID:    seriesID,
			ParentBackdropImageTags: EmptyStrings(),
			UserData:                EmptyTVLatestUserData(),
			SeriesName:              "",
			SeriesID:                seriesID,
			SeriesPrimaryImageTag:   SeriesPrimaryImageTag(seriesID),
			ImageTags:               ImageTagsForItem(seasonID, false),
			BackdropImageTags:       EmptyStrings(),
			ParentLogoImageTag:      ParentLogoImageTag(seriesID),
		})
	}
	return items, true, nil
}

func buildSiteShowEpisodeSources(database *db.DB, userID int64, serverID string, seriesRef *itemRef, seasonRef *itemRef) ([]episodeListSource, bool, error) {
	if database == nil || seriesRef == nil || seasonRef == nil || seriesRef.SiteKey != seasonRef.SiteKey || seriesRef.SiteDetail != seasonRef.SiteDetail {
		return []episodeListSource{}, false, nil
	}
	meta, err := fetchSiteDetailMeta(database, userID, seriesRef.SiteKey, seriesRef.SiteDetail)
	if err != nil {
		return []episodeListSource{}, false, err
	}
	pans, err := fetchResolvedSiteDetailPans(database, userID, seriesRef.SiteKey, seriesRef.SiteDetail)
	if err != nil {
		return []episodeListSource{}, false, err
	}
	if seasonRef.Pan <= 0 || seasonRef.Pan > len(pans) {
		return []episodeListSource{}, true, nil
	}
	pan := pans[seasonRef.Pan-1]
	seriesID := buildSiteSeriesID(seriesRef.SiteKey, seriesRef.SiteDetail)
	seasonID := buildSiteSeasonID(seriesRef.SiteKey, seriesRef.SiteDetail, seasonRef.Pan)
	seriesName := resolveSiteSeriesName(database, seriesRef.SiteKey, seriesRef.SiteDetail, meta)
	seasonName := strings.TrimSpace(pan.DisplayLabel)
	items := make([]episodeListSource, 0, len(pan.Episodes))
	state := EpisodeItemState(false, true)
	for idx, ep := range pan.Episodes {
		epNo := idx + 1
		title := strings.TrimSpace(seriesName)
		if title == "" {
			title = strings.TrimSpace(meta.Name)
		}
		itemID := buildSiteEpisodeID(seriesRef.SiteKey, seriesRef.SiteDetail, seasonRef.Pan, epNo, title, firstNonEmptyString(strings.TrimSpace(ep.Flag), strings.TrimSpace(pan.RawLabel)), strings.TrimSpace(ep.URL))
		if itemID == "" {
			continue
		}
		name := siteEpisodeDisplayName(ep, pan.RawLabel, pan.PanMock, seriesName, epNo)
		rawFileName := siteEpisodeFileName(ep, name, epNo)
		container := siteEpisodeContainerFromName(rawFileName)
		fileName := siteEpisodePlayableFileName(rawFileName, container)
		path := VirtualEpisodePath(seriesName, meta.Year, seasonRef.Pan, fileName)
		chapters := EmptyDetailChapters()
		mediaSources := detailMediaSources(itemID, path, fileName, 0, container, chapters)
		mediaStreams := EmptyAnySlice()
		if len(mediaSources) > 0 && mediaSources[0].MediaStreams != nil {
			mediaStreams = mediaSources[0].MediaStreams
		}
		items = append(items, episodeListSource{
			Name:                    name,
			ServerID:                strings.TrimSpace(serverID),
			ID:                      itemID,
			Etag:                    StableItemEtag(itemID),
			DateCreated:             ProtocolCreatedDate(0),
			CanDownload:             state.CanDownload,
			SupportsSync:            state.SupportsSync,
			Container:               container,
			SortName:                SortNameOrName(name),
			PremiereDate:            "",
			MediaSources:            mediaSources,
			AlternateMediaSources:   EmptyAnySlice(),
			Path:                    path,
			Overview:                "",
			Genres:                  EmptyStrings(),
			CommunityRating:         0,
			OfficialRating:          "",
			RunTimeTicks:            0,
			Size:                    0,
			Bitrate:                 0,
			ProductionYear:          0,
			IndexNumber:             epNo,
			ParentIndexNumber:       seasonRef.Pan,
			ProviderIDs:             EmptyAnyMap(),
			IsFolder:                state.IsFolder,
			ParentID:                seasonID,
			Type:                    state.Type,
			People:                  EmptyPeople(),
			Studios:                 EmptyNamedIDs(),
			GenreItems:              EmptyNamedIDs(),
			ParentLogoItemID:        seriesID,
			ParentBackdropItemID:    seriesID,
			ParentBackdropImageTags: EmptyStrings(),
			UserData:                EmptySimpleUserData(),
			SeriesName:              seriesName,
			SeriesID:                seriesID,
			SeasonID:                seasonID,
			SeriesPrimaryImageTag:   SeriesPrimaryImageTag(seriesID),
			SeasonName:              seasonName,
			MediaStreams:            mediaStreams,
			ImageTags:               ImageTagsForItem(itemID, false),
			BackdropImageTags:       EmptyStrings(),
			ParentLogoImageTag:      ParentLogoImageTag(seriesID),
			Chapters:                chapters,
			MediaType:               MediaTypeVideo,
		})
	}
	return items, true, nil
}

func siteEpisodeContainerFromName(name string) string {
	ext := strings.TrimPrefix(strings.ToLower(strings.TrimSpace(filepath.Ext(strings.TrimSpace(name)))), ".")
	if ext == "" {
		return "mp4"
	}
	return ext
}

func siteEpisodePlayableFileName(name string, container string) string {
	base := strings.TrimSpace(filepath.Base(strings.TrimSpace(name)))
	if base == "." || base == "/" || base == "" {
		base = "video"
	}
	if filepath.Ext(base) != "" {
		return base
	}
	ct := strings.TrimSpace(container)
	if ct == "" {
		ct = "mp4"
	}
	return base + "." + ct
}

func renderSeasonGenresItems(items []seasonListSource) []SeasonListItemDTO {
	out := make([]SeasonListItemDTO, 0, len(items))
	for _, item := range items {
		out = append(out, SeasonListItemDTO{
			Name:                    item.Name,
			ServerID:                item.ServerID,
			ID:                      item.ID,
			Genres:                  item.Genres,
			IndexNumber:             item.IndexNumber,
			IsFolder:                item.IsFolder,
			ParentID:                item.ParentID,
			Type:                    item.Type,
			GenreItems:              item.GenreItems,
			ParentLogoItemID:        item.ParentLogoItemID,
			ParentBackdropItemID:    item.ParentBackdropItemID,
			ParentBackdropImageTags: item.ParentBackdropImageTags,
			UserData:                item.UserData,
			SeriesName:              item.SeriesName,
			SeriesID:                item.SeriesID,
			SeriesPrimaryImageTag:   item.SeriesPrimaryImageTag,
			ImageTags:               item.ImageTags,
			BackdropImageTags:       item.BackdropImageTags,
			ParentLogoImageTag:      item.ParentLogoImageTag,
		})
	}
	return out
}

func renderSeasonBasicItems(items []seasonListSource) []SeasonBasicItemDTO {
	out := make([]SeasonBasicItemDTO, 0, len(items))
	for _, item := range items {
		out = append(out, SeasonBasicItemDTO{
			Name:                    item.Name,
			ServerID:                item.ServerID,
			ID:                      item.ID,
			IndexNumber:             item.IndexNumber,
			IsFolder:                item.IsFolder,
			Type:                    item.Type,
			ParentLogoItemID:        item.ParentLogoItemID,
			ParentBackdropItemID:    item.ParentBackdropItemID,
			ParentBackdropImageTags: item.ParentBackdropImageTags,
			UserData:                item.UserData,
			SeriesName:              item.SeriesName,
			SeriesID:                item.SeriesID,
			SeriesPrimaryImageTag:   item.SeriesPrimaryImageTag,
			ImageTags:               item.ImageTags,
			BackdropImageTags:       item.BackdropImageTags,
			ParentLogoImageTag:      item.ParentLogoImageTag,
		})
	}
	return out
}

func renderSeasonLennaItems(items []seasonListSource) []SeasonLennaItemDTO {
	out := make([]SeasonLennaItemDTO, 0, len(items))
	for _, item := range items {
		out = append(out, SeasonLennaItemDTO{
			Name:                    item.Name,
			ServerID:                item.ServerID,
			ID:                      item.ID,
			SupportsSync:            item.SupportsSync,
			Overview:                item.Overview,
			ProductionYear:          item.ProductionYear,
			IndexNumber:             item.IndexNumber,
			IsFolder:                item.IsFolder,
			Type:                    item.Type,
			ParentLogoItemID:        item.ParentLogoItemID,
			ParentBackdropItemID:    item.ParentBackdropItemID,
			ParentBackdropImageTags: item.ParentBackdropImageTags,
			UserData:                item.UserData,
			SeriesName:              item.SeriesName,
			SeriesID:                item.SeriesID,
			SeriesPrimaryImageTag:   item.SeriesPrimaryImageTag,
			ImageTags:               item.ImageTags,
			BackdropImageTags:       item.BackdropImageTags,
			ParentLogoImageTag:      item.ParentLogoImageTag,
		})
	}
	return out
}

func renderSeasonFamilyItems(items []seasonListSource) []SeasonFamilyItemDTO {
	out := make([]SeasonFamilyItemDTO, 0, len(items))
	for _, item := range items {
		out = append(out, SeasonFamilyItemDTO{
			Name:                    item.Name,
			ServerID:                item.ServerID,
			ID:                      item.ID,
			SupportsSync:            item.SupportsSync,
			PremiereDate:            item.PremiereDate,
			Overview:                item.Overview,
			IndexNumber:             item.IndexNumber,
			IsFolder:                item.IsFolder,
			Type:                    item.Type,
			People:                  item.People,
			ParentLogoItemID:        item.ParentLogoItemID,
			ParentBackdropItemID:    item.ParentBackdropItemID,
			ParentBackdropImageTags: item.ParentBackdropImageTags,
			UserData:                item.UserData,
			ChildCount:              item.ChildCount,
			SeriesName:              item.SeriesName,
			SeriesID:                item.SeriesID,
			SeriesPrimaryImageTag:   item.SeriesPrimaryImageTag,
			ImageTags:               item.ImageTags,
			BackdropImageTags:       item.BackdropImageTags,
			ParentLogoImageTag:      item.ParentLogoImageTag,
		})
	}
	return out
}

func renderEpisodeBasicItems(items []episodeListSource) []EpisodeBasicItemDTO {
	out := make([]EpisodeBasicItemDTO, 0, len(items))
	for _, item := range items {
		out = append(out, EpisodeBasicItemDTO{
			Name:                    item.Name,
			ServerID:                item.ServerID,
			ID:                      item.ID,
			CanDownload:             item.CanDownload,
			SupportsSync:            item.SupportsSync,
			PremiereDate:            item.PremiereDate,
			RunTimeTicks:            item.RunTimeTicks,
			IndexNumber:             item.IndexNumber,
			ParentIndexNumber:       item.ParentIndexNumber,
			IsFolder:                item.IsFolder,
			Type:                    item.Type,
			ParentLogoItemID:        item.ParentLogoItemID,
			ParentBackdropItemID:    item.ParentBackdropItemID,
			ParentBackdropImageTags: item.ParentBackdropImageTags,
			UserData:                item.UserData,
			SeriesName:              item.SeriesName,
			SeriesID:                item.SeriesID,
			SeasonID:                item.SeasonID,
			SeriesPrimaryImageTag:   item.SeriesPrimaryImageTag,
			SeasonName:              item.SeasonName,
			ImageTags:               item.ImageTags,
			BackdropImageTags:       item.BackdropImageTags,
			ParentLogoImageTag:      item.ParentLogoImageTag,
			MediaType:               item.MediaType,
		})
	}
	return out
}

func renderEpisodeInfuseItems(items []episodeListSource) []EpisodeInfuseItemDTO {
	out := make([]EpisodeInfuseItemDTO, 0, len(items))
	for _, item := range items {
		out = append(out, EpisodeInfuseItemDTO{
			Name:                    item.Name,
			ServerID:                item.ServerID,
			ID:                      item.ID,
			Etag:                    item.Etag,
			Container:               item.Container,
			PremiereDate:            item.PremiereDate,
			MediaSources:            item.MediaSources,
			AlternateMediaSources:   item.AlternateMediaSources,
			Overview:                item.Overview,
			Genres:                  item.Genres,
			RunTimeTicks:            item.RunTimeTicks,
			Size:                    item.Size,
			Bitrate:                 item.Bitrate,
			IndexNumber:             item.IndexNumber,
			ParentIndexNumber:       item.ParentIndexNumber,
			ProviderIDs:             item.ProviderIDs,
			IsFolder:                item.IsFolder,
			ParentID:                item.ParentID,
			Type:                    item.Type,
			GenreItems:              item.GenreItems,
			ParentLogoItemID:        item.ParentLogoItemID,
			ParentBackdropItemID:    item.ParentBackdropItemID,
			ParentBackdropImageTags: item.ParentBackdropImageTags,
			UserData:                item.UserData,
			SeriesName:              item.SeriesName,
			SeriesID:                item.SeriesID,
			SeasonID:                item.SeasonID,
			SeriesPrimaryImageTag:   item.SeriesPrimaryImageTag,
			SeasonName:              item.SeasonName,
			ImageTags:               item.ImageTags,
			BackdropImageTags:       item.BackdropImageTags,
			ParentLogoImageTag:      item.ParentLogoImageTag,
			MediaType:               item.MediaType,
		})
	}
	return out
}

func renderEpisodeLennaItems(items []episodeListSource) []EpisodeLennaItemDTO {
	out := make([]EpisodeLennaItemDTO, 0, len(items))
	for _, item := range items {
		out = append(out, EpisodeLennaItemDTO{
			Name:                    item.Name,
			ServerID:                item.ServerID,
			ID:                      item.ID,
			DateCreated:             item.DateCreated,
			SupportsSync:            item.SupportsSync,
			Container:               item.Container,
			SortName:                item.SortName,
			PremiereDate:            item.PremiereDate,
			MediaSources:            item.MediaSources,
			Path:                    item.Path,
			Overview:                item.Overview,
			Genres:                  item.Genres,
			CommunityRating:         item.CommunityRating,
			RunTimeTicks:            item.RunTimeTicks,
			Size:                    item.Size,
			Bitrate:                 item.Bitrate,
			ProductionYear:          item.ProductionYear,
			IndexNumber:             item.IndexNumber,
			ParentIndexNumber:       item.ParentIndexNumber,
			ProviderIDs:             item.ProviderIDs,
			IsFolder:                item.IsFolder,
			ParentID:                item.ParentID,
			Type:                    item.Type,
			People:                  item.People,
			Studios:                 item.Studios,
			GenreItems:              item.GenreItems,
			ParentBackdropItemID:    item.ParentBackdropItemID,
			ParentBackdropImageTags: item.ParentBackdropImageTags,
			UserData:                item.UserData,
			SeriesName:              item.SeriesName,
			SeriesID:                item.SeriesID,
			SeasonID:                item.SeasonID,
			SeriesPrimaryImageTag:   item.SeriesPrimaryImageTag,
			SeasonName:              item.SeasonName,
			MediaStreams:            item.MediaStreams,
			ImageTags:               item.ImageTags,
			BackdropImageTags:       item.BackdropImageTags,
			MediaType:               item.MediaType,
		})
	}
	return out
}

func renderEpisodeRichAsLegacyItems(items []episodeListSource) []EpisodeListItemDTO {
	out := make([]EpisodeListItemDTO, 0, len(items))
	for _, item := range items {
		out = append(out, EpisodeListItemDTO{
			Name:                    item.Name,
			ServerID:                item.ServerID,
			ID:                      item.ID,
			CanDownload:             item.CanDownload,
			SupportsSync:            item.SupportsSync,
			Container:               item.Container,
			PremiereDate:            item.PremiereDate,
			MediaSources:            item.MediaSources,
			Path:                    item.Path,
			Overview:                item.Overview,
			RunTimeTicks:            item.RunTimeTicks,
			Size:                    item.Size,
			Bitrate:                 item.Bitrate,
			IndexNumber:             item.IndexNumber,
			ParentIndexNumber:       item.ParentIndexNumber,
			IsFolder:                item.IsFolder,
			Type:                    item.Type,
			People:                  item.People,
			ParentLogoItemID:        item.ParentLogoItemID,
			ParentBackdropItemID:    item.ParentBackdropItemID,
			ParentBackdropImageTags: item.ParentBackdropImageTags,
			UserData:                item.UserData,
			SeriesName:              item.SeriesName,
			SeriesID:                item.SeriesID,
			SeasonID:                item.SeasonID,
			SeriesPrimaryImageTag:   item.SeriesPrimaryImageTag,
			SeasonName:              item.SeasonName,
			ImageTags:               item.ImageTags,
			BackdropImageTags:       item.BackdropImageTags,
			ParentLogoImageTag:      item.ParentLogoImageTag,
			Chapters:                item.Chapters,
			MediaType:               item.MediaType,
		})
	}
	return out
}

func seasonOverview(season db.TMDBSeason, detail *db.TMDBCachedSeasonDetail) string {
	if strings.TrimSpace(season.Overview) != "" {
		return strings.TrimSpace(season.Overview)
	}
	for _, ep := range seasonDetailEpisodes(detail) {
		if strings.TrimSpace(ep.Overview) != "" {
			return strings.TrimSpace(ep.Overview)
		}
	}
	return ""
}

func seasonPremiereDate(season db.TMDBSeason, detail *db.TMDBCachedSeasonDetail) string {
	if strings.TrimSpace(season.AirDate) != "" {
		return preciseDateString(strings.TrimSpace(season.AirDate))
	}
	for _, ep := range seasonDetailEpisodes(detail) {
		if strings.TrimSpace(ep.AirDate) != "" {
			return preciseDateString(strings.TrimSpace(ep.AirDate))
		}
	}
	return ""
}

func seasonProductionYear(season db.TMDBSeason, series *db.TMDBCachedDetail, detail *db.TMDBCachedSeasonDetail) int {
	if year := YearFromDate(strings.TrimSpace(season.AirDate)); year > 0 {
		return year
	}
	for _, ep := range seasonDetailEpisodes(detail) {
		if year := YearFromDate(strings.TrimSpace(ep.AirDate)); year > 0 {
			return year
		}
	}
	if series != nil {
		return YearFromDate(strings.TrimSpace(series.FirstAir))
	}
	return 0
}

type resolvedSitePan struct {
	RawLabel     string
	DisplayLabel string
	PanMock      bool
	Episodes     []catpawrunner.Episode
}

type panAliasRule struct {
	Pan     string
	PanNorm string
	Aliases []string
}

func fetchRawSiteDetailPans(database *db.DB, userID int64, siteKey string, siteDetail string) ([]catpawrunner.Pan, error) {
	if database == nil || strings.TrimSpace(siteKey) == "" || strings.TrimSpace(siteDetail) == "" {
		return []catpawrunner.Pan{}, nil
	}
	spiderAPI := strings.TrimSpace(smart.ResolveSpiderAPIBySiteKey(database, siteKey))
	if spiderAPI == "" {
		return []catpawrunner.Pan{}, nil
	}
	apiBase := strings.TrimSpace(smart.ResolveCatApiBaseForUser(database, &smart.User{ID: fmt.Sprintf("%d", userID)}))
	if apiBase == "" {
		return []catpawrunner.Pan{}, nil
	}
	raw, err := cache.RequestSpiderDetailDirect(apiBase, spiderAPI, strings.TrimSpace(siteDetail))
	if err != nil || raw == nil {
		return nil, err
	}
	playFrom, playURL := catpawrunner.ExtractDetailPlayFromURL(raw)
	pans := catpawrunner.ParsePlaySourcesForDetail(playFrom, playURL, smart.IsPanMockEnabled(raw))
	if pans == nil {
		pans = []catpawrunner.Pan{}
	}
	if smart.IsPanMockEnabled(raw) {
		for i := range pans {
			pans[i].PanMockEnabled = true
		}
	}
	return pans, nil
}

func fetchResolvedSiteDetailPans(database *db.DB, userID int64, siteKey string, siteDetail string) ([]resolvedSitePan, error) {
	rawPans, err := fetchRawSiteDetailPans(database, userID, siteKey, siteDetail)
	if err != nil {
		return nil, err
	}
	resolvedPans, err := resolveSiteDetailPansForBrowse(database, rawPans)
	if err != nil {
		return nil, err
	}
	return buildResolvedSitePans(database, resolvedPans), nil
}

func buildResolvedSitePans(database *db.DB, pans []catpawrunner.Pan) []resolvedSitePan {
	out := make([]resolvedSitePan, 0, len(pans))
	for _, pan := range pans {
		eps := filterValidSiteEpisodes(pan.Episodes, pan.PanMockEnabled)
		if len(eps) == 0 {
			continue
		}
		rawLabel := strings.TrimSpace(pan.Label)
		out = append(out, resolvedSitePan{
			RawLabel:     rawLabel,
			DisplayLabel: resolveSitePanDisplayLabel(database, rawLabel),
			PanMock:      pan.PanMockEnabled,
			Episodes:     eps,
		})
	}
	return out
}

func filterValidSiteEpisodes(in []catpawrunner.Episode, panMock bool) []catpawrunner.Episode {
	if len(in) == 0 {
		return []catpawrunner.Episode{}
	}
	out := make([]catpawrunner.Episode, 0, len(in))
	for _, ep := range in {
		if strings.TrimSpace(ep.URL) == "" {
			continue
		}
		// pan_mock may leave placeholder entries behind when provider expansion
		// fails; these are invalid and must not reach season/episode generation.
		if panMock && isPanMockPlaceholderEpisode(ep) {
			continue
		}
		out = append(out, ep)
	}
	return out
}

func isPanMockPlaceholderEpisode(ep catpawrunner.Episode) bool {
	urlLower := strings.ToLower(strings.TrimSpace(ep.URL))
	if urlLower == "" {
		return true
	}
	if strings.Contains(urlLower, "nopass") {
		return true
	}
	rawNames := smart.ExtractRawNamesFromEpisodeURL(strings.TrimSpace(ep.URL))
	for _, raw := range rawNames {
		nameLower := strings.ToLower(strings.TrimSpace(raw))
		if nameLower == "" {
			continue
		}
		if strings.Contains(nameLower, "nopass") {
			return true
		}
	}
	return false
}

func normalizePanFallbackLabel(raw string) string {
	s := strings.TrimSpace(raw)
	if s == "" {
		return ""
	}
	return strings.TrimSpace(strings.ReplaceAll(s, "_", " "))
}

func sitePanMatchText(raw string) string {
	s := strings.ToLower(strings.TrimSpace(raw))
	if s == "" {
		return ""
	}
	if strings.Contains(s, "-") {
		parts := strings.SplitN(s, "-", 2)
		return strings.TrimSpace(parts[0])
	}
	return s
}

func normalizePanAliasToken(raw string) string {
	return strings.ToLower(strings.TrimSpace(raw))
}

func parsePanAliasMappings(database *db.DB) []panAliasRule {
	if database == nil {
		return nil
	}
	rows, err := database.ListSmartPanAliasMappings()
	if err != nil || len(rows) == 0 {
		return nil
	}
	out := make([]panAliasRule, 0, len(rows))
	for _, row := range rows {
		pan := strings.TrimSpace(row.Pan)
		if pan == "" {
			continue
		}
		rule := panAliasRule{
			Pan:     pan,
			PanNorm: normalizePanAliasToken(pan),
			Aliases: EmptyStrings(),
		}
		for _, part := range strings.Split(strings.TrimSpace(row.Aliases), ",") {
			token := normalizePanAliasToken(part)
			if token == "" {
				continue
			}
			rule.Aliases = append(rule.Aliases, token)
		}
		out = append(out, rule)
	}
	return out
}

func resolveSitePanDisplayLabel(database *db.DB, rawLabel string) string {
	matchText := sitePanMatchText(rawLabel)
	if matchText == "" {
		return normalizePanFallbackLabel(rawLabel)
	}
	rules := parsePanAliasMappings(database)
	for _, rule := range rules {
		// "-" only decides whether we match on the prefix or the full label.
		// The actual match rule is "contains token", and the first hit wins.
		if rule.PanNorm != "" && strings.Contains(matchText, rule.PanNorm) {
			return rule.Pan
		}
		for _, alias := range rule.Aliases {
			if alias != "" && strings.Contains(matchText, alias) {
				return rule.Pan
			}
		}
	}
	return normalizePanFallbackLabel(rawLabel)
}

func resolveSiteSeriesName(database *db.DB, siteKey string, siteDetail string, meta siteDetailMeta) string {
	name := strings.TrimSpace(meta.Name)
	if name != "" {
		return name
	}
	return ""
}

func siteEpisodeDisplayName(ep catpawrunner.Episode, rawPanLabel string, panMock bool, seriesName string, epNo int) string {
	name := strings.TrimSpace(ep.Name)
	rawName := ""
	if rawNames := smart.ExtractRawNamesFromEpisodeURL(strings.TrimSpace(ep.URL)); len(rawNames) > 0 {
		rawName = strings.TrimSpace(rawNames[0])
	}
	pid := ""
	if panMock {
		pid = smart.PanMockProviderFromLabel(strings.TrimSpace(ep.Flag))
		if pid == "" {
			pid = smart.PanMockProviderFromLabel(strings.TrimSpace(rawPanLabel))
		}
	}
	if rawName != "" {
		name = smart.PickEpisodeDisplayName(name, rawName, strings.ToLower(strings.TrimSpace(seriesName)), panMock && pid != "")
	}
	if strings.TrimSpace(name) != "" {
		return strings.TrimSpace(name)
	}
	return ""
}

func siteEpisodeFileName(ep catpawrunner.Episode, displayName string, epNo int) string {
	if strings.TrimSpace(displayName) != "" {
		return strings.TrimSpace(displayName)
	}
	if rawNames := smart.ExtractRawNamesFromEpisodeURL(strings.TrimSpace(ep.URL)); len(rawNames) > 0 {
		if raw := strings.TrimSpace(rawNames[0]); raw != "" {
			return raw
		}
	}
	if strings.TrimSpace(ep.Name) != "" {
		return strings.TrimSpace(ep.Name)
	}
	return fmt.Sprintf("E%02d", epNo)
}

func seasonDetailEpisodes(detail *db.TMDBCachedSeasonDetail) []db.TMDBCachedSeasonEpisode {
	if detail == nil {
		return nil
	}
	return detail.Episodes
}

func episodeName(ep db.TMDBCachedSeasonEpisode) string {
	if strings.TrimSpace(ep.Name) != "" {
		return strings.TrimSpace(ep.Name)
	}
	return ""
}

func episodeFileName(ep db.TMDBCachedSeasonEpisode, season int) string {
	return fmt.Sprintf("S%02dE%02d", season, ep.EpisodeNumber)
}

func siteSnapshotShouldAdvance(snap db.PlayHistorySnapshot) bool {
	if snap.Pos <= 0 {
		return true
	}
	if snap.Runtime <= 0 {
		return false
	}
	if snap.Pos >= snap.Runtime {
		return true
	}
	if snap.Runtime-snap.Pos <= 30*10_000_000 {
		return true
	}
	if float64(snap.Pos)*100/float64(snap.Runtime) >= 90 {
		return true
	}
	return false
}
