package netdisk

import (
	"encoding/json"
	"strconv"
	"strings"

	"github.com/jenfonro/meowfilm/server/magic"
)

func inferPanQualityLabel(item map[string]any) string {
	if item == nil {
		return ""
	}
	width := toIntLike(item["video_width"])
	height := toIntLike(item["video_height"])
	return inferPanQualityLabelFromResolution(width, height)
}

func inferPanFPSLabel(item map[string]any) string {
	if item == nil {
		return ""
	}
	fps := toFloatLike(item["fps"])
	switch {
	case fps >= 119.0:
		return "120FPS"
	case fps >= 59.0 && fps < 119.0:
		return "60FPS"
	default:
		return ""
	}
}

func buildPanDisplayPrefix(item map[string]any) string {
	quality := inferPanQualityLabel(item)
	fps := inferPanFPSLabel(item)
	switch {
	case quality != "" && fps != "":
		return "@" + quality + "@" + fps
	case quality != "":
		return "@" + quality
	case fps != "":
		return "@" + fps
	default:
		return ""
	}
}

func buildPanDisplayName(basePath string, item map[string]any) string {
	prefix := buildPanDisplayPrefix(item)
	if prefix == "" {
		return basePath
	}
	return prefix + basePath
}

func buildPanDisplayNameWithQuality(basePath string, quality string) string {
	quality = strings.TrimSpace(quality)
	if quality == "" {
		return basePath
	}
	return "@" + quality + basePath
}

func inferPanQualityLabelFromFilename(name string) string {
	matched, err := magic.RegexExtractFirstGroupFromCandidates([]string{strings.TrimSpace(name)}, `{"pattern":"(?:^|[\\s\\[\\]\\(\\){}【】._-])(4k|2160p|1080p|720p)(?=$|[\\s\\[\\]\\(\\){}【】._-])","flags":"i"}`)
	if err != nil {
		return ""
	}
	switch strings.ToLower(strings.TrimSpace(matched)) {
	case "4k", "2160p":
		return "4K"
	case "1080p":
		return "1080P"
	case "720p":
		return "720P"
	default:
		return ""
	}
}

func inferPanQualityLabelFromResolution(width int, height int) string {
	if width <= 0 || height <= 0 {
		return ""
	}
	maxSide := width
	if height > maxSide {
		maxSide = height
	}
	switch {
	case maxSide >= 2160:
		return "4K"
	case maxSide >= 1080:
		return "1080P"
	case maxSide >= 720:
		return "720P"
	default:
		return ""
	}
}

func toIntLike(v any) int {
	return int(toFloatLike(v))
}

func toFloatLike(v any) float64 {
	switch x := v.(type) {
	case nil:
		return 0
	case float64:
		return x
	case float32:
		return float64(x)
	case int:
		return float64(x)
	case int64:
		return float64(x)
	case uint64:
		return float64(x)
	case json.Number:
		f, _ := x.Float64()
		return f
	default:
		f, _ := strconv.ParseFloat(strings.TrimSpace(toString(v)), 64)
		return f
	}
}
