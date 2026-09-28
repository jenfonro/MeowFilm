package clientmeta

import (
	"crypto/md5"
	"encoding/hex"
	"hash/crc32"
	"net"
	"net/http"
	"strings"
)

type AuthorizationMeta struct {
	Client   string
	Device   string
	DeviceID string
	Version  string
}

type RequestClientMeta struct {
	Client   string
	Device   string
	DeviceID string
	Version  string
}

func ParseQuotedKV(raw string, key string) string {
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
	start := i + len(needle)
	end := strings.IndexByte(s[start:], '"')
	if end < 0 {
		return ""
	}
	return strings.TrimSpace(s[start : start+end])
}

func ParseEmbyAuthorization(r *http.Request) AuthorizationMeta {
	if r == nil {
		return AuthorizationMeta{}
	}
	for _, key := range []string{"X-Emby-Authorization", "Authorization"} {
		if v := strings.TrimSpace(r.Header.Get(key)); v != "" {
			return AuthorizationMeta{
				Client:   ParseQuotedKV(v, "Client"),
				Device:   ParseQuotedKV(v, "Device"),
				DeviceID: ParseQuotedKV(v, "DeviceId"),
				Version:  ParseQuotedKV(v, "Version"),
			}
		}
	}
	return AuthorizationMeta{}
}

func RequestRemoteIP(r *http.Request) string {
	if r == nil {
		return ""
	}
	for _, key := range []string{"CF-Connecting-IP", "X-Forwarded-For", "X-Real-IP"} {
		if raw := strings.TrimSpace(r.Header.Get(key)); raw != "" {
			if key == "X-Forwarded-For" {
				raw = strings.TrimSpace(strings.Split(raw, ",")[0])
			}
			if ip := net.ParseIP(raw); ip != nil {
				return ip.String()
			}
		}
	}
	host, _, err := net.SplitHostPort(strings.TrimSpace(r.RemoteAddr))
	if err != nil {
		host = strings.TrimSpace(r.RemoteAddr)
	}
	if ip := net.ParseIP(host); ip != nil {
		return ip.String()
	}
	return host
}

func NormalizedRemoteIP(r *http.Request) string {
	return strings.TrimSpace(RequestRemoteIP(r))
}

func ClientDeviceID(r *http.Request) string {
	if r == nil {
		return ""
	}
	for _, key := range []string{"X-Emby-Device-Id", "X-Emby-DeviceId", "X-Jellyfin-Device-Id", "X-Device-Id"} {
		if v := strings.TrimSpace(r.Header.Get(key)); v != "" {
			return v
		}
	}
	for _, key := range []string{"X-Emby-Authorization", "Authorization"} {
		if v := strings.TrimSpace(r.Header.Get(key)); v != "" {
			if id := ParseQuotedKV(v, "DeviceId"); id != "" {
				return id
			}
		}
	}
	for _, key := range []string{"DeviceId", "deviceId"} {
		if v := strings.TrimSpace(r.URL.Query().Get(key)); v != "" {
			return v
		}
	}
	return ""
}

func ClientDeviceName(r *http.Request) string {
	if r == nil {
		return ""
	}
	for _, key := range []string{"X-Emby-Device-Name", "X-Device-Name"} {
		if v := strings.TrimSpace(r.Header.Get(key)); v != "" {
			return sanitizeDeviceName(v)
		}
	}
	for _, key := range []string{"X-Emby-Authorization", "Authorization"} {
		if v := strings.TrimSpace(r.Header.Get(key)); v != "" {
			if device := ParseQuotedKV(v, "Device"); device != "" {
				return sanitizeDeviceName(device)
			}
		}
	}
	return ""
}

func ClientName(r *http.Request) string {
	if r == nil {
		return ""
	}
	for _, key := range []string{"X-Emby-Client", "X-Application"} {
		if v := strings.TrimSpace(r.Header.Get(key)); v != "" {
			return v
		}
		if v := strings.TrimSpace(r.URL.Query().Get(key)); v != "" {
			return v
		}
	}
	if meta := ParseEmbyAuthorization(r); strings.TrimSpace(meta.Client) != "" {
		return strings.TrimSpace(meta.Client)
	}
	return ""
}

func ClientVersion(r *http.Request) string {
	if r == nil {
		return ""
	}
	for _, key := range []string{"X-Emby-Client-Version", "X-Application-Version"} {
		if v := strings.TrimSpace(r.Header.Get(key)); v != "" {
			return v
		}
	}
	for _, key := range []string{"X-Emby-Authorization", "Authorization"} {
		if v := strings.TrimSpace(r.Header.Get(key)); v != "" {
			if version := ParseQuotedKV(v, "Version"); version != "" {
				return version
			}
		}
	}
	return ""
}

func Protocol(r *http.Request) string {
	if r == nil {
		return ""
	}
	return strings.TrimSpace(r.Proto)
}

func StableHexID(parts ...string) string {
	h := md5.New()
	for _, part := range parts {
		_, _ = h.Write([]byte(strings.TrimSpace(part)))
		_, _ = h.Write([]byte{0})
	}
	return hex.EncodeToString(h.Sum(nil))
}

func StableSessionSeed(parts ...string) string {
	return strings.Join(parts, "\x00")
}

func StablePositiveInt(parts ...string) int {
	s := strings.Join(parts, "\x00")
	v := int(crc32.ChecksumIEEE([]byte(s)) & 0x7fffffff)
	if v <= 0 {
		return 1
	}
	return v
}

func Prefix(name string) string {
	s := strings.TrimSpace(name)
	if s == "" {
		return ""
	}
	r := []rune(s)[0]
	return strings.ToUpper(string(r))
}

func sanitizeDeviceName(v string) string {
	s := strings.TrimSpace(v)
	if s == "" {
		return ""
	}
	if idx := strings.Index(s, ","); idx > 0 {
		s = s[:idx]
	}
	return strings.TrimSpace(s)
}

func ResolveRequestClientMeta(r *http.Request) RequestClientMeta {
	auth := ParseEmbyAuthorization(r)
	client := strings.TrimSpace(ClientName(r))
	if client == "" {
		client = strings.TrimSpace(auth.Client)
	}
	device := strings.TrimSpace(ClientDeviceName(r))
	if device == "" {
		device = strings.TrimSpace(auth.Device)
	}
	deviceID := strings.TrimSpace(ClientDeviceID(r))
	if deviceID == "" {
		deviceID = strings.TrimSpace(auth.DeviceID)
	}
	version := strings.TrimSpace(ClientVersion(r))
	if version == "" {
		version = strings.TrimSpace(auth.Version)
	}
	return RequestClientMeta{
		Client:   client,
		Device:   device,
		DeviceID: deviceID,
		Version:  version,
	}
}
