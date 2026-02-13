package routes

import "strings"

func embyEnsureStandardMediaSources(obj map[string]any) {
	if obj == nil {
		return
	}
	msRaw, ok := obj["MediaSources"]
	if !ok || msRaw == nil {
		return
	}

	switch ms := msRaw.(type) {
	case []map[string]any:
		for _, m := range ms {
			embyEnsureStandardMediaSource(m, obj)
		}
	case []any:
		for _, it := range ms {
			m, _ := it.(map[string]any)
			if m == nil {
				continue
			}
			embyEnsureStandardMediaSource(m, obj)
		}
	}
}

func embyEnsureStandardMediaSource(ms map[string]any, parent map[string]any) {
	if ms == nil {
		return
	}
	if parent == nil {
		parent = map[string]any{}
	}

	if _, ok := ms["Protocol"]; !ok {
		ms["Protocol"] = "File"
	}
	if _, ok := ms["Type"]; !ok {
		ms["Type"] = "Default"
	}
	if _, ok := ms["Container"]; !ok {
		ms["Container"] = "mp4"
	}
	if _, ok := ms["IsRemote"]; !ok {
		ms["IsRemote"] = false
	}
	if _, ok := ms["Size"]; !ok {
		ms["Size"] = int64(0)
	}
	if _, ok := ms["Name"]; !ok {
		if n, ok := parent["Name"].(string); ok && strings.TrimSpace(n) != "" {
			ms["Name"] = strings.TrimSpace(n)
		}
	}
	if _, ok := ms["ETag"]; !ok {
		if v, ok := ms["Id"].(string); ok && strings.TrimSpace(v) != "" {
			ms["ETag"] = embyStableEtag(strings.TrimSpace(v))
		}
	}
	if _, ok := ms["RunTimeTicks"]; !ok {
		if v, ok := parent["RunTimeTicks"].(int64); ok && v > 0 {
			ms["RunTimeTicks"] = v
		} else {
			ms["RunTimeTicks"] = int64(0)
		}
	}

	if _, ok := ms["ReadAtNativeFramerate"]; !ok {
		ms["ReadAtNativeFramerate"] = false
	}
	if _, ok := ms["IgnoreDts"]; !ok {
		ms["IgnoreDts"] = false
	}
	if _, ok := ms["IgnoreIndex"]; !ok {
		ms["IgnoreIndex"] = false
	}
	if _, ok := ms["GenPtsInput"]; !ok {
		ms["GenPtsInput"] = false
	}
	if _, ok := ms["SupportsTranscoding"]; !ok {
		ms["SupportsTranscoding"] = true
	}
	if _, ok := ms["SupportsDirectStream"]; !ok {
		ms["SupportsDirectStream"] = true
	}
	if _, ok := ms["SupportsDirectPlay"]; !ok {
		ms["SupportsDirectPlay"] = true
	}
	if _, ok := ms["IsInfiniteStream"]; !ok {
		ms["IsInfiniteStream"] = false
	}
	if _, ok := ms["RequiresOpening"]; !ok {
		ms["RequiresOpening"] = false
	}
	if _, ok := ms["RequiresClosing"]; !ok {
		ms["RequiresClosing"] = false
	}
	if _, ok := ms["RequiresLooping"]; !ok {
		ms["RequiresLooping"] = false
	}
	if _, ok := ms["SupportsProbing"]; !ok {
		ms["SupportsProbing"] = true
	}
	if _, ok := ms["VideoType"]; !ok {
		ms["VideoType"] = "VideoFile"
	}
	if _, ok := ms["MediaStreams"]; !ok {
		ms["MediaStreams"] = []any{}
	}
	if _, ok := ms["MediaAttachments"]; !ok {
		ms["MediaAttachments"] = []any{}
	}
	if _, ok := ms["Formats"]; !ok {
		ms["Formats"] = []any{}
	}
	if _, ok := ms["Bitrate"]; !ok {
		ms["Bitrate"] = int64(0)
	}
	if _, ok := ms["RequiredHttpHeaders"]; !ok {
		ms["RequiredHttpHeaders"] = map[string]any{}
	}
	if _, ok := ms["DefaultAudioStreamIndex"]; !ok {
		ms["DefaultAudioStreamIndex"] = 1
	}
}
