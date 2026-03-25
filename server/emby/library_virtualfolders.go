package emby

import (
	"net/http"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/emby/emby_service"
)

func handleLibraryVirtualFolders(w http.ResponseWriter, r *http.Request, database *db.DB) {
	ctx, ok := resolveTopologyContext(w, r, database)
	if !ok || ctx == nil || ctx.Current == nil {
		return
	}
	items := make([]embyVirtualFolderDTO, 0, len(ctx.Sections))
	for _, section := range ctx.Sections {
		items = append(items, buildVirtualFolderDTO(section))
	}

	writeJSON(w, http.StatusOK, items)
}

func buildVirtualFolderDTO(section db.ThirdPartyClientHomeSection) embyVirtualFolderDTO {
	id := strings.TrimSpace(section.ID)
	name := strings.TrimSpace(section.Name)
	collectionType, hasCollectionType := emby_service.SectionCollectionType(section)

	item := embyVirtualFolderDTO{
		Name:               name,
		Locations:          virtualFolderLocations(section),
		LibraryOptions:     virtualFolderLibraryOptions(section),
		ItemID:             id,
		ID:                 id,
		GUID:               emby_service.StableMD5Hex("virtualfolder|" + id),
		PrimaryImageItemID: id,
		PrimaryImageTag:    emby_service.StableMD5Hex("virtualfolder|" + id + "|primary"),
	}
	if hasCollectionType {
		item.CollectionType = collectionType
	}
	return item
}

func virtualFolderLocations(section db.ThirdPartyClientHomeSection) []string {
	if isMixedVirtualFolder(section) {
		return []string{"/media/movie", "/media/tv"}
	}
	if strings.EqualFold(strings.TrimSpace(section.MediaType), "movie") {
		return []string{"/media/movie"}
	}
	return []string{"/media/tv"}
}

func virtualFolderContentType(section db.ThirdPartyClientHomeSection) string {
	if isMixedVirtualFolder(section) {
		return ""
	}
	if strings.EqualFold(strings.TrimSpace(section.MediaType), "movie") {
		return "movies"
	}
	return "tvshows"
}

func virtualFolderTypeOptions(section db.ThirdPartyClientHomeSection) []embyLibraryTypeOptionDTO {
	build := func(t string) embyLibraryTypeOptionDTO {
		return embyLibraryTypeOptionDTO{
			Type:                 t,
			MetadataFetchers:     []string{},
			MetadataFetcherOrder: []string{},
			ImageFetchers:        []string{},
			ImageFetcherOrder:    []string{},
			ImageOptions:         []any{},
		}
	}
	if isMixedVirtualFolder(section) {
		return []embyLibraryTypeOptionDTO{
			build("Series"),
			build("Season"),
			build("Episode"),
			build("Movie"),
		}
	}
	if strings.EqualFold(strings.TrimSpace(section.MediaType), "movie") {
		return []embyLibraryTypeOptionDTO{build("Movie")}
	}
	return []embyLibraryTypeOptionDTO{
		build("Series"),
		build("Season"),
		build("Episode"),
	}
}

func virtualFolderPathInfos(section db.ThirdPartyClientHomeSection) []embyLibraryPathInfoDTO {
	locations := virtualFolderLocations(section)
	out := make([]embyLibraryPathInfoDTO, 0, len(locations))
	for _, loc := range locations {
		out = append(out, embyLibraryPathInfoDTO{
			Path:     loc,
			Password: "",
		})
	}
	return out
}

func virtualFolderLibraryOptions(section db.ThirdPartyClientHomeSection) embyLibraryOptionsDTO {
	return embyLibraryOptionsDTO{
		EnableArchiveMediaFiles:                 false,
		EnablePhotos:                            true,
		EnableRealtimeMonitor:                   true,
		EnableMarkerDetection:                   false,
		EnableMarkerDetectionDuringLibraryScan:  false,
		IntroDetectionFingerprintLength:         10,
		EnableChapterImageExtraction:            false,
		ExtractChapterImagesDuringLibraryScan:   false,
		DownloadImagesInAdvance:                 false,
		CacheImages:                             false,
		ExcludeFromSearch:                       false,
		EnablePlexIgnore:                        false,
		PathInfos:                               virtualFolderPathInfos(section),
		IgnoreHiddenFiles:                       false,
		IgnoreFileExtensions:                    []string{},
		SaveLocalMetadata:                       false,
		SaveMetadataHidden:                      false,
		SaveLocalThumbnailSets:                  false,
		ImportPlaylists:                         true,
		EnableAutomaticSeriesGrouping:           true,
		ShareEmbeddedMusicAlbumImages:           true,
		EnableEmbeddedTitles:                    false,
		EnableAudioResume:                       false,
		AutoGenerateChapters:                    true,
		MergeTopLevelFolders:                    false,
		AutoGenerateChapterIntervalMinutes:      5,
		AutomaticRefreshIntervalDays:            0,
		PlaceholderMetadataRefreshIntervalDays:  0,
		PreferredMetadataLanguage:               "zh",
		PreferredImageLanguage:                  "zh",
		ContentType:                             virtualFolderContentType(section),
		MetadataCountryCode:                     "",
		MetadataSavers:                          []string{},
		DisabledLocalMetadataReaders:            []string{},
		DisabledLyricsFetchers:                  []string{},
		SaveLyricsWithMedia:                     true,
		LyricsDownloadMaxAgeDays:                180,
		LyricsFetcherOrder:                      []string{},
		LyricsDownloadLanguages:                 []string{},
		DisabledSubtitleFetchers:                []string{},
		SubtitleFetcherOrder:                    []string{},
		SkipSubtitlesIfEmbeddedSubtitlesPresent: false,
		SkipSubtitlesIfAudioTrackMatches:        false,
		SubtitleDownloadLanguages:               []string{},
		SubtitleDownloadMaxAgeDays:              180,
		RequirePerfectSubtitleMatch:             true,
		SaveSubtitlesWithMedia:                  true,
		ForcedSubtitlesOnly:                     false,
		HearingImpairedSubtitlesOnly:            false,
		TypeOptions:                             virtualFolderTypeOptions(section),
		CollapseSingleItemFolders:               false,
		ForceCollapseSingleItemFolders:          false,
		EnableAdultMetadata:                     false,
		ImportCollections:                       false,
		EnableMultiVersionByFiles:               true,
		EnableMultiVersionByMetadata:            true,
		EnableMultiPartItems:                    !strings.EqualFold(strings.TrimSpace(section.MediaType), "movie"),
		MinCollectionItems:                      2,
		MinResumePct:                            2,
		MaxResumePct:                            90,
		MinResumeDurationSeconds:                120,
		ThumbnailImagesIntervalSeconds:          -1,
		SampleIgnoreSize:                        314572800,
	}
}

func isMixedVirtualFolder(section db.ThirdPartyClientHomeSection) bool {
	if strings.EqualFold(strings.TrimSpace(section.Module), "history") {
		return true
	}
	return strings.EqualFold(strings.TrimSpace(section.MediaType), "mixed")
}
