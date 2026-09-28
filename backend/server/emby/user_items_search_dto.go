package emby

type embySearchItemsResponse struct {
	Items            []any `json:"Items"`
	TotalRecordCount int   `json:"TotalRecordCount"`
}
