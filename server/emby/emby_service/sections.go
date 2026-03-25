package emby_service

import (
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
)

func ListSections(database *db.DB) ([]db.ThirdPartyClientHomeSection, error) {
	if database == nil {
		return []db.ThirdPartyClientHomeSection{}, nil
	}
	return database.ReadThirdPartyClientHomeSections()
}

func ResolveSectionByID(database *db.DB, id string) (db.ThirdPartyClientHomeSection, bool, error) {
	want := strings.TrimSpace(id)
	if want == "" {
		return db.ThirdPartyClientHomeSection{}, false, nil
	}
	sections, err := ListSections(database)
	if err != nil {
		return db.ThirdPartyClientHomeSection{}, false, err
	}
	for _, section := range sections {
		if strings.TrimSpace(section.ID) == want {
			return section, true, nil
		}
	}
	return db.ThirdPartyClientHomeSection{}, false, nil
}

func LatestSectionKind(section db.ThirdPartyClientHomeSection) string {
	switch strings.ToLower(strings.TrimSpace(section.Module)) {
	case "history":
		return "mixed"
	case "douban_movie":
		return "movie"
	case "douban_tv", "bangumi_anime", "douban_variety":
		return "tv"
	case "site_data":
		if strings.EqualFold(strings.TrimSpace(section.MediaType), "movie") {
			return "movie"
		}
		if strings.EqualFold(strings.TrimSpace(section.MediaType), "tv") {
			return "tv"
		}
	}
	return ""
}

func IsMediaLibrarySection(section db.ThirdPartyClientHomeSection) bool {
	switch strings.ToLower(strings.TrimSpace(section.Module)) {
	case "douban_movie", "douban_tv", "bangumi_anime", "douban_variety", "history", "site_data":
	default:
		return false
	}
	switch strings.ToLower(strings.TrimSpace(section.MediaType)) {
	case "movie", "tv", "mixed":
		return true
	default:
		return strings.EqualFold(strings.TrimSpace(section.Module), "history")
	}
}

func SectionCollectionType(section db.ThirdPartyClientHomeSection) (string, bool) {
	if strings.EqualFold(strings.TrimSpace(section.Module), "history") {
		return "", false
	}
	switch strings.ToLower(strings.TrimSpace(section.MediaType)) {
	case "movie":
		return "movies", true
	case "tv":
		return "tvshows", true
	default:
		return "", false
	}
}

func BuildCollectionFolderItem(serverID string, section db.ThirdPartyClientHomeSection) CollectionFolderItemDTO {
	id := strings.TrimSpace(section.ID)
	name := strings.TrimSpace(section.Name)
	collectionType, hasCollectionType := SectionCollectionType(section)
	state := CollectionFolderState()
	item := CollectionFolderItemDTO{
		Name:                  name,
		ServerID:              serverID,
		ID:                    id,
		GUID:                  StableMD5Hex(id + "|" + name + "|collectionfolder"),
		Etag:                  ProtocolEtag(),
		DateCreated:           EmbyZeroTimeString(),
		DateModified:          EmbyZeroTimeString(),
		CanDelete:             state.CanDelete,
		CanDownload:           state.CanDownload,
		SupportsSync:          state.SupportsSync,
		PresentationUniqueKey: ProtocolPresentationUniqueKey(),
		SortName:              name,
		ForcedSortName:        name,
		ExternalURLs:          EmptyAnyExternalURLs(),
		Taglines:              EmptyStrings(),
		RemoteTrailers:        EmptyRemoteTrailers(),
		ProviderIDs:           EmptyStringMap(),
		IsFolder:              state.IsFolder,
		ParentID:              "2",
		Type:                  state.Type,
		UserData: CollectionUserData{
			PlaybackPositionTicks: 0,
			IsFavorite:            false,
			Played:                false,
		},
		ChildCount:              1,
		DisplayPreferencesID:    ProtocolDisplayPreferencesID(),
		PrimaryImageAspectRatio: 1.7777777777777777,
		ImageTags:               ImageTagsDTO{Primary: StableMD5Hex(id + "|primary")},
		BackdropImageTags:       EmptyStrings(),
		LockedFields:            EmptyLockedFields(),
		LockData:                false,
	}
	if hasCollectionType {
		item.CollectionType = collectionType
	}
	return item
}

func ResolvePrimarySectionID(database *db.DB, wantModule string, wantKind string) string {
	sections, err := ListSections(database)
	if err != nil {
		return ""
	}
	return resolvePrimarySectionIDFromSections(sections, wantModule, wantKind)
}

func ResolveMovieParentSectionID(database *db.DB) string {
	return ResolvePrimarySectionID(database, "douban_movie", "movie")
}

func ResolveSeriesParentSectionID(database *db.DB, genres []string) string {
	if isAnimeGenres(genres) {
		if id := ResolvePrimarySectionID(database, "bangumi_anime", "tv"); id != "" {
			return id
		}
	}
	return ResolvePrimarySectionID(database, "douban_tv", "tv")
}

func resolvePrimarySectionIDFromSections(sections []db.ThirdPartyClientHomeSection, wantModule string, wantKind string) string {
	for i := range sections {
		if strings.EqualFold(strings.TrimSpace(sections[i].Module), wantModule) {
			return strings.TrimSpace(sections[i].ID)
		}
	}
	for i := range sections {
		if LatestSectionKind(sections[i]) == wantKind {
			return strings.TrimSpace(sections[i].ID)
		}
	}
	return ""
}

func isAnimeGenres(genres []string) bool {
	for _, genre := range genres {
		switch strings.ToLower(strings.TrimSpace(genre)) {
		case "animation", "anime", "动画":
			return true
		}
	}
	return false
}
