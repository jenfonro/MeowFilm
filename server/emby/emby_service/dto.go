package emby_service

type ImageTagsDTO struct {
	Primary string `json:"Primary"`
	Logo    string `json:"Logo,omitempty"`
}

type CollectionFolderItemDTO struct {
	Name                    string             `json:"Name"`
	ServerID                string             `json:"ServerId"`
	ID                      string             `json:"Id"`
	GUID                    string             `json:"Guid"`
	Etag                    string             `json:"Etag"`
	DateCreated             string             `json:"DateCreated"`
	DateModified            string             `json:"DateModified"`
	CanDelete               bool               `json:"CanDelete"`
	CanDownload             bool               `json:"CanDownload"`
	SupportsSync            bool               `json:"SupportsSync"`
	PresentationUniqueKey   string             `json:"PresentationUniqueKey"`
	SortName                string             `json:"SortName"`
	ForcedSortName          string             `json:"ForcedSortName"`
	ExternalURLs            []any              `json:"ExternalUrls"`
	Taglines                []string           `json:"Taglines"`
	RemoteTrailers          []any              `json:"RemoteTrailers"`
	ProviderIDs             map[string]string  `json:"ProviderIds"`
	IsFolder                bool               `json:"IsFolder"`
	ParentID                string             `json:"ParentId"`
	Type                    string             `json:"Type"`
	UserData                CollectionUserData `json:"UserData"`
	ChildCount              int                `json:"ChildCount"`
	DisplayPreferencesID    string             `json:"DisplayPreferencesId"`
	PrimaryImageAspectRatio float64            `json:"PrimaryImageAspectRatio"`
	CollectionType          string             `json:"CollectionType,omitempty"`
	ImageTags               ImageTagsDTO       `json:"ImageTags"`
	BackdropImageTags       []string           `json:"BackdropImageTags"`
	LockedFields            []string           `json:"LockedFields"`
	LockData                bool               `json:"LockData"`
}

type CollectionUserData struct {
	PlaybackPositionTicks int64 `json:"PlaybackPositionTicks"`
	IsFavorite            bool  `json:"IsFavorite"`
	Played                bool  `json:"Played"`
}

type MovieLatestUserDataDTO struct {
	PlaybackPositionTicks int64    `json:"PlaybackPositionTicks"`
	PlayCount             int      `json:"PlayCount"`
	IsFavorite            bool     `json:"IsFavorite"`
	Played                bool     `json:"Played"`
	PlayedPercentage      *float64 `json:"PlayedPercentage,omitempty"`
}

type TVLatestUserDataDTO struct {
	UnplayedItemCount     int   `json:"UnplayedItemCount"`
	PlaybackPositionTicks int64 `json:"PlaybackPositionTicks"`
	PlayCount             int   `json:"PlayCount"`
	IsFavorite            bool  `json:"IsFavorite"`
	Played                bool  `json:"Played"`
}

type MovieLatestItemDTO struct {
	Name                    string                 `json:"Name"`
	ServerID                string                 `json:"ServerId"`
	ID                      string                 `json:"Id"`
	Etag                    string                 `json:"Etag"`
	DateCreated             string                 `json:"DateCreated"`
	Container               string                 `json:"Container"`
	SortName                string                 `json:"SortName"`
	CanDelete               bool                   `json:"CanDelete"`
	SupportsSync            bool                   `json:"SupportsSync"`
	PremiereDate            string                 `json:"PremiereDate"`
	MediaSources            []ResumeMediaSourceDTO `json:"MediaSources"`
	Path                    string                 `json:"Path"`
	OfficialRating          string                 `json:"OfficialRating"`
	Overview                string                 `json:"Overview"`
	Genres                  []string               `json:"Genres"`
	CommunityRating         float64                `json:"CommunityRating"`
	RunTimeTicks            int64                  `json:"RunTimeTicks"`
	Size                    int64                  `json:"Size"`
	Bitrate                 int                    `json:"Bitrate"`
	ProductionYear          int                    `json:"ProductionYear"`
	ProviderIDs             map[string]any         `json:"ProviderIds"`
	IsFolder                bool                   `json:"IsFolder"`
	ParentID                string                 `json:"ParentId"`
	Type                    string                 `json:"Type"`
	GenreItems              []NamedIDDTO           `json:"GenreItems"`
	UserData                MovieLatestUserDataDTO `json:"UserData"`
	PrimaryImageAspectRatio float64                `json:"PrimaryImageAspectRatio"`
	ImageTags               ImageTagsDTO           `json:"ImageTags"`
	BackdropImageTags       []string               `json:"BackdropImageTags"`
	MediaType               string                 `json:"MediaType"`
}

type TVLatestItemDTO struct {
	Name                    string              `json:"Name"`
	ServerID                string              `json:"ServerId"`
	ID                      string              `json:"Id"`
	Etag                    string              `json:"Etag"`
	DateCreated             string              `json:"DateCreated"`
	SortName                string              `json:"SortName"`
	CanDelete               bool                `json:"CanDelete"`
	SupportsSync            bool                `json:"SupportsSync"`
	PremiereDate            string              `json:"PremiereDate"`
	Path                    string              `json:"Path"`
	Overview                string              `json:"Overview"`
	Genres                  []string            `json:"Genres"`
	RunTimeTicks            int64               `json:"RunTimeTicks"`
	ProductionYear          int                 `json:"ProductionYear"`
	ProviderIDs             map[string]any      `json:"ProviderIds"`
	IsFolder                bool                `json:"IsFolder"`
	ParentID                string              `json:"ParentId"`
	Type                    string              `json:"Type"`
	GenreItems              []NamedIDDTO        `json:"GenreItems"`
	UserData                TVLatestUserDataDTO `json:"UserData"`
	RecursiveItemCount      int                 `json:"RecursiveItemCount"`
	ChildCount              int                 `json:"ChildCount"`
	Status                  string              `json:"Status"`
	AirDays                 []string            `json:"AirDays"`
	PrimaryImageAspectRatio float64             `json:"PrimaryImageAspectRatio"`
	ImageTags               ImageTagsDTO        `json:"ImageTags"`
	BackdropImageTags       []string            `json:"BackdropImageTags"`
}

type SectionMovieItemDTO struct {
	Name                    string                 `json:"Name"`
	ServerID                string                 `json:"ServerId"`
	ID                      string                 `json:"Id"`
	Etag                    string                 `json:"Etag"`
	DateCreated             string                 `json:"DateCreated"`
	Container               string                 `json:"Container"`
	SortName                string                 `json:"SortName"`
	CanDownload             bool                   `json:"CanDownload"`
	SupportsSync            bool                   `json:"SupportsSync"`
	PremiereDate            string                 `json:"PremiereDate"`
	EndDate                 string                 `json:"EndDate"`
	Overview                string                 `json:"Overview"`
	CommunityRating         float64                `json:"CommunityRating"`
	RunTimeTicks            int64                  `json:"RunTimeTicks"`
	ProductionYear          int                    `json:"ProductionYear"`
	ProviderIDs             map[string]any         `json:"ProviderIds"`
	IsFolder                bool                   `json:"IsFolder"`
	Type                    string                 `json:"Type"`
	UserData                MovieLatestUserDataDTO `json:"UserData"`
	PrimaryImageAspectRatio float64                `json:"PrimaryImageAspectRatio"`
	ImageTags               ImageTagsDTO           `json:"ImageTags"`
	BackdropImageTags       []string               `json:"BackdropImageTags"`
	MediaType               string                 `json:"MediaType"`
}

type SectionSeriesItemDTO struct {
	Name                    string              `json:"Name"`
	ServerID                string              `json:"ServerId"`
	ID                      string              `json:"Id"`
	Etag                    string              `json:"Etag"`
	DateCreated             string              `json:"DateCreated"`
	SortName                string              `json:"SortName"`
	CanDownload             bool                `json:"CanDownload"`
	SupportsSync            bool                `json:"SupportsSync"`
	PremiereDate            string              `json:"PremiereDate"`
	EndDate                 string              `json:"EndDate"`
	Overview                string              `json:"Overview"`
	CommunityRating         float64             `json:"CommunityRating"`
	RunTimeTicks            int64               `json:"RunTimeTicks"`
	ProductionYear          int                 `json:"ProductionYear"`
	ProviderIDs             map[string]any      `json:"ProviderIds"`
	IsFolder                bool                `json:"IsFolder"`
	Type                    string              `json:"Type"`
	UserData                TVLatestUserDataDTO `json:"UserData"`
	RecursiveItemCount      int                 `json:"RecursiveItemCount"`
	ChildCount              int                 `json:"ChildCount"`
	Status                  string              `json:"Status"`
	AirDays                 []string            `json:"AirDays"`
	PrimaryImageAspectRatio float64             `json:"PrimaryImageAspectRatio"`
	ImageTags               ImageTagsDTO        `json:"ImageTags"`
	BackdropImageTags       []string            `json:"BackdropImageTags"`
}

type SearchMovieItemDTO struct {
	Name                  string                 `json:"Name"`
	ServerID              string                 `json:"ServerId"`
	ID                    string                 `json:"Id"`
	Etag                  string                 `json:"Etag"`
	DateCreated           string                 `json:"DateCreated"`
	Container             string                 `json:"Container"`
	SortName              string                 `json:"SortName"`
	PremiereDate          string                 `json:"PremiereDate"`
	ProductionLocations   []string               `json:"ProductionLocations,omitempty"`
	MediaSources          []ResumeMediaSourceDTO `json:"MediaSources"`
	AlternateMediaSources []ResumeMediaSourceDTO `json:"AlternateMediaSources"`
	Path                  string                 `json:"Path"`
	OfficialRating        string                 `json:"OfficialRating"`
	Overview              string                 `json:"Overview"`
	Genres                []string               `json:"Genres"`
	CommunityRating       float64                `json:"CommunityRating"`
	RunTimeTicks          int64                  `json:"RunTimeTicks"`
	Size                  int64                  `json:"Size"`
	Bitrate               int                    `json:"Bitrate"`
	ProductionYear        int                    `json:"ProductionYear"`
	ProviderIDs           map[string]any         `json:"ProviderIds"`
	IsFolder              bool                   `json:"IsFolder"`
	ParentID              string                 `json:"ParentId"`
	Type                  string                 `json:"Type"`
	GenreItems            []NamedIDDTO           `json:"GenreItems"`
	UserData              MovieLatestUserDataDTO `json:"UserData"`
	ImageTags             ImageTagsDTO           `json:"ImageTags"`
	BackdropImageTags     []string               `json:"BackdropImageTags"`
	MediaType             string                 `json:"MediaType"`
}

type SearchSeriesItemDTO struct {
	Name                  string              `json:"Name"`
	ServerID              string              `json:"ServerId"`
	ID                    string              `json:"Id"`
	Etag                  string              `json:"Etag"`
	DateCreated           string              `json:"DateCreated"`
	SortName              string              `json:"SortName"`
	PremiereDate          string              `json:"PremiereDate,omitempty"`
	ProductionLocations   []string            `json:"ProductionLocations,omitempty"`
	Path                  string              `json:"Path"`
	Overview              string              `json:"Overview"`
	Genres                []string            `json:"Genres"`
	CommunityRating       float64             `json:"CommunityRating"`
	RunTimeTicks          int64               `json:"RunTimeTicks"`
	ProductionYear        int                 `json:"ProductionYear"`
	ProviderIDs           map[string]any      `json:"ProviderIds"`
	IsFolder              bool                `json:"IsFolder"`
	ParentID              string              `json:"ParentId"`
	Type                  string              `json:"Type"`
	GenreItems            []NamedIDDTO        `json:"GenreItems"`
	UserData              TVLatestUserDataDTO `json:"UserData"`
	RecursiveItemCount    int                 `json:"RecursiveItemCount"`
	ChildCount            int                 `json:"ChildCount"`
	AirDays               []string            `json:"AirDays"`
	ImageTags             ImageTagsDTO        `json:"ImageTags"`
	BackdropImageTags     []string            `json:"BackdropImageTags"`
}

type SearchPrimaryImageTagsDTO struct {
	Primary string `json:"Primary"`
}

type SearchLennaMovieItemDTO struct {
	Name              string                 `json:"Name"`
	ServerID          string                 `json:"ServerId"`
	ID                string                 `json:"Id"`
	SupportsSync      bool                   `json:"SupportsSync"`
	Container         string                 `json:"Container"`
	CommunityRating   float64                `json:"CommunityRating"`
	RunTimeTicks      int64                  `json:"RunTimeTicks"`
	ProductionYear    int                    `json:"ProductionYear"`
	IsFolder          bool                   `json:"IsFolder"`
	Type              string                 `json:"Type"`
	UserData          MovieLatestUserDataDTO `json:"UserData"`
	ImageTags         SearchPrimaryImageTagsDTO `json:"ImageTags"`
	BackdropImageTags []string               `json:"BackdropImageTags"`
	MediaType         string                 `json:"MediaType"`
}

type SearchLennaSeriesItemDTO struct {
	Name              string              `json:"Name"`
	ServerID          string              `json:"ServerId"`
	ID                string              `json:"Id"`
	SupportsSync      bool                `json:"SupportsSync"`
	RunTimeTicks      int64               `json:"RunTimeTicks"`
	ProductionYear    int                 `json:"ProductionYear"`
	IsFolder          bool                `json:"IsFolder"`
	Type              string              `json:"Type"`
	UserData          TVLatestUserDataDTO `json:"UserData"`
	AirDays           []string            `json:"AirDays"`
	ImageTags         SearchPrimaryImageTagsDTO `json:"ImageTags"`
	BackdropImageTags []string            `json:"BackdropImageTags"`
}

type SearchFamilyMovieItemDTO struct {
	Name                string                 `json:"Name"`
	ServerID            string                 `json:"ServerId"`
	ID                  string                 `json:"Id"`
	PremiereDate        string                 `json:"PremiereDate,omitempty"`
	ProductionLocations []string               `json:"ProductionLocations,omitempty"`
	Overview            string                 `json:"Overview"`
	CommunityRating     float64                `json:"CommunityRating"`
	RunTimeTicks        int64                  `json:"RunTimeTicks"`
	ProductionYear      int                    `json:"ProductionYear"`
	IsFolder            bool                   `json:"IsFolder"`
	Type                string                 `json:"Type"`
	UserData            MovieLatestUserDataDTO `json:"UserData"`
	ImageTags           ImageTagsDTO           `json:"ImageTags"`
	BackdropImageTags   []string               `json:"BackdropImageTags"`
	MediaType           string                 `json:"MediaType"`
}

type SearchFamilySeriesItemDTO struct {
	Name              string              `json:"Name"`
	ServerID          string              `json:"ServerId"`
	ID                string              `json:"Id"`
	PremiereDate      string              `json:"PremiereDate,omitempty"`
	Overview          string              `json:"Overview"`
	RunTimeTicks      int64               `json:"RunTimeTicks"`
	ProductionYear    int                 `json:"ProductionYear"`
	IsFolder          bool                `json:"IsFolder"`
	Type              string              `json:"Type"`
	UserData          TVLatestUserDataDTO `json:"UserData"`
	AirDays           []string            `json:"AirDays"`
	ImageTags         ImageTagsDTO        `json:"ImageTags"`
	BackdropImageTags []string            `json:"BackdropImageTags"`
}

type SimilarImageTagsDTO struct {
	Primary string `json:"Primary"`
}

type SimilarSeriesItemDTO struct {
	Name                    string              `json:"Name"`
	ServerID                string              `json:"ServerId"`
	ID                      string              `json:"Id"`
	DateCreated             string              `json:"DateCreated"`
	SupportsSync            bool                `json:"SupportsSync"`
	SortName                string              `json:"SortName"`
	PremiereDate            string              `json:"PremiereDate"`
	RunTimeTicks            int64               `json:"RunTimeTicks"`
	ProductionYear          int                 `json:"ProductionYear"`
	ProviderIDs             map[string]any      `json:"ProviderIds"`
	IsFolder                bool                `json:"IsFolder"`
	Type                    string              `json:"Type"`
	UserData                TVLatestUserDataDTO `json:"UserData"`
	AirDays                 []string            `json:"AirDays"`
	PrimaryImageAspectRatio float64             `json:"PrimaryImageAspectRatio"`
	ImageTags               SimilarImageTagsDTO `json:"ImageTags"`
	BackdropImageTags       []string            `json:"BackdropImageTags"`
}

type ResumeResponse struct {
	Items            []any `json:"Items"`
	TotalRecordCount int   `json:"TotalRecordCount"`
}

type HideFromResumeResponse struct {
	PlayedPercentage      float64 `json:"PlayedPercentage"`
	PlaybackPositionTicks int64   `json:"PlaybackPositionTicks"`
	PlayCount             int     `json:"PlayCount"`
	IsFavorite            bool    `json:"IsFavorite"`
	LastPlayedDate        string  `json:"LastPlayedDate"`
	Played                bool    `json:"Played"`
}

type ResumeEpisodeItemDTO struct {
	Name                    string                   `json:"Name"`
	ServerID                string                   `json:"ServerId"`
	ID                      string                   `json:"Id"`
	Etag                    string                   `json:"Etag"`
	DateCreated             string                   `json:"DateCreated"`
	Container               string                   `json:"Container"`
	SortName                string                   `json:"SortName"`
	PremiereDate            string                   `json:"PremiereDate"`
	MediaSources            []ResumeMediaSourceDTO   `json:"MediaSources"`
	Path                    string                   `json:"Path"`
	Overview                string                   `json:"Overview"`
	Genres                  []string                 `json:"Genres"`
	CommunityRating         float64                  `json:"CommunityRating"`
	RunTimeTicks            int64                    `json:"RunTimeTicks"`
	Size                    int64                    `json:"Size"`
	Bitrate                 int                      `json:"Bitrate"`
	IndexNumber             int                      `json:"IndexNumber"`
	ParentIndexNumber       int                      `json:"ParentIndexNumber"`
	ProviderIDs             map[string]any           `json:"ProviderIds"`
	IsFolder                bool                     `json:"IsFolder"`
	ParentID                string                   `json:"ParentId"`
	Type                    string                   `json:"Type"`
	GenreItems              []NamedIDDTO             `json:"GenreItems"`
	ParentLogoItemID        string                   `json:"ParentLogoItemId,omitempty"`
	ParentBackdropItemID    string                   `json:"ParentBackdropItemId,omitempty"`
	ParentBackdropImageTags []string                 `json:"ParentBackdropImageTags"`
	UserData                ResumeEpisodeUserDataDTO `json:"UserData"`
	SeriesName              string                   `json:"SeriesName"`
	SeriesID                string                   `json:"SeriesId"`
	SeasonID                string                   `json:"SeasonId"`
	SeriesPrimaryImageTag   string                   `json:"SeriesPrimaryImageTag,omitempty"`
	SeasonName              string                   `json:"SeasonName"`
	ImageTags               ImageTagsDTO             `json:"ImageTags"`
	BackdropImageTags       []string                 `json:"BackdropImageTags"`
	ParentLogoImageTag      string                   `json:"ParentLogoImageTag,omitempty"`
	MediaType               string                   `json:"MediaType"`
}

type ResumeEpisodeUserDataDTO struct {
	PlaybackPositionTicks int64    `json:"PlaybackPositionTicks"`
	PlayCount             int      `json:"PlayCount"`
	IsFavorite            bool     `json:"IsFavorite"`
	Played                bool     `json:"Played"`
	PlayedPercentage      *float64 `json:"PlayedPercentage,omitempty"`
}

type ResumeMovieItemDTO struct {
	Name              string                 `json:"Name"`
	ServerID          string                 `json:"ServerId"`
	ID                string                 `json:"Id"`
	Etag              string                 `json:"Etag"`
	DateCreated       string                 `json:"DateCreated"`
	Container         string                 `json:"Container"`
	SortName          string                 `json:"SortName"`
	PremiereDate      string                 `json:"PremiereDate"`
	MediaSources      []ResumeMediaSourceDTO `json:"MediaSources"`
	Path              string                 `json:"Path"`
	Overview          string                 `json:"Overview"`
	Genres            []string               `json:"Genres"`
	CommunityRating   float64                `json:"CommunityRating"`
	RunTimeTicks      int64                  `json:"RunTimeTicks"`
	Size              int64                  `json:"Size"`
	Bitrate           int                    `json:"Bitrate"`
	ProviderIDs       map[string]any         `json:"ProviderIds"`
	IsFolder          bool                   `json:"IsFolder"`
	ParentID          string                 `json:"ParentId"`
	Type              string                 `json:"Type"`
	GenreItems        []NamedIDDTO           `json:"GenreItems"`
	UserData          MovieLatestUserDataDTO `json:"UserData"`
	ImageTags         ImageTagsDTO           `json:"ImageTags"`
	BackdropImageTags []string               `json:"BackdropImageTags"`
	MediaType         string                 `json:"MediaType"`
}

type ResumeMediaSourceDTO struct {
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
