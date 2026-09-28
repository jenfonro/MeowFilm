package emby_service

import (
	"path/filepath"
	"strings"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
	metadata_tmdb "github.com/jenfonro/meowfilm/server/metadata/tmdb"
)

func BuildResumePlaybackPayload(database *db.DB, userID int64, serverID string, playbackItemID string) (any, bool, error) {
	if database == nil || userID <= 0 {
		return nil, false, nil
	}
	ref := parseItemRef(playbackItemID)
	if ref == nil || ref.Source != "tmdb" {
		return nil, false, nil
	}
	row, err := database.GetPlayHistoryLatestByTMDB(userID, ref.MediaType, ref.NumericID)
	if err != nil || row == nil {
		return nil, false, err
	}
	switch {
	case ref.MediaType == "movie" && ref.SubKind == "movie":
		return buildResumeTMDBMoviePayload(database, serverID, *row, ref), true, nil
	case ref.MediaType == "tv" && ref.SubKind == "episode":
		if !playHistoryMatchesTMDBEpisode(row, ref.NumericID, ref.Pan, ref.Episode) {
			return nil, false, nil
		}
		return buildResumeTMDBEpisodePayload(database, serverID, *row, ref), true, nil
	default:
		return nil, false, nil
	}
}

func buildResumeTMDBMoviePayload(database *db.DB, serverID string, row db.PlayHistoryRow, ref *itemRef) any {
	detail, _ := metadata_tmdb.GetMovieDetails(database, ref.NumericID)
	cachedDetail, _ := metadata_tmdb.GetDetailForBackend(database, "movie", ref.NumericID)
	name := strings.TrimSpace(row.ContentKey)
	if detail != nil && strings.TrimSpace(detail.Title) != "" {
		name = strings.TrimSpace(detail.Title)
	}
	itemID := buildMovieID(ref.NumericID)
	container := ""
	path := VirtualMoviePath(name, metadata_tmdb.ParseYearFromDate(strings.TrimSpace(cachedDetail.Release)), name+".mp4")
	mediaSourceID := StableMD5Hex(itemID + "|media")
	state := MovieItemState(false, false)
	backdropTags := EmptyStrings()
	if detail != nil {
		backdropTags = backdropTagsFromAsset(detail.Backdrop)
	}
	genres := resumeGenres(cachedDetail, "movie")
	return ResumeMovieItemDTO{
		Name:              name,
		ServerID:          strings.TrimSpace(serverID),
		ID:                itemID,
		Etag:              ProtocolEtag(),
		DateCreated:       resumeDateCreated(row),
		Container:         containerCSV(container),
		SortName:          SortNameOrName(name),
		PremiereDate:      resumeMoviePremiereDate(detail, cachedDetail),
		MediaSources:      resumeMediaSources(mediaSourceID, path, container, row),
		Path:              path,
		Overview:          resumeMovieOverview(detail, cachedDetail),
		Genres:            genres,
		CommunityRating:   resumeMovieCommunityRating(cachedDetail),
		RunTimeTicks:      row.PlaybackRuntimeTicks,
		Size:              0,
		Bitrate:           0,
		ProviderIDs:       ProviderIDsFromTMDBAny(ref.NumericID),
		IsFolder:          state.IsFolder,
		ParentID:          resumeRootParentID(database, "movie"),
		Type:              state.Type,
		GenreItems:        NamedGenreItems(genres),
		UserData:          EmptyMovieLatestUserData(),
		ImageTags:         ImageTagsForItem(itemID, true),
		BackdropImageTags: BackdropTagsOrEmpty(backdropTags),
		MediaType:         MediaTypeVideo,
	}
}

func buildResumeTMDBEpisodePayload(database *db.DB, serverID string, row db.PlayHistoryRow, ref *itemRef) any {
	view, _ := loadTVResumeEpisodeView(database, ref.NumericID, ref.Pan, ref.Episode)
	seriesID := buildSeriesID(ref.NumericID)
	seasonID := buildSeasonID(ref.NumericID, ref.Pan)
	itemID := buildEpisodeID(ref.NumericID, ref.Pan, ref.Episode)
	seriesName := strings.TrimSpace(row.ContentKey)
	if view != nil && view.Series != nil && strings.TrimSpace(view.Series.Title) != "" {
		seriesName = strings.TrimSpace(view.Series.Title)
	}
	seasonName := ""
	if view != nil && view.Season != nil {
		seasonName = strings.TrimSpace(view.Season.Name)
	}
	episodeName := ""
	runtime := row.PlaybackRuntimeTicks
	if view != nil && view.Episode != nil {
		if strings.TrimSpace(view.Episode.Name) != "" {
			episodeName = strings.TrimSpace(view.Episode.Name)
		}
		if runtime <= 0 && view.Episode.Runtime > 0 {
			runtime = int64(view.Episode.Runtime) * 60 * 10_000_000
		}
	}
	container := ""
	path := ""
	if view != nil && view.Series != nil {
		path = VirtualEpisodePath(strings.TrimSpace(view.Series.Title), parseTMDBCachedYear(view.Series), ref.Pan, playbackEpisodeFileName(view))
	}
	if path == "" {
		path = VirtualEpisodePath("", 0, ref.Pan, episodeName+".mp4")
	}
	mediaSourceID := StableMD5Hex(itemID + "|media")
	state := EpisodeItemState(false, false)
	primaryTag := PrimaryTagForItem(itemID)
	backdropTags := EmptyStrings()
	parentBackdropTags := EmptyStrings()
	if view != nil && view.Series != nil {
		parentBackdropTags = backdropTagsFromAsset(view.Series.Backdrop)
	}
	logoTag := StableMD5Hex(seriesID + "|logo")
	genres := resumeGenres(view.Series, "tv")
	return ResumeEpisodeItemDTO{
		Name:                    episodeName,
		ServerID:                strings.TrimSpace(serverID),
		ID:                      itemID,
		Etag:                    ProtocolEtag(),
		DateCreated:             resumeDateCreated(row),
		Container:               containerCSV(container),
		SortName:                SortNameOrName(episodeName),
		PremiereDate:            resumeEpisodePremiereDate(view),
		MediaSources:            resumeMediaSources(mediaSourceID, path, container, row),
		Path:                    path,
		Overview:                resumeEpisodeOverview(view),
		Genres:                  genres,
		CommunityRating:         resumeSeriesCommunityRating(view),
		RunTimeTicks:            runtime,
		Size:                    0,
		Bitrate:                 0,
		IndexNumber:             ref.Episode,
		ParentIndexNumber:       ref.Pan,
		ProviderIDs:             ProviderIDsFromTMDBAny(ref.NumericID),
		IsFolder:                state.IsFolder,
		ParentID:                seasonID,
		Type:                    state.Type,
		GenreItems:              NamedGenreItems(genres),
		ParentLogoItemID:        seriesID,
		ParentBackdropItemID:    seriesID,
		ParentBackdropImageTags: parentBackdropTags,
		UserData:                ResumeEpisodeUserDataDTO{},
		SeriesName:              seriesName,
		SeriesID:                seriesID,
		SeasonID:                seasonID,
		SeriesPrimaryImageTag:   PrimaryTagForItem(seriesID),
		SeasonName:              seasonName,
		ImageTags:               ImageTagsDTO{Primary: primaryTag},
		BackdropImageTags:       BackdropTagsOrEmpty(backdropTags),
		ParentLogoImageTag:      logoTag,
		MediaType:               MediaTypeVideo,
	}
}

func buildResumeMediaSource(id string, path string, container string, row db.PlayHistoryRow) ResumeMediaSourceDTO {
	return ResumeMediaSourceDTO{
		Chapters:             EmptyAnySlice(),
		Protocol:             "File",
		ID:                   id,
		Path:                 path,
		Type:                 "Default",
		Container:            container,
		Size:                 0,
		Name:                 mediaSourceName(row),
		IsRemote:             false,
		HasMixedProtocols:    false,
		RunTimeTicks:         row.PlaybackRuntimeTicks,
		SupportsTranscoding:  true,
		SupportsDirectStream: true,
		SupportsDirectPlay:   true,
		IsInfiniteStream:     false,
		RequiresOpening:      false,
		RequiresClosing:      false,
		RequiresLooping:      false,
		SupportsProbing:      true,
		MediaStreams:         EmptyAnySlice(),
		Formats:              EmptyAnySlice(),
		Bitrate:              0,
		RequiredHTTPHeaders:  EmptyRequiredHTTPHeaders(),
		AddAPIKeyToDirect:    false,
		ReadAtNativeFrame:    false,
		DefaultAudioStreamID: 0,
		ItemID:               strings.TrimSpace(row.PlaybackItemID),
		MediaSourceID:        id,
	}
}

func resumeMediaSources(id string, path string, container string, row db.PlayHistoryRow) []ResumeMediaSourceDTO {
	if strings.TrimSpace(path) == "" {
		return EmptyResumeMediaSources()
	}
	return []ResumeMediaSourceDTO{buildResumeMediaSource(id, path, container, row)}
}

func resumeDateCreated(row db.PlayHistoryRow) string {
	if row.UpdatedAt <= 0 {
		return EmbyZeroTimeString()
	}
	return embyTimeString(time.Unix(row.UpdatedAt, 0))
}

func resumeMoviePremiereDate(detail *metadata_tmdb.MovieDetailsResponse, cachedDetail *db.TMDBCachedDetail) string {
	if detail != nil {
		return preciseDateString(strings.TrimSpace(detail.ReleaseDate))
	}
	if cachedDetail != nil {
		return preciseDateString(strings.TrimSpace(cachedDetail.Release))
	}
	return ""
}

func resumeMovieOverview(detail *metadata_tmdb.MovieDetailsResponse, cachedDetail *db.TMDBCachedDetail) string {
	if detail != nil && strings.TrimSpace(detail.Overview) != "" {
		return strings.TrimSpace(detail.Overview)
	}
	if cachedDetail != nil {
		return strings.TrimSpace(cachedDetail.Overview)
	}
	return ""
}

func resumeMovieCommunityRating(detail *db.TMDBCachedDetail) float64 {
	if detail == nil {
		return 0
	}
	return detail.VoteAverage
}

func resumeEpisodePremiereDate(view *TVResumeEpisodeView) string {
	if view == nil || view.Episode == nil {
		return ""
	}
	return preciseDateString(strings.TrimSpace(view.Episode.AirDate))
}

func resumeEpisodeOverview(view *TVResumeEpisodeView) string {
	if view == nil || view.Episode == nil {
		return ""
	}
	return strings.TrimSpace(view.Episode.Overview)
}

func resumeSeriesCommunityRating(view *TVResumeEpisodeView) float64 {
	if view == nil || view.Series == nil {
		return 0
	}
	return view.Series.VoteAverage
}

func resumeGenres(detail any, mediaType string) []string {
	switch v := detail.(type) {
	case *db.TMDBCachedDetail:
		if v == nil {
			return EmptyStrings()
		}
		return GenreNamesFromIDs(mediaType, v.GenreIDs)
	default:
		return EmptyStrings()
	}
}

func resumeRootParentID(database *db.DB, mediaType string) string {
	sections, err := ListSections(database)
	if err != nil {
		return ""
	}
	for _, section := range sections {
		if !IsMediaLibrarySection(section) {
			continue
		}
		if strings.EqualFold(strings.TrimSpace(section.MediaType), strings.TrimSpace(mediaType)) {
			return strings.TrimSpace(section.ID)
		}
	}
	return ""
}

func seasonEpisodes(detail *db.TMDBCachedSeasonDetail) []db.TMDBCachedSeasonEpisode {
	if detail == nil {
		return nil
	}
	return detail.Episodes
}

func detectResumeContainer(fileName string) string {
	container := DetectMediaContainer(fileName, "mp4")
	if strings.TrimSpace(container) == "" {
		return "mp4"
	}
	return container
}

func containerCSV(container string) string {
	c := strings.TrimSpace(container)
	if c == "" {
		return "mp4,m4v"
	}
	if c == "mp4" {
		return "mp4,m4v"
	}
	return c
}

func mediaSourceName(row db.PlayHistoryRow) string {
	if name := strings.TrimSpace(row.SiteEpisodeFile); name != "" {
		return strings.TrimSuffix(name, filepath.Ext(name))
	}
	return strings.TrimSpace(row.ContentKey)
}
