package catpawopen

import (
	"encoding/json"
	"fmt"
	"strings"
)

func NormalizeSearchList(data map[string]any) []SearchItem {
	listAny, _ := data["list"].([]any)
	out := []SearchItem{}
	for _, it := range listAny {
		m, ok := it.(map[string]any)
		if !ok {
			continue
		}
		id := anyToString(m["vod_id"])
		if id == "" {
			id = anyToString(m["id"])
		}
		name := anyToString(m["vod_name"])
		if name == "" {
			name = anyToString(m["name"])
		}
		if strings.TrimSpace(name) == "" {
			continue
		}
		pic := anyToString(m["vod_pic"])
		if pic == "" {
			pic = anyToString(m["pic"])
		}
		remark := anyToString(m["vod_remarks"])
		if remark == "" {
			remark = anyToString(m["remark"])
		}
		out = append(out, SearchItem{
			ID:     strings.TrimSpace(id),
			Name:   strings.TrimSpace(name),
			Pic:    strings.TrimSpace(pic),
			Remark: strings.TrimSpace(remark),
		})
	}
	return out
}

func ExtractDetailPlayFromURL(data map[string]any) (playFrom string, playURL string) {
	pick := func(m map[string]any) (string, string) {
		if m == nil {
			return "", ""
		}
		from := anyToString(m["vod_play_from"])
		if from == "" {
			from = anyToString(m["playFrom"])
		}
		urlStr := anyToString(m["vod_play_url"])
		if urlStr == "" {
			urlStr = anyToString(m["playUrl"])
		}
		return strings.TrimSpace(from), strings.TrimSpace(urlStr)
	}
	if v, ok := data["list"].([]any); ok && len(v) > 0 {
		if m, ok := v[0].(map[string]any); ok {
			return pick(m)
		}
	}
	if d, ok := data["data"].(map[string]any); ok {
		if v, ok := d["list"].([]any); ok && len(v) > 0 {
			if m, ok := v[0].(map[string]any); ok {
				return pick(m)
			}
		}
	}
	if m, ok := data["vod"].(map[string]any); ok {
		return pick(m)
	}
	return "", ""
}

func ParsePlaySources(fromStr string, urlStr string) []Pan {
	fromStr = strings.TrimSpace(fromStr)
	urlStr = strings.TrimSpace(urlStr)
	if fromStr == "" && urlStr == "" {
		return nil
	}
	fromParts := strings.Split(fromStr, "$$$")
	urlParts := strings.Split(urlStr, "$$$")
	n := len(fromParts)
	if len(urlParts) > n {
		n = len(urlParts)
	}
	out := []Pan{}
	for i := 0; i < n; i++ {
		label := ""
		if i < len(fromParts) {
			label = strings.TrimSpace(fromParts[i])
		}
		if label == "" {
			label = fmt.Sprintf("源%d", i+1)
		}
		u := ""
		if i < len(urlParts) {
			u = strings.TrimSpace(urlParts[i])
		}
		if u == "" {
			continue
		}
		segs := []string{}
		for _, s := range strings.Split(u, "#") {
			ss := strings.TrimSpace(s)
			if ss != "" {
				segs = append(segs, ss)
			}
		}
		eps := []Episode{}
		for _, seg := range segs {
			name := seg
			id := seg
			if idx := strings.Index(seg, "$"); idx > 0 {
				name = strings.TrimSpace(seg[:idx])
				id = strings.TrimSpace(seg[idx+1:])
			}
			if name == "" {
				name = seg
			}
			if id == "" {
				id = seg
			}
			eps = append(eps, Episode{Name: name, URL: id, Flag: label})
		}
		if len(eps) == 0 {
			continue
		}
		out = append(out, Pan{Label: label, Episodes: eps})
	}
	return out
}

func anyToString(v any) string {
	switch vv := v.(type) {
	case string:
		return vv
	case json.Number:
		return vv.String()
	case float64:
		return fmt.Sprintf("%.0f", vv)
	default:
		return ""
	}
}
