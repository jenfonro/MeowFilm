package dashboard

import (
	"net/http"

	"github.com/jenfonro/meowfilm/internal/auth"
	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/netdisk"
)

func handleDashboardPanRoutes(path string, w http.ResponseWriter, r *http.Request, database *db.DB, authMw *auth.Auth) bool {
	serveAdminDB := func(handler func(http.ResponseWriter, *http.Request, *db.DB)) {
		authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			handler(w, r, database)
		})).ServeHTTP(w, r)
	}
	serveAdmin := func(handler func(http.ResponseWriter, *http.Request)) {
		authMw.RequireAdmin(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			handler(w, r)
		})).ServeHTTP(w, r)
	}
	switch path {
	case "/pan/settings":
		serveAdminDB(netdisk.HandleDashboardPanSettings)
	case "/pan/baidu/start":
		serveAdminDB(netdisk.HandleDashboardBaiduStart)
	case "/pan/baidu/image":
		serveAdmin(netdisk.HandleDashboardBaiduImage)
	case "/pan/baidu/cookie":
		serveAdminDB(netdisk.HandleDashboardBaiduCookie)
	case "/pan/quark/start":
		serveAdminDB(netdisk.HandleDashboardQuarkStart)
	case "/pan/quark/image":
		serveAdmin(netdisk.HandleDashboardQuarkImage)
	case "/pan/quark/cookie":
		serveAdminDB(netdisk.HandleDashboardQuarkCookie)
	case "/pan/uc/start":
		serveAdminDB(netdisk.HandleDashboardUCStart)
	case "/pan/uc/image":
		serveAdmin(netdisk.HandleDashboardUCImage)
	case "/pan/uc/cookie":
		serveAdminDB(netdisk.HandleDashboardUCCookie)
	case "/pan/115/start":
		serveAdminDB(netdisk.HandleDashboard115Start)
	case "/pan/115/image":
		serveAdmin(netdisk.HandleDashboard115Image)
	case "/pan/115/cookie":
		serveAdminDB(netdisk.HandleDashboard115Cookie)
	case "/pan/bili/start":
		serveAdminDB(netdisk.HandleDashboardBiliStart)
	case "/pan/bili/image":
		serveAdmin(netdisk.HandleDashboardBiliImage)
	case "/pan/bili/cookie":
		serveAdminDB(netdisk.HandleDashboardBiliCookie)
	default:
		return false
	}
	return true
}
