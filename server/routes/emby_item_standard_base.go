package routes

import "strings"

func embyEnsureStandardBaseItem(obj map[string]any, serverID string) {
	if obj == nil {
		return
	}

	if strings.TrimSpace(serverID) != "" {
		if _, ok := obj["ServerId"]; !ok {
			obj["ServerId"] = strings.TrimSpace(serverID)
		}
	}
	if _, ok := obj["ChannelId"]; !ok {
		obj["ChannelId"] = nil
	}
	if _, ok := obj["ProviderIds"]; !ok {
		obj["ProviderIds"] = map[string]any{}
	}
	if _, ok := obj["ImageTags"]; !ok {
		obj["ImageTags"] = map[string]any{}
	}
	if _, ok := obj["BackdropImageTags"]; !ok {
		obj["BackdropImageTags"] = []any{}
	}
	if _, ok := obj["ImageBlurHashes"]; !ok {
		obj["ImageBlurHashes"] = map[string]any{}
	}
}
