package emby

import (
	"net/http"
	"runtime"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
)

func handleEmbySystem(w http.ResponseWriter, r *http.Request, database *db.DB, serverID string, parts []string) {
	if len(parts) >= 2 && strings.EqualFold(parts[0], "Info") && strings.EqualFold(parts[1], "Public") && r.Method == http.MethodGet {
		cfg, _ := database.ReadAppConfig()
		siteName := defaultString(cfg.SiteName, "MeowFilm")
		writeJSON(w, 200, map[string]any{
			"ServerName":      siteName,
			"Version":         "10.9.0",
			"ProductName":     "Emby",
			"Id":              serverID,
			"OperatingSystem": runtime.GOOS,
		})
		return
	}
	if len(parts) >= 1 && strings.EqualFold(parts[0], "Configuration") && r.Method == http.MethodGet {
		// Minimal system configuration for clients that probe this endpoint during startup.
		// Keep it stable and permissive; clients typically treat missing fields as false/empty.
		writeJSON(w, 200, map[string]any{
			"EnableUPnP":                            false,
			"EnableRemoteAccess":                    true,
			"ServerName":                            "MeowFilm",
			"PublicPort":                            0,
			"HttpServerPortNumber":                  0,
			"HttpsPortNumber":                       0,
			"RequireHttps":                          false,
			"EnableHttps":                           false,
			"IsPortAuthorized":                      true,
			"EnableAutoDiscovery":                   true,
			"EnableCaseSensitiveItemIds":            false,
			"EnableAnonymousUsageReporting":         false,
			"EnableLocalizedGuids":                  false,
			"DisplaySpecialsWithinSeasons":          true,
			"EnableExternalContentInSuggestions":    true,
			"EnableNewEpisodeNotifications":         false,
			"EnableContentRemovalDuringLibraryScan": false,
			"EnableLibraryMonitor":                  false,
			"LibraryScanFanoutConcurrency":          0,
			"ImageExtractionTimeoutMs":              0,
			"MetadataRefreshOnExit":                 false,
			"MetadataRefreshOnStartup":              false,
			"RemoteClientBitrateLimit":              0,
			"EnableSlowResponseWarnings":            false,
			"SlowResponseWarningThresholdMs":        0,
			"EnableDashVttSubtitleExtraction":       false,
			"EnableHlsVttSubtitleExtraction":        false,
			"EnableMetrics":                         false,
			"EnableLiveTv":                          false,
			// Let clients enable download UI.
			"EnableContentDownloading":                true,
			"EnableTrickplayImageExtraction":          false,
			"EnableTrickplayImageExtractionOnLibrary": false,
		})
		return
	}
	embyNotFound(w)
}
