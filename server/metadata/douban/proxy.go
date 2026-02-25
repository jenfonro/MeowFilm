package douban

import (
	"net/url"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
)

// APIBase returns Douban base URL and an optional proxyBase for CORS/relay modes.
// Behavior matches legacy MeowFilm logic used by Emby and API.
func APIBase(database *db.DB) (base string, proxyBase string) {
	if database == nil {
		return "https://m.douban.com", ""
	}
	cfg, err := database.ReadAppConfig()
	if err != nil {
		return "https://m.douban.com", ""
	}
	mode := strings.TrimSpace(cfg.DoubanDataProxy)
	custom := strings.TrimSpace(cfg.DoubanDataCustom)
	switch mode {
	case "server-proxy":
		return "https://m.douban.com", ""
	case "cdn-tx", "cmliussss-cdn-tencent":
		return "https://m.douban.cmliussss.net", ""
	case "cdn-ali", "cmliussss-cdn-ali":
		return "https://m.douban.cmliussss.com", ""
	case "cors", "cors-proxy-zwei", "ciao-cors":
		return "https://m.douban.com", "https://ciao-cors.is-an.org/"
	case "cors-anywhere":
		return "https://m.douban.com", "https://cors-anywhere.com/"
	case "custom":
		if custom != "" {
			return "https://m.douban.com", strings.TrimSpace(custom)
		}
		return "https://m.douban.com", ""
	default:
		return "https://m.douban.com", ""
	}
}

// ToProxiedURL converts a Douban target URL into a proxied URL using proxyBase.
// Behavior matches frontend:
// - cors-anywhere: proxyBase + targetUrl
// - others: proxyBase + encodeURIComponent(targetUrl)
func ToProxiedURL(targetURL string, proxyBase string) string {
	t := strings.TrimSpace(targetURL)
	p := strings.TrimSpace(proxyBase)
	if t == "" || p == "" {
		return t
	}
	if !strings.HasSuffix(p, "/") && !strings.HasSuffix(p, "?") && !strings.HasSuffix(p, "&") && !strings.HasSuffix(p, "=") {
		p = p + "/"
	}
	if strings.Contains(p, "cors-anywhere.com/") {
		return p + t
	}
	return p + url.PathEscape(t)
}

func IsAllowedImageHost(hostname string) bool {
	host := strings.ToLower(strings.TrimSpace(hostname))
	if host == "" {
		return false
	}
	isDigitSuffix := func(suffix string) bool {
		if !strings.HasPrefix(host, "img") || !strings.HasSuffix(host, suffix) {
			return false
		}
		mid := strings.TrimSuffix(strings.TrimPrefix(host, "img"), suffix)
		if mid == "" {
			return false
		}
		for i := 0; i < len(mid); i++ {
			if mid[i] < '0' || mid[i] > '9' {
				return false
			}
		}
		return true
	}
	if isDigitSuffix(".doubanio.com") || isDigitSuffix(".douban.com") {
		return true
	}
	switch host {
	case "img.doubanio.com", "img.douban.com", "img3.doubanio.com", "img.doubanio.cmliussss.net", "img.doubanio.cmliussss.com":
		return true
	default:
		return false
	}
}
