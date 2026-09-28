package emby

type authenticateByNameResponse struct {
	User        embyAuthUserDTO    `json:"User"`
	SessionInfo embySessionInfoDTO `json:"SessionInfo"`
	AccessToken string             `json:"AccessToken"`
	ServerID    string             `json:"ServerId"`
}

type embyAuthUserDTO struct {
	Name                  string                   `json:"Name"`
	ServerID              string                   `json:"ServerId"`
	Prefix                string                   `json:"Prefix"`
	DateCreated           string                   `json:"DateCreated"`
	ID                    string                   `json:"Id"`
	HasPassword           bool                     `json:"HasPassword"`
	HasConfiguredPassword bool                     `json:"HasConfiguredPassword"`
	LastLoginDate         string                   `json:"LastLoginDate"`
	LastActivityDate      string                   `json:"LastActivityDate"`
	Configuration         embyUserConfigurationDTO `json:"Configuration"`
	Policy                embyUserPolicyDTO        `json:"Policy"`
}

type embyUserConfigurationDTO struct {
	PlayDefaultAudioTrack      bool     `json:"PlayDefaultAudioTrack"`
	DisplayMissingEpisodes     bool     `json:"DisplayMissingEpisodes"`
	SubtitleMode               string   `json:"SubtitleMode"`
	OrderedViews               []string `json:"OrderedViews"`
	LatestItemsExcludes        []string `json:"LatestItemsExcludes"`
	MyMediaExcludes            []string `json:"MyMediaExcludes"`
	HidePlayedInLatest         bool     `json:"HidePlayedInLatest"`
	HidePlayedInMoreLikeThis   bool     `json:"HidePlayedInMoreLikeThis"`
	HidePlayedInSuggestions    bool     `json:"HidePlayedInSuggestions"`
	RememberAudioSelections    bool     `json:"RememberAudioSelections"`
	RememberSubtitleSelections bool     `json:"RememberSubtitleSelections"`
	EnableNextEpisodeAutoPlay  bool     `json:"EnableNextEpisodeAutoPlay"`
	ResumeRewindSeconds        int      `json:"ResumeRewindSeconds"`
	IntroSkipMode              string   `json:"IntroSkipMode"`
	EnableLocalPassword        bool     `json:"EnableLocalPassword"`
}

type embyUserPolicyDTO struct {
	IsAdministrator                  bool     `json:"IsAdministrator"`
	IsHidden                         bool     `json:"IsHidden"`
	IsHiddenRemotely                 bool     `json:"IsHiddenRemotely"`
	IsHiddenFromUnusedDevices        bool     `json:"IsHiddenFromUnusedDevices"`
	IsDisabled                       bool     `json:"IsDisabled"`
	LockedOutDate                    int      `json:"LockedOutDate"`
	AllowTagOrRating                 bool     `json:"AllowTagOrRating"`
	BlockedTags                      []string `json:"BlockedTags"`
	IsTagBlockingModeInclusive       bool     `json:"IsTagBlockingModeInclusive"`
	IncludeTags                      []string `json:"IncludeTags"`
	EnableUserPreferenceAccess       bool     `json:"EnableUserPreferenceAccess"`
	AccessSchedules                  []any    `json:"AccessSchedules"`
	BlockUnratedItems                []any    `json:"BlockUnratedItems"`
	EnableRemoteControlOfOtherUsers  bool     `json:"EnableRemoteControlOfOtherUsers"`
	EnableSharedDeviceControl        bool     `json:"EnableSharedDeviceControl"`
	EnableRemoteAccess               bool     `json:"EnableRemoteAccess"`
	EnableLiveTvManagement           bool     `json:"EnableLiveTvManagement"`
	EnableLiveTvAccess               bool     `json:"EnableLiveTvAccess"`
	EnableMediaPlayback              bool     `json:"EnableMediaPlayback"`
	EnableAudioPlaybackTranscoding   bool     `json:"EnableAudioPlaybackTranscoding"`
	EnableVideoPlaybackTranscoding   bool     `json:"EnableVideoPlaybackTranscoding"`
	AutoRemoteQuality                int      `json:"AutoRemoteQuality"`
	EnablePlaybackRemuxing           bool     `json:"EnablePlaybackRemuxing"`
	EnableContentDeletion            bool     `json:"EnableContentDeletion"`
	RestrictedFeatures               []string `json:"RestrictedFeatures"`
	EnableContentDeletionFromFolders []string `json:"EnableContentDeletionFromFolders"`
	EnableContentDownloading         bool     `json:"EnableContentDownloading"`
	EnableSubtitleDownloading        bool     `json:"EnableSubtitleDownloading"`
	EnableSubtitleManagement         bool     `json:"EnableSubtitleManagement"`
	EnableSyncTranscoding            bool     `json:"EnableSyncTranscoding"`
	EnableMediaConversion            bool     `json:"EnableMediaConversion"`
	EnabledChannels                  []string `json:"EnabledChannels"`
	EnableAllChannels                bool     `json:"EnableAllChannels"`
	EnabledFolders                   []string `json:"EnabledFolders"`
	EnableAllFolders                 bool     `json:"EnableAllFolders"`
	InvalidLoginAttemptCount         int      `json:"InvalidLoginAttemptCount"`
	EnablePublicSharing              bool     `json:"EnablePublicSharing"`
	RemoteClientBitrateLimit         int      `json:"RemoteClientBitrateLimit"`
	AuthenticationProviderID         string   `json:"AuthenticationProviderId"`
	ExcludedSubFolders               []string `json:"ExcludedSubFolders"`
	SimultaneousStreamLimit          int      `json:"SimultaneousStreamLimit"`
	EnabledDevices                   []string `json:"EnabledDevices"`
	EnableAllDevices                 bool     `json:"EnableAllDevices"`
	AllowCameraUpload                bool     `json:"AllowCameraUpload"`
	AllowSharingPersonalItems        bool     `json:"AllowSharingPersonalItems"`
}

type embySessionInfoDTO struct {
	PlayState             embyPlayStateDTO `json:"PlayState"`
	AdditionalUsers       []any            `json:"AdditionalUsers"`
	RemoteEndPoint        string           `json:"RemoteEndPoint"`
	Protocol              string           `json:"Protocol"`
	PlayableMediaTypes    []string         `json:"PlayableMediaTypes"`
	PlaylistIndex         int              `json:"PlaylistIndex"`
	PlaylistLength        int              `json:"PlaylistLength"`
	ID                    string           `json:"Id"`
	ServerID              string           `json:"ServerId"`
	UserID                string           `json:"UserId"`
	UserName              string           `json:"UserName"`
	Client                string           `json:"Client"`
	LastActivityDate      string           `json:"LastActivityDate"`
	DeviceName            string           `json:"DeviceName"`
	InternalDeviceID      int              `json:"InternalDeviceId"`
	DeviceID              string           `json:"DeviceId"`
	ApplicationVersion    string           `json:"ApplicationVersion"`
	SupportedCommands     []string         `json:"SupportedCommands"`
	SupportsRemoteControl bool             `json:"SupportsRemoteControl"`
}

type embyPlayStateDTO struct {
	CanSeek        bool    `json:"CanSeek"`
	IsPaused       bool    `json:"IsPaused"`
	IsMuted        bool    `json:"IsMuted"`
	RepeatMode     string  `json:"RepeatMode"`
	SleepTimerMode string  `json:"SleepTimerMode"`
	SubtitleOffset int     `json:"SubtitleOffset"`
	Shuffle        bool    `json:"Shuffle"`
	PlaybackRate   float64 `json:"PlaybackRate"`
}
