package emby_service

type ExternalURLDTO struct {
	Name string `json:"Name"`
	URL  string `json:"Url"`
}

type PersonDTO struct {
	Name            string `json:"Name"`
	ID              string `json:"Id"`
	Role            string `json:"Role,omitempty"`
	Type            string `json:"Type"`
	PrimaryImageTag string `json:"PrimaryImageTag,omitempty"`
}

type NamedIDDTO struct {
	Name string `json:"Name"`
	ID   any    `json:"Id"`
}

type DetailChapterDTO struct {
	StartPositionTicks int64  `json:"StartPositionTicks"`
	Name               string `json:"Name"`
	MarkerType         string `json:"MarkerType"`
	ChapterIndex       int    `json:"ChapterIndex"`
}

type DetailMediaSourceDTO struct {
	Chapters                   []DetailChapterDTO  `json:"Chapters"`
	Protocol                   string              `json:"Protocol"`
	ID                         string              `json:"Id"`
	Path                       string              `json:"Path"`
	Type                       string              `json:"Type"`
	Container                  string              `json:"Container"`
	Size                       int64               `json:"Size"`
	Name                       string              `json:"Name"`
	IsRemote                   bool                `json:"IsRemote"`
	HasMixedProtocols          bool                `json:"HasMixedProtocols"`
	RunTimeTicks               int64               `json:"RunTimeTicks"`
	SupportsTranscoding        bool                `json:"SupportsTranscoding"`
	SupportsDirectStream       bool                `json:"SupportsDirectStream"`
	SupportsDirectPlay         bool                `json:"SupportsDirectPlay"`
	IsInfiniteStream           bool                `json:"IsInfiniteStream"`
	RequiresOpening            bool                `json:"RequiresOpening"`
	RequiresClosing            bool                `json:"RequiresClosing"`
	RequiresLooping            bool                `json:"RequiresLooping"`
	SupportsProbing            bool                `json:"SupportsProbing"`
	MediaStreams               []any               `json:"MediaStreams"`
	Formats                    []any               `json:"Formats"`
	Bitrate                    int                 `json:"Bitrate"`
	RequiredHTTPHeaders        map[string]string   `json:"RequiredHttpHeaders"`
	AddAPIKeyToDirectStreamURL bool                `json:"AddApiKeyToDirectStreamUrl"`
	ReadAtNativeFramerate      bool                `json:"ReadAtNativeFramerate"`
	DefaultAudioStreamIndex    int                 `json:"DefaultAudioStreamIndex"`
	ItemID                     string              `json:"ItemId"`
}

type MovieDetailUserDataDTO struct {
	PlayedPercentage      *float64 `json:"PlayedPercentage,omitempty"`
	PlaybackPositionTicks int64    `json:"PlaybackPositionTicks"`
	PlayCount             int      `json:"PlayCount"`
	IsFavorite            bool     `json:"IsFavorite"`
	LastPlayedDate        string   `json:"LastPlayedDate"`
	Played                bool     `json:"Played"`
}

type SimpleUserDataDTO struct {
	PlaybackPositionTicks int64 `json:"PlaybackPositionTicks"`
	PlayCount             int   `json:"PlayCount"`
	IsFavorite            bool  `json:"IsFavorite"`
	Played                bool  `json:"Played"`
}

type MovieDetailItemDTO struct {
	Name                    string                 `json:"Name"`
	OriginalTitle           string                 `json:"OriginalTitle"`
	ServerID                string                 `json:"ServerId"`
	ID                      string                 `json:"Id"`
	Etag                    string                 `json:"Etag"`
	DateCreated             string                 `json:"DateCreated"`
	DateModified            string                 `json:"DateModified"`
	CanDelete               bool                   `json:"CanDelete"`
	CanDownload             bool                   `json:"CanDownload"`
	PresentationUniqueKey   string                 `json:"PresentationUniqueKey"`
	SupportsSync            bool                   `json:"SupportsSync"`
	Container               string                 `json:"Container"`
	SortName                string                 `json:"SortName"`
	ForcedSortName          string                 `json:"ForcedSortName"`
	PremiereDate            string                 `json:"PremiereDate"`
	ExternalURLs            []ExternalURLDTO       `json:"ExternalUrls"`
	MediaSources            []DetailMediaSourceDTO `json:"MediaSources"`
	ProductionLocations     []string               `json:"ProductionLocations"`
	Path                    string                 `json:"Path"`
	Overview                string                 `json:"Overview"`
	Taglines                []string               `json:"Taglines"`
	Genres                  []string               `json:"Genres"`
	RunTimeTicks            int64                  `json:"RunTimeTicks"`
	FileName                string                 `json:"FileName"`
	ProductionYear          int                    `json:"ProductionYear"`
	RemoteTrailers          []any                  `json:"RemoteTrailers"`
	ProviderIDs             map[string]string      `json:"ProviderIds"`
	ParentID                string                 `json:"ParentId"`
	Type                    string                 `json:"Type"`
	People                  []PersonDTO            `json:"People"`
	Studios                 []NamedIDDTO           `json:"Studios"`
	GenreItems              []NamedIDDTO           `json:"GenreItems"`
	TagItems                []NamedIDDTO           `json:"TagItems"`
	LocalTrailerCount       int                    `json:"LocalTrailerCount"`
	UserData                MovieDetailUserDataDTO `json:"UserData"`
	DisplayPreferencesID    string                 `json:"DisplayPreferencesId"`
	PrimaryImageAspectRatio float64                `json:"PrimaryImageAspectRatio"`
	MediaStreams            []any                  `json:"MediaStreams"`
	PartCount               int                    `json:"PartCount"`
	ImageTags               ImageTagsDTO           `json:"ImageTags"`
	BackdropImageTags       []string               `json:"BackdropImageTags"`
	Chapters                []DetailChapterDTO     `json:"Chapters"`
	MediaType               string                 `json:"MediaType"`
	LockedFields            []string               `json:"LockedFields"`
	LockData                bool                   `json:"LockData"`
	Width                   int                    `json:"Width"`
	Height                  int                    `json:"Height"`
}

type SeriesDetailItemDTO struct {
	Name                    string              `json:"Name"`
	ServerID                string              `json:"ServerId"`
	ID                      string              `json:"Id"`
	Etag                    string              `json:"Etag"`
	DateCreated             string              `json:"DateCreated"`
	DateModified            string              `json:"DateModified"`
	CanDelete               bool                `json:"CanDelete"`
	CanDownload             bool                `json:"CanDownload"`
	PresentationUniqueKey   string              `json:"PresentationUniqueKey"`
	SupportsSync            bool                `json:"SupportsSync"`
	SortName                string              `json:"SortName"`
	ForcedSortName          string              `json:"ForcedSortName"`
	PremiereDate            string              `json:"PremiereDate"`
	ExternalURLs            []ExternalURLDTO    `json:"ExternalUrls"`
	Path                    string              `json:"Path"`
	Overview                string              `json:"Overview"`
	Taglines                []string            `json:"Taglines"`
	Genres                  []string            `json:"Genres"`
	RunTimeTicks            int64               `json:"RunTimeTicks"`
	FileName                string              `json:"FileName"`
	ProductionYear          int                 `json:"ProductionYear"`
	RemoteTrailers          []any               `json:"RemoteTrailers"`
	ProviderIDs             map[string]string   `json:"ProviderIds"`
	IsFolder                bool                `json:"IsFolder"`
	ParentID                string              `json:"ParentId"`
	Type                    string              `json:"Type"`
	People                  []PersonDTO         `json:"People"`
	Studios                 []NamedIDDTO        `json:"Studios"`
	GenreItems              []NamedIDDTO        `json:"GenreItems"`
	TagItems                []NamedIDDTO        `json:"TagItems"`
	LocalTrailerCount       int                 `json:"LocalTrailerCount"`
	UserData                TVLatestUserDataDTO `json:"UserData"`
	ChildCount              int                 `json:"ChildCount"`
	DisplayPreferencesID    string              `json:"DisplayPreferencesId"`
	Status                  string              `json:"Status"`
	AirDays                 []string            `json:"AirDays"`
	PrimaryImageAspectRatio float64             `json:"PrimaryImageAspectRatio"`
	DisplayOrder            string              `json:"DisplayOrder"`
	ImageTags               ImageTagsDTO        `json:"ImageTags"`
	BackdropImageTags       []string            `json:"BackdropImageTags"`
	LockedFields            []string            `json:"LockedFields"`
	LockData                bool                `json:"LockData"`
}

type EpisodeDetailItemDTO struct {
	Name                    string                 `json:"Name"`
	ServerID                string                 `json:"ServerId"`
	ID                      string                 `json:"Id"`
	Etag                    string                 `json:"Etag"`
	DateCreated             string                 `json:"DateCreated"`
	DateModified            string                 `json:"DateModified"`
	CanDelete               bool                   `json:"CanDelete"`
	CanDownload             bool                   `json:"CanDownload"`
	PresentationUniqueKey   string                 `json:"PresentationUniqueKey"`
	SupportsSync            bool                   `json:"SupportsSync"`
	Container               string                 `json:"Container"`
	SortName                string                 `json:"SortName"`
	ForcedSortName          string                 `json:"ForcedSortName"`
	PremiereDate            string                 `json:"PremiereDate"`
	ExternalURLs            []ExternalURLDTO       `json:"ExternalUrls"`
	MediaSources            []DetailMediaSourceDTO `json:"MediaSources"`
	AlternateMediaSources   []any                  `json:"AlternateMediaSources"`
	Path                    string                 `json:"Path"`
	Overview                string                 `json:"Overview"`
	Taglines                []string               `json:"Taglines"`
	Genres                  []string               `json:"Genres"`
	CommunityRating         float64                `json:"CommunityRating"`
	OfficialRating          string                 `json:"OfficialRating"`
	RunTimeTicks            int64                  `json:"RunTimeTicks"`
	Size                    int64                  `json:"Size"`
	FileName                string                 `json:"FileName"`
	Bitrate                 int                    `json:"Bitrate"`
	ProductionYear          int                    `json:"ProductionYear"`
	IndexNumber             int                    `json:"IndexNumber"`
	ParentIndexNumber       int                    `json:"ParentIndexNumber"`
	RemoteTrailers          []any                  `json:"RemoteTrailers"`
	ProviderIDs             map[string]any         `json:"ProviderIds"`
	IsFolder                bool                   `json:"IsFolder"`
	ParentID                string                 `json:"ParentId"`
	Type                    string                 `json:"Type"`
	People                  []PersonDTO            `json:"People"`
	Studios                 []NamedIDDTO           `json:"Studios"`
	GenreItems              []NamedIDDTO           `json:"GenreItems"`
	TagItems                []NamedIDDTO           `json:"TagItems"`
	ParentLogoItemID        string                 `json:"ParentLogoItemId"`
	ParentBackdropItemID    string                 `json:"ParentBackdropItemId"`
	ParentBackdropImageTags []string               `json:"ParentBackdropImageTags"`
	LocalTrailerCount       int                    `json:"LocalTrailerCount"`
	UserData                MovieDetailUserDataDTO `json:"UserData"`
	SeriesName              string                 `json:"SeriesName"`
	SeriesID                string                 `json:"SeriesId"`
	SeasonID                string                 `json:"SeasonId"`
	DisplayPreferencesID    string                 `json:"DisplayPreferencesId"`
	PrimaryImageAspectRatio float64                `json:"PrimaryImageAspectRatio"`
	SeriesPrimaryImageTag   string                 `json:"SeriesPrimaryImageTag"`
	SeasonName              string                 `json:"SeasonName"`
	MediaStreams            []any                  `json:"MediaStreams"`
	ImageTags               ImageTagsDTO           `json:"ImageTags"`
	BackdropImageTags       []string               `json:"BackdropImageTags"`
	ParentLogoImageTag      string                 `json:"ParentLogoImageTag"`
	Chapters                []DetailChapterDTO     `json:"Chapters"`
	MediaType               string                 `json:"MediaType"`
	LockedFields            []string               `json:"LockedFields"`
	LockData                bool                   `json:"LockData"`
	Width                   int                    `json:"Width"`
	Height                  int                    `json:"Height"`
}

type SeasonsResponseDTO struct {
	Items            []SeasonListItemDTO `json:"Items"`
	TotalRecordCount int                 `json:"TotalRecordCount"`
}

type SeasonsBasicResponseDTO struct {
	Items            []SeasonBasicItemDTO `json:"Items"`
	TotalRecordCount int                  `json:"TotalRecordCount"`
}

type SeasonsLennaResponseDTO struct {
	Items            []SeasonLennaItemDTO `json:"Items"`
	TotalRecordCount int                  `json:"TotalRecordCount"`
}

type SeasonsFamilyResponseDTO struct {
	Items            []SeasonFamilyItemDTO `json:"Items"`
	TotalRecordCount int                   `json:"TotalRecordCount"`
}

type SeasonsRichResponseDTO = SeasonsLennaResponseDTO

type SeasonListItemDTO struct {
	Name                    string              `json:"Name"`
	ServerID                string              `json:"ServerId"`
	ID                      string              `json:"Id"`
	Genres                  []string            `json:"Genres"`
	IndexNumber             int                 `json:"IndexNumber"`
	IsFolder                bool                `json:"IsFolder"`
	ParentID                string              `json:"ParentId"`
	Type                    string              `json:"Type"`
	GenreItems              []NamedIDDTO        `json:"GenreItems"`
	ParentLogoItemID        string              `json:"ParentLogoItemId"`
	ParentBackdropItemID    string              `json:"ParentBackdropItemId"`
	ParentBackdropImageTags []string            `json:"ParentBackdropImageTags"`
	UserData                TVLatestUserDataDTO `json:"UserData"`
	SeriesName              string              `json:"SeriesName"`
	SeriesID                string              `json:"SeriesId"`
	SeriesPrimaryImageTag   string              `json:"SeriesPrimaryImageTag"`
	ImageTags               ImageTagsDTO        `json:"ImageTags"`
	BackdropImageTags       []string            `json:"BackdropImageTags"`
	ParentLogoImageTag      string              `json:"ParentLogoImageTag"`
}

type SeasonBasicItemDTO struct {
	Name                    string              `json:"Name"`
	ServerID                string              `json:"ServerId"`
	ID                      string              `json:"Id"`
	IndexNumber             int                 `json:"IndexNumber"`
	IsFolder                bool                `json:"IsFolder"`
	Type                    string              `json:"Type"`
	ParentLogoItemID        string              `json:"ParentLogoItemId"`
	ParentBackdropItemID    string              `json:"ParentBackdropItemId"`
	ParentBackdropImageTags []string            `json:"ParentBackdropImageTags"`
	UserData                TVLatestUserDataDTO `json:"UserData"`
	SeriesName              string              `json:"SeriesName"`
	SeriesID                string              `json:"SeriesId"`
	SeriesPrimaryImageTag   string              `json:"SeriesPrimaryImageTag"`
	ImageTags               ImageTagsDTO        `json:"ImageTags"`
	BackdropImageTags       []string            `json:"BackdropImageTags"`
	ParentLogoImageTag      string              `json:"ParentLogoImageTag"`
}

type SeasonLennaItemDTO struct {
	Name                    string              `json:"Name"`
	ServerID                string              `json:"ServerId"`
	ID                      string              `json:"Id"`
	SupportsSync            bool                `json:"SupportsSync"`
	Overview                string              `json:"Overview"`
	ProductionYear          int                 `json:"ProductionYear"`
	IndexNumber             int                 `json:"IndexNumber"`
	IsFolder                bool                `json:"IsFolder"`
	Type                    string              `json:"Type"`
	ParentLogoItemID        string              `json:"ParentLogoItemId"`
	ParentBackdropItemID    string              `json:"ParentBackdropItemId"`
	ParentBackdropImageTags []string            `json:"ParentBackdropImageTags"`
	UserData                TVLatestUserDataDTO `json:"UserData"`
	SeriesName              string              `json:"SeriesName"`
	SeriesID                string              `json:"SeriesId"`
	SeriesPrimaryImageTag   string              `json:"SeriesPrimaryImageTag"`
	ImageTags               ImageTagsDTO        `json:"ImageTags"`
	BackdropImageTags       []string            `json:"BackdropImageTags"`
	ParentLogoImageTag      string              `json:"ParentLogoImageTag"`
}

type SeasonFamilyItemDTO struct {
	Name                    string              `json:"Name"`
	ServerID                string              `json:"ServerId"`
	ID                      string              `json:"Id"`
	SupportsSync            bool                `json:"SupportsSync"`
	PremiereDate            string              `json:"PremiereDate"`
	Overview                string              `json:"Overview"`
	IndexNumber             int                 `json:"IndexNumber"`
	IsFolder                bool                `json:"IsFolder"`
	Type                    string              `json:"Type"`
	People                  []PersonDTO         `json:"People"`
	ParentLogoItemID        string              `json:"ParentLogoItemId"`
	ParentBackdropItemID    string              `json:"ParentBackdropItemId"`
	ParentBackdropImageTags []string            `json:"ParentBackdropImageTags"`
	UserData                TVLatestUserDataDTO `json:"UserData"`
	ChildCount              int                 `json:"ChildCount"`
	SeriesName              string              `json:"SeriesName"`
	SeriesID                string              `json:"SeriesId"`
	SeriesPrimaryImageTag   string              `json:"SeriesPrimaryImageTag"`
	ImageTags               ImageTagsDTO        `json:"ImageTags"`
	BackdropImageTags       []string            `json:"BackdropImageTags"`
	ParentLogoImageTag      string              `json:"ParentLogoImageTag"`
}

type SeasonRichItemDTO = SeasonLennaItemDTO

type EpisodesResponseDTO struct {
	Items            []EpisodeListItemDTO `json:"Items"`
	TotalRecordCount int                  `json:"TotalRecordCount"`
}

type EpisodesBasicResponseDTO struct {
	Items            []EpisodeBasicItemDTO `json:"Items"`
	TotalRecordCount int                   `json:"TotalRecordCount"`
}

type EpisodesInfuseResponseDTO struct {
	Items            []EpisodeInfuseItemDTO `json:"Items"`
	TotalRecordCount int                    `json:"TotalRecordCount"`
}

type EpisodesLennaResponseDTO struct {
	Items            []EpisodeLennaItemDTO `json:"Items"`
	TotalRecordCount int                   `json:"TotalRecordCount"`
}

type EpisodesRichResponseDTO = EpisodesLennaResponseDTO

type NextUpResponseDTO struct {
	Items            []NextUpItemDTO `json:"Items"`
	TotalRecordCount int             `json:"TotalRecordCount"`
}

type NextUpUserDataDTO struct {
	PlayedPercentage      float64 `json:"PlayedPercentage"`
	PlaybackPositionTicks int64   `json:"PlaybackPositionTicks"`
	PlayCount             int     `json:"PlayCount"`
	IsFavorite            bool    `json:"IsFavorite"`
	Played                bool    `json:"Played"`
}

type NextUpItemDTO struct {
	Name                    string            `json:"Name"`
	ServerID                string            `json:"ServerId"`
	ID                      string            `json:"Id"`
	CanDelete               bool              `json:"CanDelete"`
	SupportsSync            bool              `json:"SupportsSync"`
	PremiereDate            string            `json:"PremiereDate"`
	RunTimeTicks            int64             `json:"RunTimeTicks"`
	IndexNumber             int               `json:"IndexNumber"`
	ParentIndexNumber       int               `json:"ParentIndexNumber"`
	IsFolder                bool              `json:"IsFolder"`
	Type                    string            `json:"Type"`
	ParentLogoItemID        string            `json:"ParentLogoItemId"`
	ParentBackdropItemID    string            `json:"ParentBackdropItemId"`
	ParentBackdropImageTags []string          `json:"ParentBackdropImageTags"`
	UserData                NextUpUserDataDTO `json:"UserData"`
	SeriesName              string            `json:"SeriesName"`
	SeriesID                string            `json:"SeriesId"`
	SeasonID                string            `json:"SeasonId"`
	PrimaryImageAspectRatio float64           `json:"PrimaryImageAspectRatio"`
	SeriesPrimaryImageTag   string            `json:"SeriesPrimaryImageTag"`
	SeasonName              string            `json:"SeasonName"`
	ImageTags               ImageTagsDTO      `json:"ImageTags"`
	BackdropImageTags       []string          `json:"BackdropImageTags"`
	ParentLogoImageTag      string            `json:"ParentLogoImageTag"`
	MediaType               string            `json:"MediaType"`
}

type EpisodeListItemDTO struct {
	Name                    string                 `json:"Name"`
	ServerID                string                 `json:"ServerId"`
	ID                      string                 `json:"Id"`
	CanDownload             bool                   `json:"CanDownload"`
	SupportsSync            bool                   `json:"SupportsSync"`
	Container               string                 `json:"Container"`
	PremiereDate            string                 `json:"PremiereDate"`
	MediaSources            []DetailMediaSourceDTO `json:"MediaSources"`
	Path                    string                 `json:"Path"`
	Overview                string                 `json:"Overview"`
	RunTimeTicks            int64                  `json:"RunTimeTicks"`
	Size                    int64                  `json:"Size"`
	Bitrate                 int                    `json:"Bitrate"`
	IndexNumber             int                    `json:"IndexNumber"`
	ParentIndexNumber       int                    `json:"ParentIndexNumber"`
	IsFolder                bool                   `json:"IsFolder"`
	Type                    string                 `json:"Type"`
	People                  []PersonDTO            `json:"People"`
	ParentLogoItemID        string                 `json:"ParentLogoItemId"`
	ParentBackdropItemID    string                 `json:"ParentBackdropItemId"`
	ParentBackdropImageTags []string               `json:"ParentBackdropImageTags"`
	UserData                SimpleUserDataDTO      `json:"UserData"`
	SeriesName              string                 `json:"SeriesName"`
	SeriesID                string                 `json:"SeriesId"`
	SeasonID                string                 `json:"SeasonId"`
	SeriesPrimaryImageTag   string                 `json:"SeriesPrimaryImageTag"`
	SeasonName              string                 `json:"SeasonName"`
	ImageTags               ImageTagsDTO           `json:"ImageTags"`
	BackdropImageTags       []string               `json:"BackdropImageTags"`
	ParentLogoImageTag      string                 `json:"ParentLogoImageTag"`
	Chapters                []DetailChapterDTO     `json:"Chapters"`
	MediaType               string                 `json:"MediaType"`
}

type EpisodeBasicItemDTO struct {
	Name                    string            `json:"Name"`
	ServerID                string            `json:"ServerId"`
	ID                      string            `json:"Id"`
	CanDownload             bool              `json:"CanDownload"`
	SupportsSync            bool              `json:"SupportsSync"`
	PremiereDate            string            `json:"PremiereDate"`
	RunTimeTicks            int64             `json:"RunTimeTicks"`
	IndexNumber             int               `json:"IndexNumber"`
	ParentIndexNumber       int               `json:"ParentIndexNumber"`
	IsFolder                bool              `json:"IsFolder"`
	Type                    string            `json:"Type"`
	ParentLogoItemID        string            `json:"ParentLogoItemId"`
	ParentBackdropItemID    string            `json:"ParentBackdropItemId"`
	ParentBackdropImageTags []string          `json:"ParentBackdropImageTags"`
	UserData                SimpleUserDataDTO `json:"UserData"`
	SeriesName              string            `json:"SeriesName"`
	SeriesID                string            `json:"SeriesId"`
	SeasonID                string            `json:"SeasonId"`
	SeriesPrimaryImageTag   string            `json:"SeriesPrimaryImageTag"`
	SeasonName              string            `json:"SeasonName"`
	ImageTags               ImageTagsDTO      `json:"ImageTags"`
	BackdropImageTags       []string          `json:"BackdropImageTags"`
	ParentLogoImageTag      string            `json:"ParentLogoImageTag"`
	MediaType               string            `json:"MediaType"`
}

type EpisodeInfuseItemDTO struct {
	Name                    string                 `json:"Name"`
	ServerID                string                 `json:"ServerId"`
	ID                      string                 `json:"Id"`
	Etag                    string                 `json:"Etag"`
	Container               string                 `json:"Container"`
	PremiereDate            string                 `json:"PremiereDate"`
	MediaSources            []DetailMediaSourceDTO `json:"MediaSources"`
	AlternateMediaSources   []any                  `json:"AlternateMediaSources"`
	Overview                string                 `json:"Overview"`
	Genres                  []string               `json:"Genres"`
	RunTimeTicks            int64                  `json:"RunTimeTicks"`
	Size                    int64                  `json:"Size"`
	Bitrate                 int                    `json:"Bitrate"`
	IndexNumber             int                    `json:"IndexNumber"`
	ParentIndexNumber       int                    `json:"ParentIndexNumber"`
	ProviderIDs             map[string]any         `json:"ProviderIds"`
	IsFolder                bool                   `json:"IsFolder"`
	ParentID                string                 `json:"ParentId"`
	Type                    string                 `json:"Type"`
	GenreItems              []NamedIDDTO           `json:"GenreItems"`
	ParentLogoItemID        string                 `json:"ParentLogoItemId"`
	ParentBackdropItemID    string                 `json:"ParentBackdropItemId"`
	ParentBackdropImageTags []string               `json:"ParentBackdropImageTags"`
	UserData                SimpleUserDataDTO      `json:"UserData"`
	SeriesName              string                 `json:"SeriesName"`
	SeriesID                string                 `json:"SeriesId"`
	SeasonID                string                 `json:"SeasonId"`
	SeriesPrimaryImageTag   string                 `json:"SeriesPrimaryImageTag"`
	SeasonName              string                 `json:"SeasonName"`
	ImageTags               ImageTagsDTO           `json:"ImageTags"`
	BackdropImageTags       []string               `json:"BackdropImageTags"`
	ParentLogoImageTag      string                 `json:"ParentLogoImageTag"`
	MediaType               string                 `json:"MediaType"`
}

type EpisodeLennaItemDTO struct {
	Name                    string                 `json:"Name"`
	ServerID                string                 `json:"ServerId"`
	ID                      string                 `json:"Id"`
	DateCreated             string                 `json:"DateCreated"`
	SupportsSync            bool                   `json:"SupportsSync"`
	Container               string                 `json:"Container"`
	SortName                string                 `json:"SortName"`
	PremiereDate            string                 `json:"PremiereDate"`
	MediaSources            []DetailMediaSourceDTO `json:"MediaSources"`
	Path                    string                 `json:"Path"`
	Overview                string                 `json:"Overview"`
	Genres                  []string               `json:"Genres"`
	CommunityRating         float64                `json:"CommunityRating"`
	RunTimeTicks            int64                  `json:"RunTimeTicks"`
	Size                    int64                  `json:"Size"`
	Bitrate                 int                    `json:"Bitrate"`
	ProductionYear          int                    `json:"ProductionYear"`
	IndexNumber             int                    `json:"IndexNumber"`
	ParentIndexNumber       int                    `json:"ParentIndexNumber"`
	ProviderIDs             map[string]any         `json:"ProviderIds"`
	IsFolder                bool                   `json:"IsFolder"`
	ParentID                string                 `json:"ParentId"`
	Type                    string                 `json:"Type"`
	People                  []PersonDTO            `json:"People"`
	Studios                 []NamedIDDTO           `json:"Studios"`
	GenreItems              []NamedIDDTO           `json:"GenreItems"`
	ParentBackdropItemID    string                 `json:"ParentBackdropItemId"`
	ParentBackdropImageTags []string               `json:"ParentBackdropImageTags"`
	UserData                SimpleUserDataDTO      `json:"UserData"`
	SeriesName              string                 `json:"SeriesName"`
	SeriesID                string                 `json:"SeriesId"`
	SeasonID                string                 `json:"SeasonId"`
	SeriesPrimaryImageTag   string                 `json:"SeriesPrimaryImageTag"`
	SeasonName              string                 `json:"SeasonName"`
	MediaStreams            []any                  `json:"MediaStreams"`
	ImageTags               ImageTagsDTO           `json:"ImageTags"`
	BackdropImageTags       []string               `json:"BackdropImageTags"`
	MediaType               string                 `json:"MediaType"`
}

type EpisodeRichItemDTO = EpisodeLennaItemDTO
