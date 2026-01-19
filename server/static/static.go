package static

import (
	"io"
	"io/fs"
	"net/http"
	"os"
	"path"
	"strconv"
	"strings"
	"time"

	"github.com/jenfonro/TV_Server/internal/auth"
	"github.com/jenfonro/TV_Server/public"
)

var dist fs.FS

func init() {
	sub, err := fs.Sub(public.Dist, "dist")
	if err == nil {
		dist = sub
	}
}

func Handler(authMw *auth.Auth) http.Handler {
	assetVersion := strings.TrimSpace(os.Getenv("ASSET_VERSION"))
	if assetVersion == "" || strings.EqualFold(assetVersion, "timestamp") {
		assetVersion = strconv.FormatInt(time.Now().UnixNano()/int64(time.Millisecond), 10)
	}

	indexHTML := mustReadFile("index.html")
	dashboardHTML := mustReadFile("dashboard.html")
	indexHTML = strings.ReplaceAll(indexHTML, "__ASSET_VERSION__", assetVersion)
	dashboardHTML = strings.ReplaceAll(dashboardHTML, "__ASSET_VERSION__", assetVersion)

	fsHandler := http.FileServer(http.FS(dist))

	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if dist == nil {
			http.Error(w, "dist not found", http.StatusInternalServerError)
			return
		}

		switch r.URL.Path {
		case "/":
			w.Header().Set("Cache-Control", "no-store")
			w.Header().Set("Content-Type", "text/html; charset=utf-8")
			_, _ = io.WriteString(w, indexHTML)
			return
		case "/dashboard", "/dashboard/":
			auth.RequireLoginPage(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				w.Header().Set("Cache-Control", "no-store")
				w.Header().Set("Content-Type", "text/html; charset=utf-8")
				_, _ = io.WriteString(w, dashboardHTML)
			})).ServeHTTP(w, r)
			return
		case "/logout":
			http.Redirect(w, r, "/api/logout", http.StatusFound)
			return
		}

		// Fall back to embedded static dist.
		fsHandler.ServeHTTP(w, r)
	})
}

func NoStoreForHTMLCSSJS(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if next == nil {
			return
		}
		// Set after handler runs, but before write? We can't reliably, so set early for these paths.
		ext := strings.ToLower(path.Ext(r.URL.Path))
		if ext == ".html" || ext == ".css" || ext == ".js" {
			w.Header().Set("Cache-Control", "no-store")
		}
		next.ServeHTTP(w, r)
	})
}

func mustReadFile(name string) string {
	if dist == nil {
		return ""
	}
	f, err := dist.Open(name)
	if err != nil {
		return ""
	}
	defer func() { _ = f.Close() }()
	b, err := io.ReadAll(f)
	if err != nil {
		return ""
	}
	return string(b)
}
