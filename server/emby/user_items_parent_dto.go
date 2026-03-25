package emby

type embyPagedContentResponse struct {
	Items            []any `json:"Items"`
	TotalRecordCount int   `json:"TotalRecordCount"`
}
