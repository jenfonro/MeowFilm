package emby

type embyResumeResponse struct {
	Items            []any `json:"Items"`
	TotalRecordCount int   `json:"TotalRecordCount"`
}

type embyHideFromResumeRequest struct {
	Hide bool `json:"Hide"`
}

type embyHideFromResumeResponse struct {
	PlayedPercentage      float64 `json:"PlayedPercentage"`
	PlaybackPositionTicks int64   `json:"PlaybackPositionTicks"`
	PlayCount             int     `json:"PlayCount"`
	IsFavorite            bool    `json:"IsFavorite"`
	LastPlayedDate        string  `json:"LastPlayedDate"`
	Played                bool    `json:"Played"`
}

type embyResumeEpisodeItemDTO struct {
	Name                    string                         `json:"Name"`
	ServerID                string                         `json:"ServerId"`
	ID                      string                         `json:"Id"`
	Container               string                         `json:"Container"`
	MediaSources            []embyResumeMediaSourceDTO     `json:"MediaSources"`
	RunTimeTicks            int64                          `json:"RunTimeTicks"`
	Size                    int64                          `json:"Size"`
	Bitrate                 int                            `json:"Bitrate"`
	IndexNumber             int                            `json:"IndexNumber"`
	ParentIndexNumber       int                            `json:"ParentIndexNumber"`
	ProviderIDs             map[string]any                 `json:"ProviderIds"`
	IsFolder                bool                           `json:"IsFolder"`
	Type                    string                         `json:"Type"`
	ParentLogoItemID        string                         `json:"ParentLogoItemId,omitempty"`
	ParentBackdropItemID    string                         `json:"ParentBackdropItemId,omitempty"`
	ParentBackdropImageTags []string                       `json:"ParentBackdropImageTags"`
	UserData                embyResumeEpisodeUserDataDTO   `json:"UserData"`
	SeriesName              string                         `json:"SeriesName"`
	SeriesID                string                         `json:"SeriesId"`
	SeasonID                string                         `json:"SeasonId"`
	SeriesPrimaryImageTag   string                         `json:"SeriesPrimaryImageTag,omitempty"`
	SeasonName              string                         `json:"SeasonName"`
	ImageTags               embyLatestImageTagsDTO         `json:"ImageTags"`
	BackdropImageTags       []string                       `json:"BackdropImageTags"`
	ParentLogoImageTag      string                         `json:"ParentLogoImageTag,omitempty"`
	MediaType               string                         `json:"MediaType"`
}

type embyResumeEpisodeUserDataDTO struct {
	PlaybackPositionTicks int64    `json:"PlaybackPositionTicks"`
	PlayCount             int      `json:"PlayCount"`
	IsFavorite            bool     `json:"IsFavorite"`
	Played                bool     `json:"Played"`
	PlayedPercentage      *float64 `json:"PlayedPercentage,omitempty"`
}

type embyResumeMovieItemDTO struct {
	Name              string                     `json:"Name"`
	ServerID          string                     `json:"ServerId"`
	ID                string                     `json:"Id"`
	Container         string                     `json:"Container"`
	MediaSources      []embyResumeMediaSourceDTO `json:"MediaSources"`
	RunTimeTicks      int64                      `json:"RunTimeTicks"`
	Size              int64                      `json:"Size"`
	Bitrate           int                        `json:"Bitrate"`
	ProviderIDs       map[string]any             `json:"ProviderIds"`
	IsFolder          bool                       `json:"IsFolder"`
	Type              string                     `json:"Type"`
	UserData          embyMovieLatestUserDataDTO `json:"UserData"`
	ImageTags         embyLatestImageTagsDTO     `json:"ImageTags"`
	BackdropImageTags []string                   `json:"BackdropImageTags"`
	MediaType         string                     `json:"MediaType"`
}

type embyResumeMediaSourceDTO struct {
	Chapters             []any             `json:"Chapters"`
	Protocol             string            `json:"Protocol"`
	ID                   string            `json:"Id"`
	Path                 string            `json:"Path"`
	Type                 string            `json:"Type"`
	Container            string            `json:"Container"`
	Size                 int64             `json:"Size"`
	Name                 string            `json:"Name"`
	IsRemote             bool              `json:"IsRemote"`
	HasMixedProtocols    bool              `json:"HasMixedProtocols"`
	RunTimeTicks         int64             `json:"RunTimeTicks"`
	SupportsTranscoding  bool              `json:"SupportsTranscoding"`
	SupportsDirectStream bool              `json:"SupportsDirectStream"`
	SupportsDirectPlay   bool              `json:"SupportsDirectPlay"`
	IsInfiniteStream     bool              `json:"IsInfiniteStream"`
	RequiresOpening      bool              `json:"RequiresOpening"`
	RequiresClosing      bool              `json:"RequiresClosing"`
	RequiresLooping      bool              `json:"RequiresLooping"`
	SupportsProbing      bool              `json:"SupportsProbing"`
	MediaStreams         []any             `json:"MediaStreams"`
	Formats              []any             `json:"Formats"`
	Bitrate              int               `json:"Bitrate"`
	RequiredHTTPHeaders  map[string]string `json:"RequiredHttpHeaders"`
	AddAPIKeyToDirect    bool              `json:"AddApiKeyToDirectStreamUrl"`
	ReadAtNativeFrame    bool              `json:"ReadAtNativeFramerate"`
	DefaultAudioStreamID int               `json:"DefaultAudioStreamIndex"`
	ItemID               string            `json:"ItemId"`
	MediaSourceID        string            `json:"MediaSourceId"`
}
