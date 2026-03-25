package emby

import (
	"net/http"
	"strconv"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/clientmeta"
	"github.com/jenfonro/meowfilm/server/emby/emby_service"
)

func handleUserItems(w http.ResponseWriter, r *http.Request, database *db.DB, protocolUserID string) {
	current, serverID, ok := resolveCurrentUserAndServerID(w, r, database)
	if !ok {
		return
	}
	if !requireProtocolUserMatch(w, current, protocolUserID) {
		return
	}
	switch ResolveItemQueryShape(r) {
	case ItemQueryShapeParentList:
		parentID := strings.TrimSpace(r.URL.Query().Get("ParentId"))
		startIndex := queryIntDefault(r, "StartIndex", 0)
		if startIndex < 0 {
			startIndex = 0
		}
		limit := queryIntDefault(r, "Limit", 12)
		if limit <= 0 {
			limit = 12
		}
		items, total, ok, err := emby_service.BuildSectionItemsPayload(
			database,
			current.Row.ID,
			serverID,
			parentID,
			strings.TrimSpace(r.URL.Query().Get("IncludeItemTypes")),
			startIndex,
			limit,
		)
		if err != nil {
			writeEmbyError(w, http.StatusInternalServerError, "请求失败")
			return
		}
		if !ok {
			writeJSON(w, http.StatusOK, embyPagedContentResponse{
				Items:            []any{},
				TotalRecordCount: 0,
			})
			return
		}
		writeJSON(w, http.StatusOK, embyPagedContentResponse{
			Items:            items,
			TotalRecordCount: total,
		})
		return
	case ItemQueryShapeSearch:
		searchTerm := strings.TrimSpace(r.URL.Query().Get("SearchTerm"))
		if playing, ok := emby_service.GetPlaying(current.Row.ID, strings.TrimSpace(clientmeta.ClientDeviceID(r))); ok {
			if emby_service.IsDerivedSearchTerm(playing, searchTerm) {
				writeJSON(w, http.StatusOK, embySearchItemsResponse{
					Items:            []any{},
					TotalRecordCount: 0,
				})
				return
			}
		}
		startIndex := queryIntDefault(r, "StartIndex", 0)
		if startIndex < 0 {
			startIndex = 0
		}
		limit := queryIntDefault(r, "Limit", 24)
		if limit <= 0 {
			limit = 24
		}
		if limit > 60 {
			limit = 60
		}
		items, total, ok, err := emby_service.BuildSearchItemsPayload(
			database,
			current.Row.ID,
			serverID,
			strings.TrimSpace(r.URL.Query().Get("IncludeItemTypes")),
			searchTerm,
			startIndex,
			limit,
			ResolveSearchQueryShape(r),
		)
		if err != nil {
			writeEmbyError(w, http.StatusInternalServerError, "请求失败")
			return
		}
		if !ok {
			writeJSON(w, http.StatusOK, embySearchItemsResponse{
				Items:            []any{},
				TotalRecordCount: 0,
			})
			return
		}
		writeJSON(w, http.StatusOK, embySearchItemsResponse{
			Items:            items,
			TotalRecordCount: total,
		})
		return
	}
	all, err := listEmbyCollectionFolderItems(database, serverID)
	if err != nil {
		writeEmbyError(w, http.StatusInternalServerError, "请求失败")
		return
	}

	startIndex := queryIntDefault(r, "StartIndex", 0)
	if startIndex < 0 {
		startIndex = 0
	}
	limit := queryIntDefault(r, "Limit", len(all))
	if limit <= 0 {
		limit = len(all)
	}
	total := len(all)
	page := []emby_service.CollectionFolderItemDTO{}
	if startIndex < total {
		end := startIndex + limit
		if end > total {
			end = total
		}
		page = all[startIndex:end]
	}

	writeJSON(w, http.StatusOK, embyItemsResponseDTO{
		Items:            page,
		TotalRecordCount: total,
	})
}

func queryIntDefault(r *http.Request, key string, def int) int {
	if r == nil {
		return def
	}
	raw := strings.TrimSpace(r.URL.Query().Get(key))
	if raw == "" {
		return def
	}
	n, err := strconv.Atoi(raw)
	if err != nil {
		return def
	}
	return n
}
