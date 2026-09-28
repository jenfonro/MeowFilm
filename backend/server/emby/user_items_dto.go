package emby

import "github.com/jenfonro/meowfilm/server/emby/emby_service"

type embyItemsResponseDTO struct {
	Items            []emby_service.CollectionFolderItemDTO `json:"Items"`
	TotalRecordCount int                                    `json:"TotalRecordCount"`
}
