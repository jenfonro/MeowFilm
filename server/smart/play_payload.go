package smart

import (
	"strings"

	"github.com/jenfonro/meowfilm/server/catpawrunner"
	"github.com/jenfonro/meowfilm/server/netdisk"
)

func smartExtractPlayHeaders(playRaw map[string]any) map[string]string {
	if playRaw == nil {
		return nil
	}
	rawHeader, ok := playRaw["header"].(map[string]any)
	if !ok || len(rawHeader) == 0 {
		return nil
	}
	headers := map[string]string{}
	for k, v := range rawHeader {
		kk := strings.TrimSpace(k)
		sv := strings.TrimSpace(smartAnyToString(v))
		if kk == "" || sv == "" {
			continue
		}
		headers[kk] = sv
	}
	if len(headers) == 0 {
		return nil
	}
	return headers
}

func smartBuildCatpawPlayPayload(playRaw map[string]any, apiBase string, tvUser string) map[string]any {
	urlPicked := strings.TrimSpace(catpawrunner.PickFirstPlayableURL(playRaw))
	if urlPicked == "" {
		return map[string]any{}
	}
	urlPicked = catpawrunner.RewriteProxyURLToBase(urlPicked, apiBase, tvUser)
	return netdisk.BuildPlayPayload(urlPicked, smartExtractPlayHeaders(playRaw))
}
