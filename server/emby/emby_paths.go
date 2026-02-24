package emby

import (
	"net/url"
	"strings"
)

const (
	embyPathPrefix       = "/emby"
	embyMediaPrefix      = "/emby/media/"
)

func embyTrimAPIPrefix(path string) string {
	p := strings.TrimSpace(path)
	if p == "" {
		return "/"
	}
	if p == embyPathPrefix || strings.HasPrefix(p, embyPathPrefix+"/") {
		p = strings.TrimPrefix(p, embyPathPrefix)
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
