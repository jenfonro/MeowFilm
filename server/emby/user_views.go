package emby

import (
	"net/http"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/emby/emby_service"
)

func handleUserViews(w http.ResponseWriter, r *http.Request, database *db.DB, protocolUserID string) {
	ctx, ok := resolveTopologyContext(w, r, database)
	if !ok || ctx == nil {
		return
	}
	if !requireProtocolUserMatch(w, ctx.Current, protocolUserID) {
		return
	}
	items := make([]emby_service.CollectionFolderItemDTO, 0, len(ctx.Sections))
	for _, section := range ctx.Sections {
		items = append(items, emby_service.BuildCollectionFolderItem(ctx.ServerID, section))
	}
	writeJSON(w, http.StatusOK, embyViewsResponseDTO{
		Items:            items,
		TotalRecordCount: len(items),
	})
}
