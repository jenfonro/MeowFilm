package dashboard

import (
	"net/http"
	"strings"

	"github.com/jenfonro/meowfilm/internal/auth"
	"github.com/jenfonro/meowfilm/internal/db"
)

func Handler(database *db.DB, authMw *auth.Auth) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		path := strings.TrimPrefix(r.URL.Path, "/dashboard")
		if handleDashboardPanRoutes(path, w, r, database, authMw) {
			return
		}
		if handleDashboardMainRoutes(path, w, r, database, authMw) {
			return
		}
		http.NotFound(w, r)
	})
}

func handleDashboardMainRoutes(path string, w http.ResponseWriter, r *http.Request, database *db.DB, authMw *auth.Auth) bool {
	serveAdminDB := func(handler func(http.ResponseWriter, *http.Request, *db.DB)) {
		authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			handler(w, r, database)
		})).ServeHTTP(w, r)
	}
	switch path {
	case "/backup":
		serveAdminDB(handleDashboardBackup)
	case "/restore":
		serveAdminDB(handleDashboardRestore)
	case "/site/save":
		serveAdminDB(handleDashboardSiteSave)
	case "/catpawrunner/save":
		serveAdminDB(handleDashboardcatpawrunnerSave)
	case "/catpawrunner/delete":
		serveAdminDB(handleDashboardcatpawrunnerDelete)
	case "/site/settings":
		serveAdminDB(handleDashboardSiteSettings)
	case "/goproxy/save":
		serveAdminDB(handleDashboardGoProxySave)
	case "/relay/save":
		serveAdminDB(handleDashboardRelaySave)
	case "/video/pans/list":
		serveAdminDB(handleDashboardVideoPansList)
	case "/video/source/save":
		serveAdminDB(handleDashboardVideoSourceSave)
	case "/video/source/settings":
		serveAdminDB(handleDashboardVideoSourceSettings)
	case "/video/source/sites":
		serveAdminDB(handleDashboardVideoSourceSites)
	case "/video/source/sites/status":
		serveAdminDB(handleDashboardVideoSourceSiteStatus)
	case "/video/source/sites/home":
		serveAdminDB(handleDashboardVideoSourceSiteHome)
	case "/video/source/sites/search":
		serveAdminDB(handleDashboardVideoSourceSiteSearch)
	case "/video/source/sites/cover":
		serveAdminDB(handleDashboardVideoSourceCoverSite)
	case "/video/source/sites/order":
		serveAdminDB(handleDashboardVideoSourceSiteOrder)
	case "/video/source/sites/check":
		serveAdminDB(handleDashboardVideoSourceSitesCheck)
	case "/video/source/sites/import":
		serveAdminDB(handleDashboardVideoSourceSitesImport)
	case "/magic/settings":
		serveAdminDB(handleDashboardMagicSettings)
	case "/smart/settings":
		serveAdminDB(handleDashboardSmartSettings)
	case "/smart/matchblock/keywords":
		serveAdminDB(handleDashboardSmartMatchBlockKeywords)
	case "/smart/matchblock/items":
		serveAdminDB(handleDashboardSmartMatchBlockItems)
	case "/smart/matchblock/delete":
		serveAdminDB(handleDashboardSmartMatchBlockDelete)
	case "/smart/matchblock/keyword/delete":
		serveAdminDB(handleDashboardSmartMatchBlockKeywordDelete)
	case "/metadata/settings":
		serveAdminDB(handleDashboardMetadataSettings)
	case "/metadata/cache/clear":
		serveAdminDB(handleDashboardMetadataCacheClear)
	case "/thirdparty/settings":
		serveAdminDB(handleDashboardThirdPartySettings)
	case "/thirdparty/save":
		serveAdminDB(handleDashboardThirdPartySave)
	case "/thirdparty/site/categories":
		serveAdminDB(handleDashboardThirdPartySiteCategories)
	case "/user/list":
		serveAdminDB(handleDashboardUserList)
	case "/user/add":
		serveAdminDB(handleDashboardUserAdd)
	case "/user/ban":
		serveAdminDB(handleDashboardUserBan)
	case "/user/delete":
		serveAdminDB(handleDashboardUserDelete)
	case "/user/update":
		serveAdminDB(handleDashboardUserUpdate)
	default:
		return false
	}
	return true
}

func handleDashboardVideoSourceSettings(w http.ResponseWriter, r *http.Request, _ *db.DB) {
	if r.Method != http.MethodGet {
		methodNotAllowed(w)
		return
	}
	writeJSON(w, 200, map[string]any{"success": true, "videoSourceUrl": ""})
}

func handleDashboardVideoSourceSites(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodGet {
		methodNotAllowed(w)
		return
	}
	sites := mergeVideoSourceSites(database)
	cfg, _ := database.ReadAppConfig()
	cover := resolveSearchCoverSite(sites, cfg.VideoSourceSearchCoverSite)
	writeJSON(w, 200, map[string]any{"success": true, "sites": sites, "coverSite": cover})
}
