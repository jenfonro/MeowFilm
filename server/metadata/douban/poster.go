package douban

import (
	"net/url"

	mfnet "github.com/jenfonro/meowfilm/server/net"
)

func NormalizeProxyBase(value string) string {
	return mfnet.NormalizeProxyBase(value)
}

func NormalizeImageURL(value string) string {
	return mfnet.NormalizeImageURL(value)
}

func NormalizeProxyMode(value string) string {
	return mfnet.NormalizeProxyMode(value)
}

// RewriteVideoPosterURL rewrites a Douban image URL according to configured proxy mode.
// Behavior is intentionally kept in sync with historical /api rewriting logic.
func RewriteVideoPosterURL(value string, doubanImgProxy string, doubanImgCustom string) string {
	original := NormalizeImageURL(value)
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
	if !IsAllowedImageHost(parsed.Hostname()) {
		return original
	}

	mode := CanonicalImageProxyMode(doubanImgProxy)
	switch mode {
	case "server-proxy":
		return "/api/douban/image?url=" + url.QueryEscape(original)
	case "custom":
		base := NormalizeProxyBase(doubanImgCustom)
		if base == "" {
			return original
		}
		return base + url.QueryEscape(original)
	case "douban-cdn-ali":
		parsed.Scheme = "https"
		parsed.Host = "img3.doubanio.com"
		return parsed.String()
	case "cdn-tx":
		parsed.Scheme = "https"
		parsed.Host = "img.doubanio.cmliussss.net"
		return parsed.String()
	case "cdn-ali":
		parsed.Scheme = "https"
		parsed.Host = "img.doubanio.cmliussss.com"
		return parsed.String()
	default:
		return original
	}
}
