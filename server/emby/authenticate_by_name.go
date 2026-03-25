package emby

import (
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"net/http"
	"strings"
	"time"

	"golang.org/x/crypto/bcrypt"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/clientmeta"
)

func handleAuthenticateByName(w http.ResponseWriter, r *http.Request, database *db.DB) {
	writeEmbyCommonHeaders(w.Header())
	if database == nil {
		writeEmbyError(w, http.StatusServiceUnavailable, "database unavailable")
		return
	}

	var body struct {
		Username string `json:"Username"`
		Pw       string `json:"Pw"`
		Password string `json:"Password"`
	}
	if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
		writeEmbyError(w, http.StatusBadRequest, "Invalid JSON")
		return
	}

	username := strings.TrimSpace(body.Username)
	password := body.Pw
	if strings.TrimSpace(password) == "" {
		password = body.Password
	}
	if username == "" || strings.TrimSpace(password) == "" {
		writeEmbyError(w, http.StatusBadRequest, "用户名或密码不能为空")
		return
	}

	row, err := database.GetUserAuthByUsername(username)
	if err != nil {
		if isNoRowsErr(err) {
			writeEmbyError(w, http.StatusUnauthorized, "用户名或密码错误")
			return
		}
		writeEmbyError(w, http.StatusInternalServerError, "请求失败")
		return
	}
	if strings.TrimSpace(row.Status) != "active" {
		writeEmbyError(w, http.StatusForbidden, "该账户已禁用")
		return
	}
	if bcrypt.CompareHashAndPassword([]byte(row.PasswordHash), []byte(password)) != nil {
		writeEmbyError(w, http.StatusUnauthorized, "用户名或密码错误")
		return
	}

	token, err := issueHexToken(database, row.ID)
	if err != nil {
		writeEmbyError(w, http.StatusInternalServerError, "请求失败")
		return
	}

	serverID, err := database.EnsureServerIdentity()
	if err != nil || strings.TrimSpace(serverID) == "" {
		writeEmbyError(w, http.StatusInternalServerError, "请求失败")
		return
	}
	userID, err := database.GetOrCreateUserProtocolIdentity(row.ID, "emby")
	if err != nil || strings.TrimSpace(userID) == "" {
		writeEmbyError(w, http.StatusInternalServerError, "请求失败")
		return
	}

	clientMeta := clientmeta.ResolveRequestClientMeta(r)
	now := time.Now().UTC()
	remoteIP := clientmeta.NormalizedRemoteIP(r)
	deviceID := clientMeta.DeviceID
	sessionID := clientmeta.StableHexID(clientmeta.StableSessionSeed(token, userID, deviceID, remoteIP))
	internalDeviceID := clientmeta.StablePositiveInt(userID, deviceID)
	if deviceID == "" {
		internalDeviceID = clientmeta.StablePositiveInt(userID, clientMeta.Client, remoteIP)
	}

	resp := authenticateByNameResponse{
		User: buildEmbyAuthUser(row, serverID, userID, now),
		SessionInfo: embySessionInfoDTO{
			PlayState:             defaultPlayState(),
			AdditionalUsers:       []any{},
			RemoteEndPoint:        remoteIP,
			Protocol:              clientmeta.Protocol(r),
			PlayableMediaTypes:    []string{},
			PlaylistIndex:         0,
			PlaylistLength:        0,
			ID:                    sessionID,
			ServerID:              serverID,
			UserID:                userID,
			UserName:              row.Username,
			Client:                clientMeta.Client,
			LastActivityDate:      embyTime(now),
			DeviceName:            clientMeta.Device,
			InternalDeviceID:      internalDeviceID,
			DeviceID:              deviceID,
			ApplicationVersion:    clientMeta.Version,
			SupportedCommands:     []string{},
			SupportsRemoteControl: false,
		},
		AccessToken: token,
		ServerID:    serverID,
	}
	writeJSON(w, http.StatusOK, resp)
}

func issueHexToken(database *db.DB, userID int64) (string, error) {
	raw := make([]byte, 16)
	if _, err := rand.Read(raw); err != nil {
		return "", err
	}
	token := hex.EncodeToString(raw)
	expiresAt := time.Now().Add(30 * 24 * time.Hour)
	if err := database.InsertToken(token, userID, expiresAt); err != nil {
		return "", err
	}
	return token, nil
}

func embyTime(t time.Time) string {
	return t.UTC().Format("2006-01-02T15:04:05.0000000Z")
}

func defaultUserConfiguration() embyUserConfigurationDTO {
	return embyUserConfigurationDTO{
		PlayDefaultAudioTrack:      true,
		DisplayMissingEpisodes:     false,
		SubtitleMode:               "Smart",
		OrderedViews:               []string{},
		LatestItemsExcludes:        []string{},
		MyMediaExcludes:            []string{},
		HidePlayedInLatest:         true,
		HidePlayedInMoreLikeThis:   false,
		HidePlayedInSuggestions:    false,
		RememberAudioSelections:    true,
		RememberSubtitleSelections: true,
		EnableNextEpisodeAutoPlay:  true,
		ResumeRewindSeconds:        0,
		IntroSkipMode:              "ShowButton",
		EnableLocalPassword:        false,
	}
}

func defaultUserPolicy(isAdmin bool) embyUserPolicyDTO {
	return embyUserPolicyDTO{
		IsAdministrator:                  isAdmin,
		IsHidden:                         false,
		IsHiddenRemotely:                 true,
		IsHiddenFromUnusedDevices:        false,
		IsDisabled:                       false,
		LockedOutDate:                    0,
		AllowTagOrRating:                 false,
		BlockedTags:                      []string{},
		IsTagBlockingModeInclusive:       false,
		IncludeTags:                      []string{},
		EnableUserPreferenceAccess:       true,
		AccessSchedules:                  []any{},
		BlockUnratedItems:                []any{},
		EnableRemoteControlOfOtherUsers:  true,
		EnableSharedDeviceControl:        true,
		EnableRemoteAccess:               true,
		EnableLiveTvManagement:           true,
		EnableLiveTvAccess:               true,
		EnableMediaPlayback:              true,
		EnableAudioPlaybackTranscoding:   true,
		EnableVideoPlaybackTranscoding:   true,
		AutoRemoteQuality:                0,
		EnablePlaybackRemuxing:           true,
		EnableContentDeletion:            true,
		RestrictedFeatures:               []string{},
		EnableContentDeletionFromFolders: []string{},
		EnableContentDownloading:         true,
		EnableSubtitleDownloading:        true,
		EnableSubtitleManagement:         true,
		EnableSyncTranscoding:            true,
		EnableMediaConversion:            true,
		EnabledChannels:                  []string{},
		EnableAllChannels:                true,
		EnabledFolders:                   []string{},
		EnableAllFolders:                 true,
		InvalidLoginAttemptCount:         0,
		EnablePublicSharing:              true,
		RemoteClientBitrateLimit:         0,
		AuthenticationProviderID:         "Emby.Server.Implementations.Library.DefaultAuthenticationProvider",
		ExcludedSubFolders:               []string{},
		SimultaneousStreamLimit:          0,
		EnabledDevices:                   []string{},
		EnableAllDevices:                 true,
		AllowCameraUpload:                true,
		AllowSharingPersonalItems:        true,
	}
}

func defaultPlayState() embyPlayStateDTO {
	return embyPlayStateDTO{
		CanSeek:        false,
		IsPaused:       false,
		IsMuted:        false,
		RepeatMode:     "RepeatNone",
		SleepTimerMode: "None",
		SubtitleOffset: 0,
		Shuffle:        false,
		PlaybackRate:   1,
	}
}

func isNoRowsErr(err error) bool {
	if err == nil {
		return false
	}
	return strings.Contains(strings.ToLower(err.Error()), "no rows")
}
