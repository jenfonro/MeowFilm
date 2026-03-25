package emby

import (
	"net/http"
	"os"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/static"
)

type systemInfoPublicResponse struct {
	LocalAddress    string   `json:"LocalAddress"`
	LocalAddresses  []string `json:"LocalAddresses"`
	WanAddress      string   `json:"WanAddress"`
	RemoteAddresses []string `json:"RemoteAddresses"`
	ServerName      string   `json:"ServerName"`
	Version         string   `json:"Version"`
	ID              string   `json:"Id"`
}

type systemInfoPublicContext struct {
	ServerID   string
	ServerName string
	BaseURL    string
}

func handleSystemInfoPublic(w http.ResponseWriter, r *http.Request, database *db.DB) {
	writeEmbyCommonHeaders(w.Header())
	ctx, ok := resolveSystemInfoPublicContext(database, r)
	if !ok {
		writeEmbyError(w, http.StatusInternalServerError, "请求失败")
		return
	}
	localAddress := ""
	wanAddress := ""
	localAddresses := []string{}
	remoteAddresses := []string{}
	if ctx.BaseURL != "" {
		localAddress = ctx.BaseURL
		wanAddress = ctx.BaseURL
		localAddresses = []string{ctx.BaseURL}
		remoteAddresses = []string{ctx.BaseURL}
	}

	writeJSON(w, http.StatusOK, systemInfoPublicResponse{
		LocalAddress:    localAddress,
		LocalAddresses:  localAddresses,
		WanAddress:      wanAddress,
		RemoteAddresses: remoteAddresses,
		ServerName:      ctx.ServerName,
		Version:         static.ServerVersion(),
		ID:              ctx.ServerID,
	})
}

func resolveSystemInfoPublicContext(database *db.DB, r *http.Request) (systemInfoPublicContext, bool) {
	publicCtx, ok := resolvePublicMetaContext(database, r)
	if !ok {
		return systemInfoPublicContext{}, false
	}
	serverName, _ := os.Hostname()
	serverName = strings.TrimSpace(serverName)
	if serverName == "" {
		serverName = publicCtx.ServerID
	}
	return systemInfoPublicContext{
		ServerID:   publicCtx.ServerID,
		ServerName: serverName,
		BaseURL:    publicCtx.BaseURL,
	}, true
}

func embyRequestBaseURL(r *http.Request) string {
	if r == nil {
		return ""
	}
	host := strings.TrimSpace(r.Host)
	if host == "" {
		return ""
	}
	scheme := "http"
	if proto := strings.TrimSpace(r.Header.Get("X-Forwarded-Proto")); proto != "" {
		scheme = strings.ToLower(proto)
	} else if r.TLS != nil {
		scheme = "https"
	}
	return scheme + "://" + host
}
