package static

import (
	"fmt"
	"io"
	"io/fs"
	"net/http"
	"os"
	"path"
	"strconv"
	"strings"
	"time"

	"github.com/jenfonro/meowfilm/internal/auth"
	"github.com/jenfonro/meowfilm/public"
)

var dist fs.FS

// BuildAssetVersion can be set at build time with:
//
//	go build -ldflags "-X github.com/jenfonro/meowfilm/server/static.BuildAssetVersion=v1.0.0"
//
// If ASSET_VERSION is set at runtime, it takes precedence over this value.
var BuildAssetVersion string
var BuildBackendCommit string
var BuildFrontendCommit string

// ServerVersion returns the current server version string for logs/diagnostics.
// It resolves the same release version source as the UI embedding:
// - runtime env `ASSET_VERSION` (highest priority)
// - build-time `BuildAssetVersion` (set via -ldflags)
// and falls back to "beta" when not a release build.
func ServerVersion() string {
	rawVersion := strings.TrimSpace(os.Getenv("ASSET_VERSION"))
	if rawVersion == "" {
		rawVersion = strings.TrimSpace(BuildAssetVersion)
	}
	semver := normalizeReleaseSemver(rawVersion)
	if semver == "" {
		return "beta"
	}
	return semver
}

func init() {
	sub, err := fs.Sub(public.Dist, "dist")
	if err == nil {
		dist = sub
	}
}

func Handler(authMw *auth.Auth) http.Handler {
	rawVersion := strings.TrimSpace(os.Getenv("ASSET_VERSION"))
	if rawVersion == "" {
		rawVersion = strings.TrimSpace(BuildAssetVersion)
	}
	semver := normalizeReleaseSemver(rawVersion)
	backendCommit := strings.TrimSpace(BuildBackendCommit)
	frontendCommit := strings.TrimSpace(BuildFrontendCommit)

	uiVersion := "beta"
	// In local/dev builds we want a stable "beta-<timestamp>" that is computed once per process,
	// not a per-request value (which can make caching/debugging confusing).
	betaStamp := strconv.FormatInt(time.Now().UnixNano()/int64(time.Millisecond), 10)
	assetVersion := betaStamp
	if semver != "" {
		uiVersion = "V" + semver
		assetVersion = semver
	} else {
		uiVersion = fmt.Sprintf("beta-%s", betaStamp)
	}
	if backendCommit == "" {
		backendCommit = uiVersion
	}
	if frontendCommit == "" {
		frontendCommit = uiVersion
	}

	indexHTML := mustReadFile("index.html")
	dashboardHTML := mustReadFile("dashboard.html")
	indexHTML = patchUiVersionPlaceholders(indexHTML)
	dashboardHTML = patchUiVersionPlaceholders(dashboardHTML)
	indexHTML = strings.ReplaceAll(indexHTML, "__ASSET_VERSION__", assetVersion)
	indexHTML = strings.ReplaceAll(indexHTML, "__UI_VERSION__", uiVersion)
	indexHTML = strings.ReplaceAll(indexHTML, "__BACKEND_COMMIT__", backendCommit)
	indexHTML = strings.ReplaceAll(indexHTML, "__FRONTEND_COMMIT__", frontendCommit)
	dashboardHTML = strings.ReplaceAll(dashboardHTML, "__ASSET_VERSION__", assetVersion)
	dashboardHTML = strings.ReplaceAll(dashboardHTML, "__UI_VERSION__", uiVersion)
	dashboardHTML = strings.ReplaceAll(dashboardHTML, "__BACKEND_COMMIT__", backendCommit)
	dashboardHTML = strings.ReplaceAll(dashboardHTML, "__FRONTEND_COMMIT__", frontendCommit)

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

func patchUiVersionPlaceholders(html string) string {
	if !strings.Contains(html, "__ASSET_VERSION__") && !strings.Contains(html, "__UI_VERSION__") {
		return html
	}
	r := strings.NewReplacer(
		"window.__MEOWFILM_VERSION__ = '__ASSET_VERSION__';", "window.__MEOWFILM_VERSION__ = '__UI_VERSION__';",
		"window.__MEOWFILM_VERSION__='__ASSET_VERSION__';", "window.__MEOWFILM_VERSION__='__UI_VERSION__';",
		"window.__MEOWFILM_VERSION__ = \"__ASSET_VERSION__\";", "window.__MEOWFILM_VERSION__ = \"__UI_VERSION__\";",
		"window.__MEOWFILM_VERSION__=\"__ASSET_VERSION__\";", "window.__MEOWFILM_VERSION__=\"__UI_VERSION__\";",
	)
	return r.Replace(html)
}

func normalizeReleaseSemver(raw string) string {
	s := strings.TrimSpace(raw)
	if s == "" {
		return ""
	}
	s = strings.TrimPrefix(s, "refs/tags/")
	s = strings.TrimSpace(s)
	if s == "" {
		return ""
	}

	low := strings.ToLower(s)
	if low == "timestamp" || low == "beta" {
		return ""
	}

	// Accept "v1.2.3", "V1.2.3" and "1.2.3".
	if strings.HasPrefix(low, "v") {
		s = s[1:]
	}
	s = strings.TrimSpace(s)
	if s == "" {
		return ""
	}

	// Very lightweight validation: ensure it starts with a digit.
	if s[0] < '0' || s[0] > '9' {
		return ""
	}
	return s
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
