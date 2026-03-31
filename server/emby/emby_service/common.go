package emby_service

import (
	"crypto/md5"
	"encoding/hex"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"time"
)

func StableMD5Hex(input string) string {
	sum := md5.Sum([]byte(strings.TrimSpace(input)))
	return hex.EncodeToString(sum[:])
}

func EmbyZeroTimeString() string {
	return time.Time{}.UTC().Format("2006-01-02T15:04:05.0000000Z")
}

func embyTimeString(t time.Time) string {
	if t.IsZero() {
		return ""
	}
	return t.UTC().Format("2006-01-02T15:04:05.0000000Z")
}

func maxInt(a int, b int) int {
	if a > b {
		return a
	}
	return b
}

func minInt64(a int64, b int64) int64 {
	if a < b {
		return a
	}
	return b
}

func maxInt64(a int64, b int64) int64 {
	if a > b {
		return a
	}
	return b
}

func RealMediaPathOrEmpty(path string) string {
	trimmed := strings.TrimSpace(path)
	if trimmed == "" || !strings.Contains(trimmed, "/") {
		return ""
	}
	return trimmed
}

func VirtualMoviePath(title string, year int, fileName string) string {
	dirName := VirtualMediaDirName(title, year)
	name := strings.TrimSpace(fileName)
	if name == "" {
		base := strings.TrimSpace(title)
		if base == "" {
			base = "video"
		}
		name = base + ".mp4"
	}
	return "/media/movie/" + dirName + "/" + name
}

func VirtualSeriesPath(title string, year int) string {
	return "/media/tv/" + VirtualMediaDirName(title, year)
}

func VirtualEpisodePath(seriesTitle string, year int, season int, fileName string) string {
	base := strings.TrimSpace(fileName)
	if base == "" {
		base = fmt.Sprintf("S%02dE%02d.mp4", season, 1)
	}
	return VirtualSeriesPath(seriesTitle, year) + "/" + VirtualSeasonDirName(season) + "/" + base
}

func VirtualSettingsEpisodePath(seriesTitle string, year int, fileName string) string {
	base := strings.TrimSpace(fileName)
	if base == "" {
		base = "noop.mp4"
	}
	return VirtualSeriesPath(seriesTitle, year) + "/设置/" + base
}

func VirtualMediaDirName(title string, year int) string {
	name := strings.TrimSpace(title)
	if name == "" {
		name = "video"
	}
	if year > 0 {
		return fmt.Sprintf("%s (%d)", name, year)
	}
	return name
}

func VirtualSeasonDirName(season int) string {
	if season <= 0 {
		return "Season 01"
	}
	return fmt.Sprintf("Season %02d", season)
}

func YearFromDate(raw string) int {
	raw = strings.TrimSpace(raw)
	if len(raw) < 4 {
		return 0
	}
	year := raw[:4]
	n := 0
	for _, ch := range year {
		if ch < '0' || ch > '9' {
			return 0
		}
		n = n*10 + int(ch-'0')
	}
	return n
}

func ProtocolCreatedDate(ts int64) string {
	created, _ := ProtocolDatePairFromUnix(ts)
	return created
}

func DetectMediaContainer(fileName string, fallback string) string {
	ext := strings.ToLower(strings.TrimPrefix(filepath.Ext(strings.TrimSpace(fileName)), "."))
	switch ext {
	case "mkv", "mp4", "m4v", "avi", "ts":
		return ext
	}
	return strings.TrimSpace(fallback)
}

func MediaFileSizeAndBitrate(path string, runtimeTicks int64) (int64, int) {
	trimmed := strings.TrimSpace(path)
	if trimmed == "" {
		return 0, 0
	}
	info, err := os.Stat(trimmed)
	if err != nil || info == nil || info.IsDir() {
		return 0, 0
	}
	size := info.Size()
	if size <= 0 || runtimeTicks <= 0 {
		return size, 0
	}
	seconds := float64(runtimeTicks) / 10_000_000
	if seconds <= 0 {
		return size, 0
	}
	bitrate := int(float64(size*8) / seconds)
	if bitrate < 0 {
		bitrate = 0
	}
	return size, bitrate
}
