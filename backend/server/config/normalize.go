package config

import "strings"

// NormalizeSourceExtractPriority normalizes the smart playback source extraction priority setting.
// Allowed values are: 无 / 网盘 / 关键字.
func NormalizeSourceExtractPriority(raw string) string {
	s := strings.TrimSpace(raw)
	if s == "" {
		return "无"
	}
	if s == "无" || s == "网盘" || s == "关键字" {
		return s
	}
	return "无"
}
