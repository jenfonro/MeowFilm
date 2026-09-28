package emby

import (
	"net/http"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
	embysvc "github.com/jenfonro/meowfilm/server/emby/emby_service"
)

func handleUserItemsLatest(w http.ResponseWriter, r *http.Request, database *db.DB, protocolUserID string) {
	current, serverID, ok := resolveCurrentUserAndServerID(w, r, database)
	if !ok {
		return
	}
	if !requireProtocolUserMatch(w, current, protocolUserID) {
		return
	}

	parentID := strings.TrimSpace(r.URL.Query().Get("ParentId"))
	if parentID == "" {
		writeJSON(w, http.StatusOK, []any{})
		return
	}
	section, ok, err := embysvc.ResolveSectionByID(database, parentID)
	if err != nil {
		writeEmbyError(w, http.StatusInternalServerError, "请求失败")
		return
	}
	if !ok {
		writeJSON(w, http.StatusOK, []any{})
		return
	}
	limit := queryIntDefault(r, "Limit", 15)
	if limit <= 0 {
		limit = 15
	}

	payload, err := embysvc.BuildLatestPayload(database, current.Row.ID, serverID, section, limit)
	if err != nil {
		writeEmbyError(w, http.StatusInternalServerError, "请求失败")
		return
	}
	switch ResolveLatestQueryShape(r) {
	case LatestQueryShapeThin:
		writeJSON(w, http.StatusOK, thinLatestPayload(payload))
	default:
		writeJSON(w, http.StatusOK, payload)
	}
}

func thinLatestPayload(payload any) []any {
	switch v := payload.(type) {
	case []embysvc.MovieLatestItemDTO:
		out := make([]any, 0, len(v))
		for _, item := range v {
			out = append(out, thinMovieLatestItem(item))
		}
		return out
	case []embysvc.TVLatestItemDTO:
		out := make([]any, 0, len(v))
		for _, item := range v {
			out = append(out, thinTVLatestItem(item))
		}
		return out
	case []any:
		out := make([]any, 0, len(v))
		for _, item := range v {
			switch row := item.(type) {
			case embysvc.MovieLatestItemDTO:
				out = append(out, thinMovieLatestItem(row))
			case embysvc.TVLatestItemDTO:
				out = append(out, thinTVLatestItem(row))
			}
		}
		return out
	default:
		return []any{}
	}
}

func thinMovieLatestItem(row embysvc.MovieLatestItemDTO) embyMovieLatestItemDTO {
	return embyMovieLatestItemDTO{
		Name:                    row.Name,
		ServerID:                row.ServerID,
		ID:                      row.ID,
		CanDelete:               true,
		SupportsSync:            row.SupportsSync,
		PremiereDate:            row.PremiereDate,
		CommunityRating:         row.CommunityRating,
		RunTimeTicks:            row.RunTimeTicks,
		ProductionYear:          row.ProductionYear,
		IsFolder:                row.IsFolder,
		Type:                    row.Type,
		UserData:                embyMovieLatestUserDataDTO(row.UserData),
		PrimaryImageAspectRatio: row.PrimaryImageAspectRatio,
		ImageTags:               embyLatestImageTagsDTO(row.ImageTags),
		BackdropImageTags:       row.BackdropImageTags,
		MediaType:               row.MediaType,
	}
}

func thinTVLatestItem(row embysvc.TVLatestItemDTO) embyTVLatestItemDTO {
	return embyTVLatestItemDTO{
		Name:                    row.Name,
		ServerID:                row.ServerID,
		ID:                      row.ID,
		CanDelete:               true,
		SupportsSync:            row.SupportsSync,
		PremiereDate:            row.PremiereDate,
		RunTimeTicks:            row.RunTimeTicks,
		ProductionYear:          row.ProductionYear,
		IsFolder:                row.IsFolder,
		Type:                    row.Type,
		UserData:                embyTVLatestUserDataDTO(row.UserData),
		ChildCount:              row.ChildCount,
		Status:                  row.Status,
		AirDays:                 row.AirDays,
		PrimaryImageAspectRatio: row.PrimaryImageAspectRatio,
		ImageTags:               embyLatestImageTagsDTO(row.ImageTags),
		BackdropImageTags:       row.BackdropImageTags,
	}
}
