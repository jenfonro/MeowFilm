package routes

import (
	"net/url"
	"strings"
)

const (
	embyPathPrefix       = "/emby"
	embyLegacyPathPrefix = "/jellyfin"
	embyMediaPrefix      = "/emby/media/"
)

func embyTrimAPIPrefix(path string) string {
	p := strings.TrimSpace(path)
	if p == "" {
		return "/"
	}
	if p == embyPathPrefix || strings.HasPrefix(p, embyPathPrefix+"/") {
		p = strings.TrimPrefix(p, embyPathPrefix)
	} else if p == embyLegacyPathPrefix || strings.HasPrefix(p, embyLegacyPathPrefix+"/") {
		p = strings.TrimPrefix(p, embyLegacyPathPrefix)
	}
	if p == "" {
		p = "/"
	}
	if !strings.HasPrefix(p, "/") {
		p = "/" + p
	}
	return p
}

func embyBuildMediaPath(itemID string, ext string) string {
	e := strings.TrimSpace(ext)
	if e == "" {
		e = "mp4"
	}
	e = strings.TrimPrefix(e, ".")
	return embyMediaPrefix + url.PathEscape(strings.TrimSpace(itemID)) + "." + url.PathEscape(e)
}
