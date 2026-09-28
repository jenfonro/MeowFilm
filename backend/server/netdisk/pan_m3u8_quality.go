package netdisk

import (
	"errors"
	"io"
	"net/http"
	"regexp"
	"strings"
	"time"
)

var panM3U8ResolutionRe = regexp.MustCompile(`(?i)RESOLUTION=(\d+)x(\d+)`)

func detectPanQualityFromM3U8(url string) (string, error) {
	u := strings.TrimSpace(url)
	if u == "" {
		return "", errors.New("empty m3u8 url")
	}
	req, err := http.NewRequest(http.MethodGet, u, nil)
	if err != nil {
		return "", err
	}
	req.Header.Set("User-Agent", pan139UA)
	client := &http.Client{Timeout: 12 * time.Second, Transport: netdiskHTTPTransport}
	resp, err := client.Do(req)
	if err != nil {
		return "", err
	}
	defer resp.Body.Close()
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return "", errors.New("m3u8 http " + resp.Status)
	}
	b, err := io.ReadAll(resp.Body)
	if err != nil {
		return "", err
	}
	matches := panM3U8ResolutionRe.FindAllStringSubmatch(string(b), -1)
	bestW, bestH := 0, 0
	for _, m := range matches {
		if len(m) < 3 {
			continue
		}
		w := toIntLike(m[1])
		h := toIntLike(m[2])
		if qualityRankOfResolution(w, h) > qualityRankOfResolution(bestW, bestH) {
			bestW, bestH = w, h
			continue
		}
		if qualityRankOfResolution(w, h) == qualityRankOfResolution(bestW, bestH) {
			if maxInt(w, h) > maxInt(bestW, bestH) {
				bestW, bestH = w, h
			}
		}
	}
	return inferPanQualityLabelFromResolution(bestW, bestH), nil
}

func qualityRankOfResolution(width int, height int) int {
	switch inferPanQualityLabelFromResolution(width, height) {
	case "4K":
		return 3
	case "1080P":
		return 2
	case "720P":
		return 1
	default:
		return 0
	}
}

func maxInt(a int, b int) int {
	if a > b {
		return a
	}
	return b
}
