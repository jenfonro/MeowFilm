package emby

import (
	"net/http"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
	embysvc "github.com/jenfonro/meowfilm/server/emby/emby_service"
)

func handleItemsCounts(w http.ResponseWriter, r *http.Request, database *db.DB) {
	current, serverID, ok := resolveCurrentUserAndServerID(w, r, database)
	if !ok {
		return
	}

	sections, err := embysvc.ListSections(database)
	if err != nil {
		writeEmbyError(w, http.StatusInternalServerError, "请求失败")
		return
	}

	movieSection := findPrimarySection(sections, "douban_movie", "movie")
	tvSection := findPrimarySection(sections, "douban_tv", "tv")

	movieCount := 0
	seriesCount := 0
	episodeCount := 0
	if movieSection != nil {
		payload, err := embysvc.BuildLatestPayload(database, current.Row.ID, serverID, *movieSection, 200)
		if err != nil {
			writeEmbyError(w, http.StatusInternalServerError, "请求失败")
			return
		}
		if rows, ok := payload.([]embysvc.MovieLatestItemDTO); ok {
			movieCount = len(rows)
		}
	}
	if tvSection != nil {
		payload, err := embysvc.BuildLatestPayload(database, current.Row.ID, serverID, *tvSection, 200)
		if err != nil {
			writeEmbyError(w, http.StatusInternalServerError, "请求失败")
			return
		}
		if rows, ok := payload.([]embysvc.TVLatestItemDTO); ok {
			seriesCount = len(rows)
			for _, row := range rows {
				if row.UserData.UnplayedItemCount > 0 {
					episodeCount += row.UserData.UnplayedItemCount
				}
			}
		}
	}

	writeJSON(w, http.StatusOK, embyItemsCountsDTO{
		MovieCount:      movieCount,
		SeriesCount:     seriesCount,
		EpisodeCount:    episodeCount,
		GameCount:       0,
		ArtistCount:     0,
		ProgramCount:    0,
		GameSystemCount: 0,
		TrailerCount:    0,
		SongCount:       0,
		AlbumCount:      0,
		MusicVideoCount: 0,
		BoxSetCount:     0,
		BookCount:       0,
		ItemCount:       0,
	})
}

func findPrimarySection(sections []db.ThirdPartyClientHomeSection, wantModule string, wantKind string) *db.ThirdPartyClientHomeSection {
	for i := range sections {
		if strings.EqualFold(strings.TrimSpace(sections[i].Module), wantModule) {
			return &sections[i]
		}
	}
	for i := range sections {
		if embysvc.LatestSectionKind(sections[i]) == wantKind {
			return &sections[i]
		}
	}
	return nil
}
