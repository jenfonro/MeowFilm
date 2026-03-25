package emby

type embyLatestImageTagsDTO struct {
	Primary string `json:"Primary"`
	Logo    string `json:"Logo,omitempty"`
}

type embyMovieLatestItemDTO struct {
	Name                    string                     `json:"Name"`
	ServerID                string                     `json:"ServerId"`
	ID                      string                     `json:"Id"`
	CanDelete               bool                       `json:"CanDelete"`
	SupportsSync            bool                       `json:"SupportsSync"`
	PremiereDate            string                     `json:"PremiereDate"`
	CommunityRating         float64                    `json:"CommunityRating"`
	RunTimeTicks            int64                      `json:"RunTimeTicks"`
	ProductionYear          int                        `json:"ProductionYear"`
	IsFolder                bool                       `json:"IsFolder"`
	Type                    string                     `json:"Type"`
	UserData                embyMovieLatestUserDataDTO `json:"UserData"`
	PrimaryImageAspectRatio float64                    `json:"PrimaryImageAspectRatio"`
	ImageTags               embyLatestImageTagsDTO     `json:"ImageTags"`
	BackdropImageTags       []string                   `json:"BackdropImageTags"`
	MediaType               string                     `json:"MediaType"`
}

type embyMovieLatestUserDataDTO struct {
	PlaybackPositionTicks int64    `json:"PlaybackPositionTicks"`
	PlayCount             int      `json:"PlayCount"`
	IsFavorite            bool     `json:"IsFavorite"`
	Played                bool     `json:"Played"`
	PlayedPercentage      *float64 `json:"PlayedPercentage,omitempty"`
}

type embyTVLatestItemDTO struct {
	Name                    string                  `json:"Name"`
	ServerID                string                  `json:"ServerId"`
	ID                      string                  `json:"Id"`
	CanDelete               bool                    `json:"CanDelete"`
	SupportsSync            bool                    `json:"SupportsSync"`
	PremiereDate            string                  `json:"PremiereDate"`
	RunTimeTicks            int64                   `json:"RunTimeTicks"`
	ProductionYear          int                     `json:"ProductionYear"`
	IsFolder                bool                    `json:"IsFolder"`
	Type                    string                  `json:"Type"`
	UserData                embyTVLatestUserDataDTO `json:"UserData"`
	ChildCount              int                     `json:"ChildCount"`
	Status                  string                  `json:"Status"`
	AirDays                 []string                `json:"AirDays"`
	PrimaryImageAspectRatio float64                 `json:"PrimaryImageAspectRatio"`
	ImageTags               embyLatestImageTagsDTO  `json:"ImageTags"`
	BackdropImageTags       []string                `json:"BackdropImageTags"`
}

type embyTVLatestUserDataDTO struct {
	UnplayedItemCount     int   `json:"UnplayedItemCount"`
	PlaybackPositionTicks int64 `json:"PlaybackPositionTicks"`
	PlayCount             int   `json:"PlayCount"`
	IsFavorite            bool  `json:"IsFavorite"`
	Played                bool  `json:"Played"`
}
