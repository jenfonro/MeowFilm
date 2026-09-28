package smart

import (
	"encoding/json"
	"fmt"
	"strings"

	"github.com/jenfonro/meowfilm/server/catpawrunner"
	"github.com/jenfonro/meowfilm/server/netdisk"
)

func smartPlayHeaderValueString(v any) string {
	switch x := v.(type) {
	case nil:
		return ""
	case string:
		return x
	case json.Number:
		return x.String()
	default:
		return strings.TrimSpace(fmt.Sprint(x))
	}
}

func smartExtractPlayHeadersFromValue(raw any) map[string]string {
	switch headers := raw.(type) {
	case map[string]any:
		out := map[string]string{}
		for k, v := range headers {
			kk := strings.TrimSpace(k)
			sv := strings.TrimSpace(smartPlayHeaderValueString(v))
			if kk == "" || sv == "" {
				continue
			}
			out[kk] = sv
		}
		if len(out) == 0 {
			return nil
		}
		return out
	case map[string]string:
		out := map[string]string{}
		for k, v := range headers {
			kk := strings.TrimSpace(k)
			sv := strings.TrimSpace(v)
			if kk == "" || sv == "" {
				continue
			}
			out[kk] = sv
		}
		if len(out) == 0 {
			return nil
		}
		return out
	default:
		return nil
	}
}

func smartExtractPlayHeaders(playRaw map[string]any) map[string]string {
	if playRaw == nil {
		return nil
	}
	if headers := smartExtractPlayHeadersFromValue(playRaw["header"]); len(headers) > 0 {
		return headers
	}
	return smartExtractPlayHeadersFromValue(playRaw["headers"])
}

func smartBuildCatpawPlayPayload(playRaw map[string]any, apiBase string, tvUser string) map[string]any {
	urlPicked := strings.TrimSpace(catpawrunner.PickFirstPlayableURL(playRaw))
	if urlPicked == "" {
		return map[string]any{}
	}
	urlPicked = catpawrunner.RewriteProxyURLToBase(urlPicked, apiBase, tvUser)
	return netdisk.BuildPlayPayload(urlPicked, smartExtractPlayHeaders(playRaw))
}
