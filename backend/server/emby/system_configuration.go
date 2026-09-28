package emby

import (
	"net/http"

	"github.com/jenfonro/meowfilm/internal/db"
)

func handleSystemConfiguration(w http.ResponseWriter, r *http.Request, database *db.DB) {
	writeEmbyCommonHeaders(w.Header())
	_ = r
	_ = database

	writeJSON(w, http.StatusOK, systemConfigurationResponse{
		EnableUPnP:                           false,
		PublicPort:                           8096,
		PublicHttpsPort:                      8920,
		HttpServerPortNumber:                 8096,
		HttpsPortNumber:                      8920,
		EnableHttps:                          false,
		IsPortAuthorized:                     true,
		AutoRunWebApp:                        true,
		EnableRemoteAccess:                   true,
		LogAllQueryTimes:                     false,
		DisableOutgoingIPv6:                  false,
		EnableCaseSensitiveItemIds:           true,
		MetadataCountryCode:                  "US",
		SortRemoveWords:                      []string{"the", "a", "an", "das", "der", "el", "la"},
		LibraryMonitorDelaySeconds:           90,
		EnableDashboardResponseCaching:       true,
		ImageSavingConvention:                "Compatible",
		EnableAutomaticRestart:               true,
		PreferredDetectedRemoteAddressFamily: "InterNetwork",
		UICulture:                            "zh-CN",
		RemoteClientBitrateLimit:             0,
		LocalNetworkSubnets:                  []string{},
		LocalNetworkAddresses:                []string{},
		EnableExternalContentInSuggestions:   true,
		RequireHttps:                         false,
		IsBehindProxy:                        false,
		RemoteIPFilter:                       []string{},
		IsRemoteIPFilterBlacklist:            false,
		ImageExtractionTimeoutMs:             0,
		PathSubstitutions:                    []string{},
		UninstalledPlugins:                   []string{},
		CollapseVideoFolders:                 false,
		EnableOriginalTrackTitles:            false,
		VacuumDatabaseOnStartup:              false,
		SimultaneousStreamLimit:              0,
		DatabaseCacheSizeMB:                  128,
		EnableSqLiteMmio:                     false,
		PlaylistsUpgradedToM3U:               true,
		ImageExtractorUpgraded1:              true,
		EnablePeopleLetterSubFolders:         true,
		OptimizeDatabaseOnShutdown:           true,
		DatabaseAnalysisLimit:                400,
		MaxLibraryDatabaseConnections:        5,
		MaxAuthDbConnections:                 5,
		MaxOtherDbConnections:                3,
		DisableAsyncIO:                       false,
		MigratedToUserItemShares8:            true,
		MigratedLibraryOptionsToDb:           true,
		AllowLegacyLocalNetworkPassword:      false,
		EnableSavedMetadataForPeople:         false,
		TvChannelsRefreshed:                  true,
		ProxyHeaderMode:                      "AllAddresses",
		IsInMaintenanceMode:                  false,
		EnableDebugLevelLogging:              false,
		EnableAutoUpdate:                     true,
		LogFileRetentionDays:                 3,
		RunAtStartup:                         true,
		IsStartupWizardCompleted:             true,
	})
}
