package emby

type embyGenresResponse struct {
	Items            []embyGenreItemDTO `json:"Items"`
	TotalRecordCount int                `json:"TotalRecordCount"`
}

type embyGenreItemDTO struct {
	Name     string                 `json:"Name"`
	ServerID string                 `json:"ServerId"`
	ID       string                 `json:"Id"`
	Type     string                 `json:"Type"`
	UserData embyGenreUserDataDTO   `json:"UserData"`
}

type embyGenreUserDataDTO struct {
	PlaybackPositionTicks int64 `json:"PlaybackPositionTicks"`
	PlayCount             int   `json:"PlayCount"`
	IsFavorite            bool  `json:"IsFavorite"`
	Played                bool  `json:"Played"`
}
