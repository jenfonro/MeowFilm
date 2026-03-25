package emby_service

import (
	"encoding/json"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/cache"
	"github.com/jenfonro/meowfilm/server/catpawrunner"
	metadata_tmdb "github.com/jenfonro/meowfilm/server/metadata/tmdb"
	"github.com/jenfonro/meowfilm/server/sites"
	"github.com/jenfonro/meowfilm/server/smart"
)

type searchSiteHit struct {
	SiteKey    string
	SiteName   string
	SpiderAPI  string
	SiteDetail string
	Name       string
	Pic        string
	Remark     string
	Score      int
	TitleLen   int
	SiteOrder  int
	Seq        int
}

type SearchQueryShape string

const (
	SearchQueryShapeInfuse SearchQueryShape = "infuse"
	SearchQueryShapeLenna  SearchQueryShape = "lenna"
	SearchQueryShapeFamily SearchQueryShape = "family"
)

// BuildSearchItemsPayload renders the search family behind
// /Users/{id}/Items?...SearchTerm=... and intentionally stays separate from
// section item lists and latest items.
func BuildSearchItemsPayload(
	database *db.DB,
	userID int64,
	serverID string,
	includeItemTypes string,
	searchTerm string,
	startIndex int,
	limit int,
	shape SearchQueryShape,
) (items []any, total int, ok bool, err error) {
	if database == nil {
		return EmptyAnySlice(), 0, true, nil
	}
	query := strings.TrimSpace(searchTerm)
	if query == "" {
		return EmptyAnySlice(), 0, true, nil
	}
	if startIndex < 0 {
		startIndex = 0
	}
	if limit <= 0 {
		limit = 24
	}
	if limit > 60 {
		limit = 60
	}

	typesSet := map[string]struct{}{}
	for _, part := range strings.Split(includeItemTypes, ",") {
		t := strings.ToLower(strings.TrimSpace(part))
		if t == "" {
			continue
		}
		if t == "tv" {
			t = "series"
		}
		typesSet[t] = struct{}{}
	}
	if len(typesSet) == 0 {
		typesSet["movie"] = struct{}{}
		typesSet["series"] = struct{}{}
	}

	scoreTerm := canonicalSearchTerm(query)
	if scoreTerm == "" {
		scoreTerm = query
	}
	tmdbSorted, err := searchTMDBItems(database, scoreTerm, typesSet)
	if err != nil {
		return nil, 0, false, err
	}
	siteSorted := []searchSiteHit(nil)
	if wantsSearchSiteResults(shape) {
		siteSorted = searchSiteItems(database, userID, scoreTerm, 1500*time.Millisecond)
	}

	results := make([]any, 0, len(tmdbSorted)+len(siteSorted))
	for idx := range tmdbSorted {
		if shouldDropSearchTMDBMovie(tmdbSorted[idx]) {
			continue
		}
		if item, ok := buildSearchTMDBItemDTOByShape(database, userID, serverID, tmdbSorted[idx], shape); ok {
			results = append(results, item)
		}
	}
	if wantsSearchSiteResults(shape) {
		for idx := range siteSorted {
			if item, ok := buildSearchSiteSeriesItemDTOByShape(database, serverID, siteSorted[idx], shape); ok {
				results = append(results, item)
			}
		}
	}

	totalItems := len(results)
	if startIndex >= totalItems {
		return EmptyAnySlice(), totalItems, true, nil
	}
	end := startIndex + limit
	if end > totalItems {
		end = totalItems
	}
	items = make([]any, 0, end-startIndex)
	for idx := startIndex; idx < end; idx++ {
		items = append(items, results[idx])
	}
	return items, totalItems, true, nil
}

func wantsSearchSiteResults(shape SearchQueryShape) bool {
	return shape != SearchQueryShapeInfuse
}

func buildSearchTMDBItemDTOByShape(database *db.DB, userID int64, serverID string, item metadata_tmdb.SearchItem, shape SearchQueryShape) (any, bool) {
	switch strings.ToLower(strings.TrimSpace(item.MediaType)) {
	case "movie":
		source := buildSearchMovieItemDTO(database, userID, serverID, item)
		switch shape {
		case SearchQueryShapeLenna:
			return renderSearchLennaMovieItem(source), true
		case SearchQueryShapeFamily:
			return renderSearchFamilyMovieItem(source), true
		default:
			return source, true
		}
	case "tv":
		source := buildSearchSeriesItemDTO(database, userID, serverID, item)
		switch shape {
		case SearchQueryShapeLenna:
			return renderSearchLennaSeriesItem(source), true
		case SearchQueryShapeFamily:
			return renderSearchFamilySeriesItem(source), true
		default:
			return source, true
		}
	default:
		return nil, false
	}
}

func buildSearchSiteSeriesItemDTOByShape(database *db.DB, serverID string, hit searchSiteHit, shape SearchQueryShape) (any, bool) {
	source, ok := buildSearchSiteSeriesItemDTO(database, serverID, hit, shape == SearchQueryShapeInfuse)
	if !ok {
		return nil, false
	}
	switch shape {
	case SearchQueryShapeLenna:
		return renderSearchLennaSeriesItem(source), true
	case SearchQueryShapeFamily:
		return renderSearchFamilySeriesItem(source), true
	default:
		return source, true
	}
}

func renderSearchLennaMovieItem(source SearchMovieItemDTO) SearchLennaMovieItemDTO {
	return SearchLennaMovieItemDTO{
		Name:              source.Name,
		ServerID:          source.ServerID,
		ID:                source.ID,
		SupportsSync:      true,
		Container:         source.Container,
		CommunityRating:   source.CommunityRating,
		RunTimeTicks:      source.RunTimeTicks,
		ProductionYear:    source.ProductionYear,
		IsFolder:          source.IsFolder,
		Type:              source.Type,
		UserData:          source.UserData,
		ImageTags:         SearchPrimaryImageTagsDTO{Primary: source.ImageTags.Primary},
		BackdropImageTags: source.BackdropImageTags,
		MediaType:         source.MediaType,
	}
}

func renderSearchLennaSeriesItem(source SearchSeriesItemDTO) SearchLennaSeriesItemDTO {
	return SearchLennaSeriesItemDTO{
		Name:              source.Name,
		ServerID:          source.ServerID,
		ID:                source.ID,
		SupportsSync:      true,
		RunTimeTicks:      source.RunTimeTicks,
		ProductionYear:    source.ProductionYear,
		IsFolder:          source.IsFolder,
		Type:              source.Type,
		UserData:          source.UserData,
		AirDays:           source.AirDays,
		ImageTags:         SearchPrimaryImageTagsDTO{Primary: source.ImageTags.Primary},
		BackdropImageTags: source.BackdropImageTags,
	}
}

func renderSearchFamilyMovieItem(source SearchMovieItemDTO) SearchFamilyMovieItemDTO {
	return SearchFamilyMovieItemDTO{
		Name:                source.Name,
		ServerID:            source.ServerID,
		ID:                  source.ID,
		PremiereDate:        source.PremiereDate,
		ProductionLocations: source.ProductionLocations,
		Overview:            source.Overview,
		CommunityRating:     source.CommunityRating,
		RunTimeTicks:        source.RunTimeTicks,
		ProductionYear:      source.ProductionYear,
		IsFolder:            source.IsFolder,
		Type:                source.Type,
		UserData:            source.UserData,
		ImageTags:           source.ImageTags,
		BackdropImageTags:   source.BackdropImageTags,
		MediaType:           source.MediaType,
	}
}

func renderSearchFamilySeriesItem(source SearchSeriesItemDTO) SearchFamilySeriesItemDTO {
	return SearchFamilySeriesItemDTO{
		Name:              source.Name,
		ServerID:          source.ServerID,
		ID:                source.ID,
		PremiereDate:      source.PremiereDate,
		Overview:          source.Overview,
		RunTimeTicks:      source.RunTimeTicks,
		ProductionYear:    source.ProductionYear,
		IsFolder:          source.IsFolder,
		Type:              source.Type,
		UserData:          source.UserData,
		AirDays:           source.AirDays,
		ImageTags:         source.ImageTags,
		BackdropImageTags: source.BackdropImageTags,
	}
}

func buildSearchMovieItemDTO(database *db.DB, userID int64, serverID string, item metadata_tmdb.SearchItem) SearchMovieItemDTO {
	id := buildMovieID(item.ID)
	overview := strings.TrimSpace(item.Overview)
	premiereDate := preciseDateString(item.ReleaseDate)
	state := MovieItemState(false, false)
	meta := LatestMovieMetadataFromTMDB(database, item.ID, item.PosterPath)
	dateCreated := meta.DateCreated
	if strings.TrimSpace(meta.PremiereDate) != "" {
		premiereDate = meta.PremiereDate
	}
	productionYear := item.Year
	if meta.ProductionYear > 0 {
		productionYear = meta.ProductionYear
	}
	if strings.TrimSpace(meta.Overview) != "" {
		overview = meta.Overview
	}
	genres := EmptyStrings()
	genreItems := EmptyNamedIDs()
	if len(meta.Genres) > 0 {
		genres = meta.Genres
	}
	if len(meta.GenreItems) > 0 {
		genreItems = meta.GenreItems
	}
	providerIDs := ProviderIDsFromTMDBAny(item.ID)
	if len(meta.ProviderIDs) > 0 {
		providerIDs = meta.ProviderIDs
	}
	communityRating := item.VoteAverage
	if meta.CommunityRating > 0 {
		communityRating = meta.CommunityRating
	}
	var hist *db.PlayHistoryRow
	if database != nil && userID > 0 {
		hist, _ = database.GetPlayHistoryLatestByTMDB(userID, "movie", item.ID)
	}
	container, path, mediaSources := LatestPathAndMediaSources(id, hist)
	if strings.TrimSpace(path) == "" {
		path = VirtualMoviePath(strings.TrimSpace(item.Title), productionYear, strings.TrimSpace(item.Title)+".mp4")
	}
	if hist != nil && hist.UpdatedAt > 0 {
		dateCreated = ProtocolCreatedDate(hist.UpdatedAt)
	}
	userData := EmptyMovieLatestUserData()
	if hist != nil {
		userData.PlaybackPositionTicks = maxInt64(0, hist.PlaybackPositionTicks)
		if hist.PlaybackPositionTicks > 0 {
			userData.PlayCount = 1
		}
		if hist.PlaybackRuntimeTicks > 0 && hist.PlaybackPositionTicks > 0 {
			pct := PlayedPercentage(hist.PlaybackPositionTicks, hist.PlaybackRuntimeTicks)
			userData.PlayedPercentage = &pct
		}
	}
	runtimeTicks := int64(0)
	if hist != nil {
		runtimeTicks = hist.PlaybackRuntimeTicks
	}
	if runtimeTicks <= 0 && len(mediaSources) > 0 {
		runtimeTicks = mediaSources[0].RunTimeTicks
	}
	size, bitrate := mediaSourceSizeAndBitrate(mediaSources)
	if size <= 0 || bitrate <= 0 {
		statSize, statBitrate := MediaFileSizeAndBitrate(path, runtimeTicks)
		if size <= 0 {
			size = statSize
		}
		if bitrate <= 0 {
			bitrate = statBitrate
		}
	}
	parentID := ResolveMovieParentSectionID(database)
	return SearchMovieItemDTO{
		Name:                  strings.TrimSpace(item.Title),
		ServerID:              strings.TrimSpace(serverID),
		ID:                    id,
		Etag:                  StableItemEtag(id),
		DateCreated:           dateCreated,
		Container:             container,
		SortName:              SortNameOrName(strings.TrimSpace(item.Title)),
		PremiereDate:          premiereDate,
		ProductionLocations:   []string{"智能播放"},
		MediaSources:          mediaSources,
		AlternateMediaSources: EmptyResumeMediaSources(),
		Path:                  path,
		OfficialRating:        searchMovieOfficialRating(database, item.ID),
		Overview:              overview,
		Genres:                genres,
		CommunityRating:       communityRating,
		RunTimeTicks:          runtimeTicks,
		Size:                  size,
		Bitrate:               bitrate,
		ProductionYear:        productionYear,
		ProviderIDs:           providerIDs,
		IsFolder:              state.IsFolder,
		ParentID:              parentID,
		Type:                  state.Type,
		GenreItems:            genreItems,
		UserData:              userData,
		ImageTags:             ImageTagsDTO{Primary: SearchTMDBPrimaryTag(item.PosterPath), Logo: StableMD5Hex(id + "|logo")},
		BackdropImageTags:     BackdropTagsOrEmpty(firstBackdropTags(meta.BackdropTags, backdropTagsFromAsset(item.BackdropPath))),
		MediaType:             MediaTypeVideo,
	}
}

func buildSearchSeriesItemDTO(database *db.DB, userID int64, serverID string, item metadata_tmdb.SearchItem) SearchSeriesItemDTO {
	id := buildSeriesID(item.ID)
	overview := strings.TrimSpace(item.Overview)
	premiereDate := preciseDateString(item.FirstAirDate)
	state := SeriesItemState(false, false)
	meta := LatestTVMetadataFromTMDB(database, item.ID, item.PosterPath)
	dateCreated := meta.DateCreated
	if strings.TrimSpace(meta.PremiereDate) != "" {
		premiereDate = meta.PremiereDate
	}
	productionYear := item.Year
	if meta.ProductionYear > 0 {
		productionYear = meta.ProductionYear
	}
	if strings.TrimSpace(meta.Overview) != "" {
		overview = meta.Overview
	}
	genres := EmptyStrings()
	genreItems := EmptyNamedIDs()
	if len(meta.Genres) > 0 {
		genres = meta.Genres
	}
	if len(meta.GenreItems) > 0 {
		genreItems = meta.GenreItems
	}
	providerIDs := ProviderIDsFromTMDBAny(item.ID)
	if len(meta.ProviderIDs) > 0 {
		providerIDs = meta.ProviderIDs
	}
	communityRating := item.VoteAverage
	if meta.CommunityRating > 0 {
		communityRating = meta.CommunityRating
	}
	recursiveCount := maxInt(0, meta.RecursiveCount)
	childCount := 0
	var hist *db.PlayHistoryRow
	if database != nil && userID > 0 {
		hist, _ = database.GetPlayHistoryLatestByTMDB(userID, "tv", item.ID)
	}
	path := searchSeriesPath(item.Title, productionYear, hist)
	userData := BuildSeriesUserData(hist)
	if detail, err := loadTVSeriesDetailView(database, item.ID); err == nil && detail != nil {
		if recursiveCount <= 0 {
			recursiveCount = detail.EpisodeCount
		}
		childCount = len(detail.Seasons)
		if userData.UnplayedItemCount == 0 && hist != nil {
			if nextUp, err := loadTVNextUpView(database, item.ID, hist, 512); err == nil && nextUp != nil {
				userData.UnplayedItemCount = len(nextUp.Candidates)
			}
		}
	}
	if childCount <= 0 && recursiveCount > 0 {
		childCount = 1
	}
	runtimeTicks := searchSeriesRuntimeTicks(database, item.ID, hist)
	productionLocations := []string{"智能播放"}
	if len(item.OriginCountry) > 0 {
		productionLocations = append([]string(nil), item.OriginCountry...)
	}
	parentID := ResolveSeriesParentSectionID(database, genres)
	return SearchSeriesItemDTO{
		Name:                strings.TrimSpace(item.Title),
		ServerID:            strings.TrimSpace(serverID),
		ID:                  id,
		Etag:                StableItemEtag(id),
		DateCreated:         dateCreated,
		SortName:            SortNameOrName(strings.TrimSpace(item.Title)),
		PremiereDate:        premiereDate,
		ProductionLocations: productionLocations,
		Path:                path,
		Overview:            overview,
		Genres:              genres,
		CommunityRating:     communityRating,
		RunTimeTicks:        runtimeTicks,
		ProductionYear:      productionYear,
		ProviderIDs:         providerIDs,
		IsFolder:            state.IsFolder,
		ParentID:            parentID,
		Type:                state.Type,
		GenreItems:          genreItems,
		UserData:            userData,
		RecursiveItemCount:  recursiveCount,
		ChildCount:          childCount,
		AirDays:             EmptyStrings(),
		ImageTags:           ImageTagsDTO{Primary: SearchTMDBPrimaryTag(item.PosterPath), Logo: StableMD5Hex(id + "|logo")},
		BackdropImageTags:   BackdropTagsOrEmpty(firstBackdropTags(meta.BackdropTags, backdropTagsFromAsset(item.BackdropPath))),
	}
}

func itemRuntimeMinutes(database *db.DB, tmdbType string, tmdbID int) int {
	if database == nil || tmdbID <= 0 {
		return 0
	}
	detail, err := database.ReadTMDBCachedDetail(tmdbType, tmdbID, "zh-CN")
	if err != nil || detail == nil {
		return 0
	}
	return detail.RuntimeMinutes
}

func searchSeriesPath(title string, productionYear int, hist *db.PlayHistoryRow) string {
	return VirtualSeriesPath(strings.TrimSpace(title), productionYear)
}

func searchSeriesRuntimeTicks(database *db.DB, tmdbID int, hist *db.PlayHistoryRow) int64 {
	if hist != nil && hist.PlaybackRuntimeTicks > 0 {
		return hist.PlaybackRuntimeTicks
	}
	if minutes, err := database.ReadTMDBAverageEpisodeRuntime(tmdbID); err == nil && minutes > 0 {
		return runtimeTicksFromMinutes(minutes)
	}
	return runtimeTicksFromMinutes(itemRuntimeMinutes(database, "tv", tmdbID))
}

func mediaSourceSizeAndBitrate(mediaSources []ResumeMediaSourceDTO) (int64, int) {
	if len(mediaSources) == 0 {
		return 0, 0
	}
	return mediaSources[0].Size, mediaSources[0].Bitrate
}

func firstBackdropTags(values ...[]string) []string {
	for _, value := range values {
		if len(value) > 0 {
			return value
		}
	}
	return EmptyStrings()
}

func searchMovieOfficialRating(database *db.DB, tmdbID int) string {
	if database == nil || tmdbID <= 0 {
		return ""
	}
	raw, err := database.ReadTMDBRawDetailJSON("movie", tmdbID)
	if err != nil || strings.TrimSpace(raw) == "" {
		return ""
	}
	var payload struct {
		ReleaseDates struct {
			Results []struct {
				ISO31661     string `json:"iso_3166_1"`
				ReleaseDates []struct {
					Certification string `json:"certification"`
				} `json:"release_dates"`
			} `json:"results"`
		} `json:"release_dates"`
		Certifications struct {
			US []struct {
				Rating string `json:"rating"`
			} `json:"US"`
		} `json:"certifications"`
	}
	if err := json.Unmarshal([]byte(raw), &payload); err != nil {
		return ""
	}
	for _, result := range payload.ReleaseDates.Results {
		if strings.EqualFold(strings.TrimSpace(result.ISO31661), "US") {
			for _, item := range result.ReleaseDates {
				if rating := strings.TrimSpace(item.Certification); rating != "" {
					return rating
				}
			}
		}
	}
	for _, item := range payload.Certifications.US {
		if rating := strings.TrimSpace(item.Rating); rating != "" {
			return rating
		}
	}
	return ""
}

func buildSearchSiteSeriesItemDTO(database *db.DB, serverID string, hit searchSiteHit, infuseMode bool) (SearchSeriesItemDTO, bool) {
	if database == nil || strings.TrimSpace(hit.Name) == "" || strings.TrimSpace(hit.SiteKey) == "" || strings.TrimSpace(hit.SiteDetail) == "" {
		return SearchSeriesItemDTO{}, false
	}
	id := buildSiteSeriesID(strings.TrimSpace(hit.SiteKey), strings.TrimSpace(hit.SiteDetail))
	if id == "" {
		return SearchSeriesItemDTO{}, false
	}
	state := SeriesItemState(false, false)
	parentID := ResolveSeriesParentSectionID(database, nil)
	baseName := strings.TrimSpace(hit.Name)
	name := baseName
	sortName := SortNameOrName(baseName)
	if infuseMode {
		siteName := strings.TrimSpace(hit.SiteName)
		if siteName != "" {
			name = name + " · " + siteName
		}
	}
	return SearchSeriesItemDTO{
		Name:                name,
		ServerID:            strings.TrimSpace(serverID),
		ID:                  id,
		Etag:                StableItemEtag(id),
		DateCreated:         EmbyZeroTimeString(),
		SortName:            sortName,
		PremiereDate:        "",
		ProductionLocations: []string{strings.TrimSpace(hit.SiteName)},
		Path:                VirtualSeriesPath(baseName, 0),
		Overview:            strings.TrimSpace(hit.Remark),
		Genres:              EmptyStrings(),
		CommunityRating:     0,
		RunTimeTicks:        0,
		ProductionYear:      0,
		ProviderIDs:         EmptyAnyMap(),
		IsFolder:            state.IsFolder,
		ParentID:            parentID,
		Type:                state.Type,
		GenreItems:          EmptyNamedIDs(),
		UserData:            TVLatestUserDataDTO{PlayCount: 0, IsFavorite: false, Played: false},
		RecursiveItemCount:  1,
		ChildCount:          1,
		AirDays:             EmptyStrings(),
		ImageTags:           ImageTagsDTO{Primary: SearchSitePrimaryTag(hit.Pic), Logo: StableMD5Hex(id + "|logo")},
		BackdropImageTags:   EmptyStrings(),
	}, true
}

func searchTMDBItems(database *db.DB, query string, typesSet map[string]struct{}) ([]metadata_tmdb.SearchItem, error) {
	results, err := metadata_tmdb.SearchMulti(database, query)
	if err != nil {
		return nil, err
	}
	type row struct {
		Item     metadata_tmdb.SearchItem
		Score    int
		TitleLen int
		Seq      int
	}
	rows := make([]row, 0, len(results))
	seq := 0
	for _, item := range results {
		if item.ID <= 0 || strings.TrimSpace(item.Title) == "" {
			continue
		}
		mt := strings.ToLower(strings.TrimSpace(item.MediaType))
		if mt == "tv" {
			mt = "series"
		}
		if _, ok := typesSet[mt]; !ok {
			continue
		}
		seq++
		rows = append(rows, row{
			Item:     item,
			Score:    smart.ComputeMatchScore(query, item.Title),
			TitleLen: smart.TitleLenForSort(item.Title),
			Seq:      seq,
		})
	}
	sort.SliceStable(rows, func(i, j int) bool {
		if rows[i].Score != rows[j].Score {
			return rows[i].Score > rows[j].Score
		}
		if rows[i].TitleLen != rows[j].TitleLen {
			return rows[i].TitleLen < rows[j].TitleLen
		}
		return rows[i].Seq < rows[j].Seq
	})
	out := make([]metadata_tmdb.SearchItem, 0, len(rows))
	for _, row := range rows {
		out = append(out, row.Item)
	}
	return out, nil
}

func searchSiteItems(database *db.DB, userID int64, query string, maxWait time.Duration) []searchSiteHit {
	if database == nil || strings.TrimSpace(query) == "" {
		return nil
	}
	apiBase := strings.TrimSpace(smart.ResolveCatApiBaseForUser(database, &smart.User{ID: strconv.FormatInt(userID, 10)}))
	if apiBase == "" {
		return nil
	}
	rawSites, _ := database.ListVideoSourceSites()
	states, _ := database.ReadVideoSourceSiteStates()
	sitesList := make([]sites.Site, 0, len(rawSites))
	for _, site := range rawSites {
		sitesList = append(sitesList, sites.Site{
			Key:  site.Key,
			Name: site.Name,
			API:  site.API,
			Type: site.Type,
		})
	}
	ordered := sites.ApplySiteOrder(sitesList, smart.LoadSiteOrder(database, &smart.User{ID: strconv.FormatInt(userID, 10)}))
	searchSites := make([]sites.Site, 0, len(ordered))
	for _, site := range ordered {
		if strings.TrimSpace(site.Key) == "" || strings.TrimSpace(site.API) == "" || sites.IsConfigCenterSite(site) {
			continue
		}
		if st, ok := states[site.Key]; ok {
			if !st.Enabled || !st.Search {
				continue
			}
		}
		searchSites = append(searchSites, site)
	}
	if len(searchSites) == 0 {
		return nil
	}

	deadline := time.Now().Add(maxWait)
	type result struct {
		Site sites.Site
		List []catpawrunner.SearchItem
	}
	jobs := make(chan sites.Site, len(searchSites))
	results := make(chan result, len(searchSites))
	var wg sync.WaitGroup
	for i := 0; i < len(searchSites); i++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			for site := range jobs {
				remain := time.Until(deadline)
				if remain <= 0 {
					continue
				}
				raw, err := cache.RequestSpiderSearchWithTimeout(apiBase, site.API, query, 1, remain)
				if err != nil || raw == nil {
					continue
				}
				list := catpawrunner.NormalizeSearchList(raw)
				if len(list) == 0 {
					continue
				}
				select {
				case results <- result{Site: site, List: list}:
				default:
				}
			}
		}()
	}
	for _, site := range searchSites {
		jobs <- site
	}
	close(jobs)
	go func() {
		wg.Wait()
		close(results)
	}()

	siteOrder := map[string]int{}
	for i, site := range searchSites {
		siteOrder[site.Key] = i
	}
	seq := 0
	out := make([]searchSiteHit, 0, 32)
	timer := time.NewTimer(time.Until(deadline))
	defer timer.Stop()

	apply := func(site sites.Site, list []catpawrunner.SearchItem) {
		for _, item := range list {
			name := strings.TrimSpace(item.Name)
			vid := strings.TrimSpace(item.ID)
			if name == "" || vid == "" {
				continue
			}
			score := smart.ComputeMatchScore(query, name)
			if score <= 0 {
				continue
			}
			seq++
			out = append(out, searchSiteHit{
				SiteKey:    site.Key,
				SiteName:   site.Name,
				SpiderAPI:  site.API,
				SiteDetail: vid,
				Name:       name,
				Pic:        strings.TrimSpace(item.Pic),
				Remark:     strings.TrimSpace(item.Remark),
				Score:      score,
				TitleLen:   smart.TitleLenForSort(name),
				SiteOrder:  siteOrder[site.Key],
				Seq:        seq,
			})
		}
	}
	for {
		select {
		case res, ok := <-results:
			if !ok {
				goto done
			}
			apply(res.Site, res.List)
		case <-timer.C:
			goto done
		}
	}
done:
	sort.SliceStable(out, func(i, j int) bool {
		if out[i].Score != out[j].Score {
			return out[i].Score > out[j].Score
		}
		if out[i].SiteOrder != out[j].SiteOrder {
			return out[i].SiteOrder < out[j].SiteOrder
		}
		if out[i].TitleLen != out[j].TitleLen {
			return out[i].TitleLen < out[j].TitleLen
		}
		if out[i].SiteKey != out[j].SiteKey {
			return out[i].SiteKey < out[j].SiteKey
		}
		if out[i].SiteDetail != out[j].SiteDetail {
			return out[i].SiteDetail < out[j].SiteDetail
		}
		return out[i].Seq < out[j].Seq
	})
	return out
}

func canonicalSearchTerm(term string) string {
	in := strings.TrimSpace(term)
	if in == "" {
		return ""
	}
	var tr2s = map[rune]rune{
		'來': '来', '劍': '剑', '國': '国', '畫': '画', '後': '后', '視': '视', '電': '电',
		'這': '这', '裡': '里', '眾': '众', '劇': '剧', '體': '体', '書': '书', '雲': '云',
		'龍': '龙', '鳳': '凤', '陰': '阴', '陽': '阳', '讓': '让', '說': '说', '當': '当',
		'時': '时', '長': '长', '車': '车', '轉': '转', '於': '于', '對': '对', '愛': '爱',
		'親': '亲', '發': '发', '髮': '发', '變': '变', '個': '个', '與': '与', '見': '见',
		'門': '门', '開': '开', '關': '关', '會': '会', '學': '学', '曉': '晓', '兒': '儿', '東': '东',
	}
	var b strings.Builder
	b.Grow(len(in))
	for _, r := range in {
		if rr, ok := tr2s[r]; ok {
			b.WriteRune(rr)
			continue
		}
		b.WriteRune(r)
	}
	return strings.TrimSpace(b.String())
}

func shouldDropSearchTMDBMovie(item metadata_tmdb.SearchItem) bool {
	if !strings.EqualFold(strings.TrimSpace(item.MediaType), "movie") {
		return false
	}
	return strings.TrimSpace(item.ReleaseDate) == "" && item.Year <= 0
}
