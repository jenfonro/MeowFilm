package emby

import "net/http"

func embyWriteCORSHeaders(h http.Header) {
	if h == nil {
		return
	}
	h.Set("Access-Control-Allow-Origin", "*")
	h.Set("Access-Control-Allow-Headers", embyCORSAllowHeaders)
	h.Set("Access-Control-Allow-Methods", "GET,POST,OPTIONS")
}
