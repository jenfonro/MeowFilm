package emby

import (
	"net/http"
	"strings"
)

func embyParseAuthQuotedKV(raw string, key string) string {
	s := strings.TrimSpace(raw)
	k := strings.TrimSpace(key)
	if s == "" || k == "" {
		return ""
	}
	needle := k + "=\""
	i := strings.Index(s, needle)
	if i < 0 {
		return ""
	}
	j := i + len(needle)
	if j >= len(s) {
		return ""
	}
	end := strings.IndexByte(s[j:], '"')
	if end < 0 {
		return ""
	}
	return strings.TrimSpace(s[j : j+end])
}

func embyClientDeviceID(r *http.Request) string {
	if r == nil {
		return ""
	}
	// Common explicit device id headers.
	// (Different clients use different casings; Go normalizes header keys.)
	if id := embyHeaderTrim(r, "X-Emby-Device-Id"); id != "" {
		return id
	}
	if id := embyHeaderTrim(r, "X-Emby-DeviceId"); id != "" {
		return id
	}
	if id := embyHeaderTrim(r, "X-Jellyfin-Device-Id"); id != "" {
		return id
	}
	if id := embyHeaderTrim(r, "X-Device-Id"); id != "" {
		return id
	}

	// Prefer Emby auth header if present.
	if v := embyHeaderTrim(r, "X-Emby-Authorization"); v != "" {
		if id := embyParseAuthQuotedKV(v, "DeviceId"); id != "" {
			return id
		}
	}
	// Jellyfin/Infuse commonly uses Authorization: MediaBrowser ...
	if v := embyHeaderTrim(r, "Authorization"); v != "" {
		if id := embyParseAuthQuotedKV(v, "DeviceId"); id != "" {
			return id
		}
	}

	// Some clients pass DeviceId as a query param.
	if id := embyQueryTrimCI(r, "DeviceId"); id != "" {
		return id
	}
	if id := embyQueryTrimCI(r, "deviceId"); id != "" {
		return id
	}
	return ""
}

func embyIsInfuseClient(r *http.Request) bool {
	if r == nil {
		return false
	}
	ua := strings.ToLower(embyHeaderTrim(r, "User-Agent"))
	if strings.Contains(ua, "infuse") {
		return true
	}
	auth := strings.ToLower(embyHeaderTrim(r, "Authorization"))
	if strings.Contains(auth, "infuse") {
		return true
	}
	// Some clients only send the Emby authorization header.
	xauth := strings.ToLower(embyHeaderTrim(r, "X-Emby-Authorization"))
	if strings.Contains(xauth, "infuse") {
		return true
	}
	return false
}
