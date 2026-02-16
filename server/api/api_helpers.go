package api

import (
	"net/url"
	"strings"

	"github.com/jenfonro/meowfilm/server/metadata/douban"
	mfnet "github.com/jenfonro/meowfilm/server/net"
)

type goProxyServer struct {
	Name        string      `json:"name"`
	DisplayName string      `json:"displayName"`
	Base        string      `json:"base"`
	Pans        goProxyPans `json:"pans"`
}

type goProxyPans struct {
	Baidu bool `json:"baidu"`
	Quark bool `json:"quark"`
}

func normalizeGoProxyServers(value string) []goProxyServer {
	servers := mfnet.NormalizeGoProxyServers(value)
	out := make([]goProxyServer, 0, len(servers))
	for _, s := range servers {
		out = append(out, goProxyServer{
			Name:        s.Name,
			DisplayName: s.DisplayName,
			Base:        s.Base,
			Pans:        goProxyPans{Baidu: s.Pans.Baidu, Quark: s.Pans.Quark},
		})
	}
	return out
}

func normalizeContentKey(s string) string {
	return strings.TrimSpace(strings.ToLower(strings.Join(strings.Fields(s), "")))
}

func defaultString(v, def string) string {
	return mfnet.DefaultString(v, def)
}

func normalizeHTTPBase(value string) string {
	return mfnet.NormalizeHTTPBase(value)
}

func isAllowedDoubanImageHost(hostname string) bool {
	return douban.IsAllowedImageHost(hostname)
}

func normalizeProxyBase(value string) string {
	return mfnet.NormalizeProxyBase(value)
}

func normalizeImageURL(value string) string {
	return mfnet.NormalizeImageURL(value)
}

func normalizeProxyMode(value string) string {
	return mfnet.NormalizeProxyMode(value)
}

func rewriteVideoPosterURL(value string, doubanImgProxy string, doubanImgCustom string) string {
	original := normalizeImageURL(value)
	if original == "" {
		return ""
	}
	parsed, err := url.Parse(original)
	if err != nil || parsed.Host == "" {
		return original
	}
	if parsed.Scheme != "" && parsed.Scheme != "http" && parsed.Scheme != "https" {
		return original
	}
	if !isAllowedDoubanImageHost(parsed.Hostname()) {
		return original
	}

	mode := normalizeProxyMode(doubanImgProxy)
	if mode == "" {
		mode = "direct-browser"
	}
	switch mode {
	case "server-proxy":
		return "/api/douban/image?url=" + url.QueryEscape(original)
	case "custom":
		base := normalizeProxyBase(doubanImgCustom)
		if base == "" {
			return original
		}
		return base + url.QueryEscape(original)
	case "douban-cdn-ali", "img3":
		parsed.Scheme = "https"
		parsed.Host = "img3.doubanio.com"
		return parsed.String()
	case "cdn-tx", "cmliussss-cdn-tencent":
		parsed.Scheme = "https"
		parsed.Host = "img.doubanio.cmliussss.net"
		return parsed.String()
	case "cdn-ali", "cmliussss-cdn-ali":
		parsed.Scheme = "https"
		parsed.Host = "img.doubanio.cmliussss.com"
		return parsed.String()
	default:
		return original
	}
}
