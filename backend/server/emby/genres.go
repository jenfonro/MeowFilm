package emby

import (
	"net/http"

	"github.com/jenfonro/meowfilm/internal/db"
	embysvc "github.com/jenfonro/meowfilm/server/emby/emby_service"
)

func handleGenres(w http.ResponseWriter, r *http.Request, database *db.DB) {
	current, serverID, ok := resolveCurrentUserAndServerID(w, r, database)
	if !ok {
		return
	}
	if !requireQueryUserMatch(w, r, current) {
		return
	}

	seeds := embysvc.ResolveGenreSeeds(r.URL.Query().Get("IncludeItemTypes"))
	items := make([]embyGenreItemDTO, 0, len(seeds))
	for _, seed := range seeds {
		items = append(items, embyGenreItemDTO{
			Name:     seed.Name,
			ServerID: serverID,
			ID:       seed.ID,
			Type:     "Genre",
			UserData: embyGenreUserDataDTO{
				PlaybackPositionTicks: 0,
				PlayCount:             0,
				IsFavorite:            false,
				Played:                false,
			},
		})
	}

	writeJSON(w, http.StatusOK, embyGenresResponse{
		Items:            items,
		TotalRecordCount: len(items),
	})
}
