package emby

type embyVirtualFolderDTO struct {
	Name               string                `json:"Name"`
	Locations          []string              `json:"Locations"`
	CollectionType     string                `json:"CollectionType,omitempty"`
	LibraryOptions     embyLibraryOptionsDTO `json:"LibraryOptions"`
	ItemID             string                `json:"ItemId"`
	ID                 string                `json:"Id"`
	GUID               string                `json:"Guid"`
	PrimaryImageItemID string                `json:"PrimaryImageItemId"`
	PrimaryImageTag    string                `json:"PrimaryImageTag"`
}

type embyLibraryOptionsDTO struct {
	EnableArchiveMediaFiles                 bool                       `json:"EnableArchiveMediaFiles"`
	EnablePhotos                            bool                       `json:"EnablePhotos"`
	EnableRealtimeMonitor                   bool                       `json:"EnableRealtimeMonitor"`
	EnableMarkerDetection                   bool                       `json:"EnableMarkerDetection"`
	EnableMarkerDetectionDuringLibraryScan  bool                       `json:"EnableMarkerDetectionDuringLibraryScan"`
	IntroDetectionFingerprintLength         int                        `json:"IntroDetectionFingerprintLength"`
	EnableChapterImageExtraction            bool                       `json:"EnableChapterImageExtraction"`
	ExtractChapterImagesDuringLibraryScan   bool                       `json:"ExtractChapterImagesDuringLibraryScan"`
	DownloadImagesInAdvance                 bool                       `json:"DownloadImagesInAdvance"`
	CacheImages                             bool                       `json:"CacheImages"`
	ExcludeFromSearch                       bool                       `json:"ExcludeFromSearch"`
	EnablePlexIgnore                        bool                       `json:"EnablePlexIgnore"`
	PathInfos                               []embyLibraryPathInfoDTO   `json:"PathInfos"`
	IgnoreHiddenFiles                       bool                       `json:"IgnoreHiddenFiles"`
	IgnoreFileExtensions                    []string                   `json:"IgnoreFileExtensions"`
	SaveLocalMetadata                       bool                       `json:"SaveLocalMetadata"`
	SaveMetadataHidden                      bool                       `json:"SaveMetadataHidden"`
	SaveLocalThumbnailSets                  bool                       `json:"SaveLocalThumbnailSets"`
	ImportPlaylists                         bool                       `json:"ImportPlaylists"`
	EnableAutomaticSeriesGrouping           bool                       `json:"EnableAutomaticSeriesGrouping"`
	ShareEmbeddedMusicAlbumImages           bool                       `json:"ShareEmbeddedMusicAlbumImages"`
	EnableEmbeddedTitles                    bool                       `json:"EnableEmbeddedTitles"`
	EnableAudioResume                       bool                       `json:"EnableAudioResume"`
	AutoGenerateChapters                    bool                       `json:"AutoGenerateChapters"`
	MergeTopLevelFolders                    bool                       `json:"MergeTopLevelFolders"`
	AutoGenerateChapterIntervalMinutes      int                        `json:"AutoGenerateChapterIntervalMinutes"`
	AutomaticRefreshIntervalDays            int                        `json:"AutomaticRefreshIntervalDays"`
	PlaceholderMetadataRefreshIntervalDays  int                        `json:"PlaceholderMetadataRefreshIntervalDays"`
	PreferredMetadataLanguage               string                     `json:"PreferredMetadataLanguage"`
	PreferredImageLanguage                  string                     `json:"PreferredImageLanguage"`
	ContentType                             string                     `json:"ContentType,omitempty"`
	MetadataCountryCode                     string                     `json:"MetadataCountryCode"`
	MetadataSavers                          []string                   `json:"MetadataSavers"`
	DisabledLocalMetadataReaders            []string                   `json:"DisabledLocalMetadataReaders"`
	DisabledLyricsFetchers                  []string                   `json:"DisabledLyricsFetchers"`
	SaveLyricsWithMedia                     bool                       `json:"SaveLyricsWithMedia"`
	LyricsDownloadMaxAgeDays                int                        `json:"LyricsDownloadMaxAgeDays"`
	LyricsFetcherOrder                      []string                   `json:"LyricsFetcherOrder"`
	LyricsDownloadLanguages                 []string                   `json:"LyricsDownloadLanguages"`
	DisabledSubtitleFetchers                []string                   `json:"DisabledSubtitleFetchers"`
	SubtitleFetcherOrder                    []string                   `json:"SubtitleFetcherOrder"`
	SkipSubtitlesIfEmbeddedSubtitlesPresent bool                       `json:"SkipSubtitlesIfEmbeddedSubtitlesPresent"`
	SkipSubtitlesIfAudioTrackMatches        bool                       `json:"SkipSubtitlesIfAudioTrackMatches"`
	SubtitleDownloadLanguages               []string                   `json:"SubtitleDownloadLanguages"`
	SubtitleDownloadMaxAgeDays              int                        `json:"SubtitleDownloadMaxAgeDays"`
	RequirePerfectSubtitleMatch             bool                       `json:"RequirePerfectSubtitleMatch"`
	SaveSubtitlesWithMedia                  bool                       `json:"SaveSubtitlesWithMedia"`
	ForcedSubtitlesOnly                     bool                       `json:"ForcedSubtitlesOnly"`
	HearingImpairedSubtitlesOnly            bool                       `json:"HearingImpairedSubtitlesOnly"`
	TypeOptions                             []embyLibraryTypeOptionDTO `json:"TypeOptions"`
	CollapseSingleItemFolders               bool                       `json:"CollapseSingleItemFolders"`
	ForceCollapseSingleItemFolders          bool                       `json:"ForceCollapseSingleItemFolders"`
	EnableAdultMetadata                     bool                       `json:"EnableAdultMetadata"`
	ImportCollections                       bool                       `json:"ImportCollections"`
	EnableMultiVersionByFiles               bool                       `json:"EnableMultiVersionByFiles"`
	EnableMultiVersionByMetadata            bool                       `json:"EnableMultiVersionByMetadata"`
	EnableMultiPartItems                    bool                       `json:"EnableMultiPartItems"`
	MinCollectionItems                      int                        `json:"MinCollectionItems"`
	MinResumePct                            int                        `json:"MinResumePct"`
	MaxResumePct                            int                        `json:"MaxResumePct"`
	MinResumeDurationSeconds                int                        `json:"MinResumeDurationSeconds"`
	ThumbnailImagesIntervalSeconds          int                        `json:"ThumbnailImagesIntervalSeconds"`
	SampleIgnoreSize                        int64                      `json:"SampleIgnoreSize"`
}

type embyLibraryPathInfoDTO struct {
	Path     string `json:"Path"`
	Password string `json:"Password"`
}

type embyLibraryTypeOptionDTO struct {
	Type                 string   `json:"Type"`
	MetadataFetchers     []string `json:"MetadataFetchers"`
	MetadataFetcherOrder []string `json:"MetadataFetcherOrder"`
	ImageFetchers        []string `json:"ImageFetchers"`
	ImageFetcherOrder    []string `json:"ImageFetcherOrder"`
	ImageOptions         []any    `json:"ImageOptions"`
}
