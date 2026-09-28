package netdisk

import "strings"

const panRootFolderName = "MeowFilm"

func extractPanSaveTopFid(root any) string {
	if root == nil {
		return ""
	}
	type item struct{ value any }
	queue := []item{{value: root}}
	steps := 0
	for len(queue) > 0 && steps < 5000 {
		steps++
		cur := queue[0].value
		queue = queue[1:]
		switch v := cur.(type) {
		case map[string]any:
			if raw, ok := v["save_as_top_fids"].([]any); ok && len(raw) > 0 {
				if fid := strings.TrimSpace(toString(raw[0])); fid != "" {
					return fid
				}
			}
			if raw, ok := v["save_as_top_fid"].([]any); ok && len(raw) > 0 {
				if fid := strings.TrimSpace(toString(raw[0])); fid != "" {
					return fid
				}
			}
			if fid := strings.TrimSpace(toString(v["save_as_top_fid"])); fid != "" {
				return fid
			}
			if raw, ok := v["save_as_select_top_fids"].([]any); ok && len(raw) > 0 {
				if fid := strings.TrimSpace(toString(raw[0])); fid != "" {
					return fid
				}
			}
			for _, child := range v {
				queue = append(queue, item{value: child})
			}
		case []any:
			for _, child := range v {
				queue = append(queue, item{value: child})
			}
		}
	}
	return ""
}
