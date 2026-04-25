package api

import (
	"net/http"

	"github.com/jenfonro/meowfilm/internal/auth"
	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/netdisk"
)

func handleAPIPanRoutes(path string, w http.ResponseWriter, r *http.Request, database *db.DB, authMw *auth.Auth) bool {
	serveProtected := func(handler func(http.ResponseWriter, *http.Request, *db.DB)) {
		authMw.RequireAuthAPI(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			handler(w, r, database)
		})).ServeHTTP(w, r)
	}
	switch path {
	case "/pan/189/list":
		serveProtected(netdisk.HandleAPI189List)
	case "/pan/189/play":
		serveProtected(netdisk.HandleAPI189Play)
	case "/pan/139/list":
		serveProtected(netdisk.HandleAPI139List)
	case "/pan/139/play":
		serveProtected(netdisk.HandleAPI139Play)
	case "/pan/quark/list":
		serveProtected(netdisk.HandleAPIQuarkList)
	case "/pan/quark/status":
		serveProtected(netdisk.HandleAPIQuarkStatus)
	case "/pan/quark_tv/refresh":
		serveProtected(netdisk.HandleAPIQuarkTVRefresh)
	case "/pan/quark/play":
		serveProtected(netdisk.HandleAPIQuarkPlay)
	case "/pan/relay/resolve":
		netdisk.HandleAPIRelayResolve(w, r, database)
	case "/pan/uc/list":
		serveProtected(netdisk.HandleAPIUCList)
	case "/pan/uc/play":
		serveProtected(netdisk.HandleAPIUCPlay)
	case "/pan/uc_tv/refresh":
		serveProtected(netdisk.HandleAPIUCTVRefresh)
	case "/pan/baidu/list":
		serveProtected(netdisk.HandleAPIBaiduList)
	case "/pan/baidu/play":
		serveProtected(netdisk.HandleAPIBaiduPlay)
	default:
		return false
	}
	return true
}
