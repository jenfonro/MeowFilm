package emby

type embyDisplayPreferencesUserSettingsDTO struct {
	ID          string         `json:"Id"`
	CustomPrefs map[string]any `json:"CustomPrefs"`
	SortOrder   string         `json:"SortOrder"`
	Client      string         `json:"Client"`
}
