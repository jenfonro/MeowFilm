package emby_service

import (
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
)

func BuildItemDetailPayload(database *db.DB, userID int64, serverID string, itemID string) (any, bool, error) {
	section, ok, err := ResolveSectionByID(database, itemID)
	if err != nil {
		return nil, false, err
	}
	if ok {
		return BuildCollectionFolderItem(serverID, section), true, nil
	}
	ref := parseItemRef(itemID)
	if ref == nil || database == nil {
		return nil, false, nil
	}
	if payload, ok := resolveLatestSectionItemDetailPayload(database, userID, serverID, ref.RawID); ok {
		return payload, true, nil
	}
	if payload, ok, err := BuildUserItemDetailPayload(database, userID, serverID, ref.RawID); err != nil {
		return nil, false, err
	} else if ok {
		return payload, true, nil
	}
	historyRows, err := BuildHistoryLatestPayload(database, userID, serverID, 100)
	if err != nil {
		return nil, false, err
	}
	for _, row := range historyRows {
		if itemMatchesID(row, ref.RawID) {
			return row, true, nil
		}
	}
	return nil, false, nil
}

func resolveLatestSectionItemDetailPayload(database *db.DB, userID int64, serverID string, itemID string) (any, bool) {
	sections, err := ListSections(database)
	if err != nil {
		return nil, false
	}
	const scanLimit = 100
	for _, section := range sections {
		payload, err := BuildLatestPayload(database, userID, serverID, section, scanLimit)
		if err != nil {
			continue
		}
		switch rows := payload.(type) {
		case []MovieLatestItemDTO:
			for _, row := range rows {
				if strings.TrimSpace(row.ID) == itemID {
					return row, true
				}
			}
		case []TVLatestItemDTO:
			for _, row := range rows {
				if strings.TrimSpace(row.ID) == itemID {
					return row, true
				}
			}
		}
	}
	return nil, false
}

func itemMatchesID(payload any, itemID string) bool {
	switch v := payload.(type) {
	case MovieLatestItemDTO:
		return strings.TrimSpace(v.ID) == strings.TrimSpace(itemID)
	case TVLatestItemDTO:
		return strings.TrimSpace(v.ID) == strings.TrimSpace(itemID)
	case ResumeMovieItemDTO:
		return strings.TrimSpace(v.ID) == strings.TrimSpace(itemID)
	case ResumeEpisodeItemDTO:
		return strings.TrimSpace(v.ID) == strings.TrimSpace(itemID)
	default:
		return false
	}
}
