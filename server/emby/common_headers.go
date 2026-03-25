package emby

import "net/http"

func writeEmbyCommonHeaders(h http.Header) {
	if h == nil {
		return
	}
	h.Set("Access-Control-Allow-Origin", "*")
	h.Set("Access-Control-Allow-Headers", "Accept, Accept-Language, Authorization, Cache-Control, Content-Disposition, Content-Encoding, Content-Language, Content-Length, Content-MD5, Content-Range, Content-Type, Date, Host, If-Match, If-Modified-Since, If-None-Match, If-Unmodified-Since, Origin, OriginToken, Pragma, Range, Slug, Transfer-Encoding, Want-Digest, X-MediaBrowser-Token, X-Emby-Token, X-Emby-Client, X-Emby-Client-Version, X-Emby-Device-Id, X-Emby-Device-Name, X-Emby-Authorization")
	h.Set("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, PATCH, OPTIONS")
	h.Set("Access-Control-Allow-Private-Network", "true")
	h.Set("Cross-Origin-Resource-Policy", "cross-origin")
	h.Set("Expires", "-1")
}
