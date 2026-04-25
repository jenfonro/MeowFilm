package emby_service

import (
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
)

// BuildParentItemsPayload is kept as a compatibility alias for section item lists.
// New call sites should prefer BuildSectionItemsPayload to make the family boundary explicit.
func BuildParentItemsPayload(database *db.DB, userID int64, serverID string, parentID string, includeItemTypes string, startIndex int, limit int) ([]any, int, bool, error) {
	return BuildSectionItemsPayload(database, userID, serverID, parentID, includeItemTypes, startIndex, limit)
}

// BuildSectionItemsPayload renders the section item list family behind
// /Users/{id}/Items?ParentId=... and must not be reused for latest/search DTOs.
func BuildSectionItemsPayload(database *db.DB, userID int64, serverID string, parentID string, includeItemTypes string, startIndex int, limit int) ([]any, int, bool, error) {
	section, ok, err := ResolveSectionByID(database, parentID)
	if err != nil || !ok {
		return EmptyAnySlice(), 0, ok, err
	}
	kind := sectionContentKind(section)
	if kind == "" {
		return EmptyAnySlice(), 0, false, nil
	}
	include := includeItemTypeSet(includeItemTypes)
	if len(include) > 0 {
		if kind == "movie" && !allowsMovieItems(include) {
			return EmptyAnySlice(), 0, false, nil
		}
		if kind == "tv" && !allowsSeriesItems(include) {
			return EmptyAnySlice(), 0, false, nil
		}
	}
	switch kind {
	case "movie":
		rows, err := loadMovieLatestRange(database, section, startIndex, limit)
		if err != nil {
			return nil, 0, true, err
		}
		items := make([]any, 0, len(rows))
		for _, row := range rows {
			items = append(items, buildSectionMovieItemDTO(serverID, row))
		}
		return items, inferHasMoreTotal(startIndex, limit, len(items)), true, nil
	case "tv":
		rows, err := loadTVLatestRange(database, section, startIndex, limit)
		if err != nil {
			return nil, 0, true, err
		}
		items := make([]any, 0, len(rows))
		for _, row := range rows {
			items = append(items, buildSectionSeriesItemDTO(serverID, row))
		}
		return items, inferHasMoreTotal(startIndex, limit, len(items)), true, nil
	case "mixed":
		items, total, err := buildHistorySectionItems(database, userID, serverID, startIndex, limit)
		if err != nil {
			return nil, 0, true, err
		}
		return items, total, true, nil
	default:
		return EmptyAnySlice(), 0, false, nil
	}
}

func sectionContentKind(section db.ThirdPartyClientHomeSection) string {
	switch LatestSectionKind(section) {
	case "movie":
		return "movie"
	case "tv":
		return "tv"
	case "mixed":
		return "mixed"
	default:
		return ""
	}
}

func includeItemTypeSet(raw string) map[string]bool {
	raw = strings.TrimSpace(strings.ToLower(raw))
	if raw == "" {
		return nil
	}
	out := map[string]bool{}
	for _, part := range strings.Split(raw, ",") {
		part = strings.TrimSpace(part)
		if part == "" {
			continue
		}
		out[part] = true
	}
	return out
}

func allowsMovieItems(include map[string]bool) bool {
	if len(include) == 0 {
		return true
	}
	return include["movie"] || include["video"] || include["musicvideo"]
}

func allowsSeriesItems(include map[string]bool) bool {
	if len(include) == 0 {
		return true
	}
	return include["series"] || include["video"]
}

func buildHistorySectionItems(database *db.DB, userID int64, serverID string, startIndex int, limit int) ([]any, int, error) {
	if database == nil || userID <= 0 {
		return EmptyAnySlice(), 0, nil
	}
	fetch := startIndex + limit
	if fetch < 20 {
		fetch = 20
	}
	rows, err := BuildHistoryLatestPayload(database, userID, serverID, fetch)
	if err != nil {
		return nil, 0, err
	}
	items := make([]any, 0, len(rows))
	for _, row := range rows {
		switch v := row.(type) {
		case MovieLatestItemDTO:
			items = append(items, sectionMovieItemFromLatest(v))
		case TVLatestItemDTO:
			items = append(items, sectionSeriesItemFromLatest(v))
		}
	}
	if startIndex >= len(items) {
		return EmptyAnySlice(), len(items), nil
	}
	end := startIndex + limit
	if end > len(items) {
		end = len(items)
	}
	page := items[startIndex:end]
	return page, inferHasMoreTotal(startIndex, limit, len(page)), nil
}

func buildSectionMovieItemDTO(serverID string, row movieLatestSource) SectionMovieItemDTO {
	return sectionMovieItemFromLatest(buildMovieLatestItemDTO(serverID, row))
}

func buildSectionSeriesItemDTO(serverID string, row tvLatestSource) SectionSeriesItemDTO {
	row = NormalizeTVLatestSource(row)
	state := SeriesItemState(false, false)
	return SectionSeriesItemDTO{
		Name:                    row.Name,
		ServerID:                strings.TrimSpace(serverID),
		ID:                      strings.TrimSpace(row.ID),
		Etag:                    StableItemEtag(row.ID),
		DateCreated:             row.DateCreated,
		SortName:                SortNameOrName(row.SortName),
		CanDownload:             state.CanDownload,
		SupportsSync:            state.SupportsSync,
		PremiereDate:            strings.TrimSpace(row.PremiereDate),
		EndDate:                 "",
		Overview:                strings.TrimSpace(row.Overview),
		CommunityRating:         row.CommunityRating,
		RunTimeTicks:            row.RunTimeTicks,
		ProductionYear:          row.ProductionYear,
		ProviderIDs:             row.ProviderIDs,
		IsFolder:                state.IsFolder,
		Type:                    state.Type,
		UserData:                TVLatestUserDataDTO{UnplayedItemCount: row.UnplayedCount, PlaybackPositionTicks: 0, PlayCount: 0, IsFavorite: false, Played: false},
		RecursiveItemCount:      row.RecursiveCount,
		ChildCount:              row.ChildCount,
		Status:                  strings.TrimSpace(row.Status),
		AirDays:                 EmptyStrings(),
		PrimaryImageAspectRatio: NormalizeAspectRatio(row.AspectRatio),
		ImageTags:               ImageTagsDTO{Primary: row.PrimaryTag, Logo: row.LogoTag},
		BackdropImageTags:       BackdropTagsOrEmpty(defaultBackdropTags(row.BackdropTags)),
	}
}

func sectionMovieItemFromLatest(row MovieLatestItemDTO) SectionMovieItemDTO {
	state := MovieItemState(false, false)
	return SectionMovieItemDTO{
		Name:                    row.Name,
		ServerID:                row.ServerID,
		ID:                      row.ID,
		Etag:                    row.Etag,
		DateCreated:             row.DateCreated,
		Container:               row.Container,
		SortName:                row.SortName,
		CanDownload:             state.CanDownload,
		SupportsSync:            row.SupportsSync,
		PremiereDate:            row.PremiereDate,
		EndDate:                 "",
		Overview:                row.Overview,
		CommunityRating:         row.CommunityRating,
		RunTimeTicks:            row.RunTimeTicks,
		ProductionYear:          row.ProductionYear,
		ProviderIDs:             row.ProviderIDs,
		IsFolder:                row.IsFolder,
		Type:                    row.Type,
		UserData:                row.UserData,
		PrimaryImageAspectRatio: row.PrimaryImageAspectRatio,
		ImageTags:               row.ImageTags,
		BackdropImageTags:       row.BackdropImageTags,
		MediaType:               row.MediaType,
	}
}

func sectionSeriesItemFromLatest(row TVLatestItemDTO) SectionSeriesItemDTO {
	state := SeriesItemState(false, false)
	return SectionSeriesItemDTO{
		Name:                    row.Name,
		ServerID:                row.ServerID,
		ID:                      row.ID,
		Etag:                    row.Etag,
		DateCreated:             row.DateCreated,
		SortName:                row.SortName,
		CanDownload:             state.CanDownload,
		SupportsSync:            row.SupportsSync,
		PremiereDate:            row.PremiereDate,
		EndDate:                 "",
		Overview:                row.Overview,
		CommunityRating:         0,
		RunTimeTicks:            row.RunTimeTicks,
		ProductionYear:          row.ProductionYear,
		ProviderIDs:             row.ProviderIDs,
		IsFolder:                row.IsFolder,
		Type:                    row.Type,
		UserData:                row.UserData,
		RecursiveItemCount:      row.RecursiveItemCount,
		ChildCount:              row.ChildCount,
		Status:                  row.Status,
		AirDays:                 row.AirDays,
		PrimaryImageAspectRatio: row.PrimaryImageAspectRatio,
		ImageTags:               row.ImageTags,
		BackdropImageTags:       row.BackdropImageTags,
	}
}

func inferHasMoreTotal(startIndex int, limit int, got int) int {
	if got <= 0 {
		return 0
	}
	total := startIndex + got
	if got == limit && limit > 0 {
		total++
	}
	return total
}
