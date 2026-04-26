package api

import (
	"net/http"
	"strings"

	"github.com/jenfonro/meowfilm/internal/auth"
	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/metadata/tmdb"
	"github.com/jenfonro/meowfilm/server/search"
)

func Handler(database *db.DB, authMw *auth.Auth) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		path := strings.TrimPrefix(r.URL.Path, "/api")

		// Douban API proxy (rexxar v2): frontend uses same params; backend chooses upstream base.
		if strings.HasPrefix(path, "/douban/rexxar/api/v2/") {
			authMw.RequireAuthAPI(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				handleAPIDoubanRexxarProxy(w, r, database)
			})).ServeHTTP(w, r)
			return
		}
		if handleAPIPanRoutes(path, w, r, database, authMw) {
			return
		}
		if handleAPIMainRoutes(path, w, r, database, authMw) {
			return
		}
		http.NotFound(w, r)
	})
}

func handleAPIMainRoutes(path string, w http.ResponseWriter, r *http.Request, database *db.DB, authMw *auth.Auth) bool {
	serveProtected := func(handler func(http.ResponseWriter, *http.Request, *db.DB)) {
		authMw.RequireAuthAPI(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			handler(w, r, database)
		})).ServeHTTP(w, r)
	}
	serveProtectedHTTP := func(handler http.Handler) {
		authMw.RequireAuthAPI(handler).ServeHTTP(w, r)
	}
	serveProtectedNoDB := func(handler func(http.ResponseWriter, *http.Request)) {
		authMw.RequireAuthAPI(http.HandlerFunc(handler)).ServeHTTP(w, r)
	}

	switch path {
	case "/home":
		serveProtected(handleAPIHome)
	case "/bootstrap":
		handleAPIBootstrap(w, r, database)
	case "/video/sites":
		serveProtected(handleAPIVideoSites)
	case "/login":
		handleAPILogin(w, r, authMw)
	case "/logout":
		handleAPILogout(w, r, authMw)
	case "/searchhistory":
		serveProtectedHTTP(search.HistoryHandler(database))
	case "/playhistory/one":
		serveProtected(handleAPIPlayHistoryOne)
	case "/playhistory":
		serveProtected(handleAPIPlayHistory)
	case "/favorites":
		serveProtected(handleAPIFavorites)
	case "/favorites/status":
		serveProtected(handleAPIFavoritesStatus)
	case "/favorites/toggle":
		serveProtected(handleAPIFavoritesToggle)
	case "/user/sites":
		serveProtected(handleAPIUserSites)
	case "/douban/image":
		serveProtectedNoDB(handleAPIDoubanImage)
	case "/douban/search":
		serveProtected(handleAPIDoubanSearch)
	case "/tmdb/search":
		serveProtected(tmdb.HandleSearch)
	case "/tmdb/resolve":
		serveProtected(handleAPITMDBResolve)
	case "/tmdb/detail":
		serveProtected(handleAPITMDBDetail)
	case "/tmdb/season":
		serveProtected(handleAPITMDBSeason)
	case "/tv/meta":
		serveProtected(handleAPITVMeta)
	case "/smart/matchblock/items":
		serveProtected(handleAPISmartMatchBlockItems)
	case "/smart/matchblock/add":
		serveProtected(handleAPISmartMatchBlockAdd)
	case "/smart/matchblock/delete":
		serveProtected(handleAPISmartMatchBlockDelete)
	case "/smart/manual/tmdb/get-list":
		serveProtected(handleAPIManualTMDBManualList)
	case "/smart/manual/tmdb/add":
		serveProtected(handleAPIManualTMDBAdd)
	case "/smart/manual/tmdb/delete":
		serveProtected(handleAPIManualTMDBDelete)
	case "/smart/manual/item/get-data":
		serveProtected(handleAPIManualItemManualList)
	case "/smart/manual/item/add":
		serveProtected(handleAPIManualItemAdd)
	case "/smart/manual/item/update":
		serveProtected(handleAPIManualItemUpdate)
	case "/smart/manual/item/delete":
		serveProtected(handleAPIManualItemDelete)
	case "/smart/manual/item/report-result":
		serveProtected(handleAPIManualItemReportResult)
	default:
		return false
	}
	return true
}

func handleAPILogin(w http.ResponseWriter, r *http.Request, authMw *auth.Auth) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	parseForm(r)
	username := r.FormValue("username")
	password := r.FormValue("password")
	// support JSON body too
	if strings.TrimSpace(username) == "" && strings.TrimSpace(password) == "" && strings.Contains(r.Header.Get("Content-Type"), "application/json") {
		var body struct {
			Username string `json:"username"`
			Password string `json:"password"`
		}
		_ = readJSONLoose(r, &body)
		username = body.Username
		password = body.Password
	}
	status, msg := authMw.Login(w, username, password)
	if msg != "" {
		writeJSON(w, status, map[string]any{"success": false, "message": msg})
		return
	}
	writeJSON(w, 200, map[string]any{"success": true})
}

func handleAPILogout(w http.ResponseWriter, r *http.Request, authMw *auth.Auth) {
	if r.Method != http.MethodGet {
		methodNotAllowed(w)
		return
	}
	authMw.Logout(w, r)
	http.Redirect(w, r, "/", http.StatusFound)
}
