package emby

import (
	"regexp"
	"strings"
	"sync"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/catpawopen"
	"github.com/jenfonro/meowfilm/server/netdisk"
)

type embyPanMock189AccessEntry struct {
	AccessCode string
	ExpireAt   time.Time
}

var embyPanMock189Access struct {
	mu sync.Mutex
	m  map[string]embyPanMock189AccessEntry // key: shareID
}

const embyPanMock189AccessTTL = 30 * time.Minute

func embyPanMock189AccessPut(shareID string, accessCode string) {
	sid := strings.TrimSpace(shareID)
	ac := strings.TrimSpace(accessCode)
	if sid == "" || ac == "" {
		return
	}
	now := time.Now()

	embyPanMock189Access.mu.Lock()
	defer embyPanMock189Access.mu.Unlock()
	if embyPanMock189Access.m == nil {
		embyPanMock189Access.m = map[string]embyPanMock189AccessEntry{}
	}
	// quick cleanup to avoid unbounded growth
	if len(embyPanMock189Access.m) > 4096 {
		for k, v := range embyPanMock189Access.m {
			if !v.ExpireAt.IsZero() && now.After(v.ExpireAt) {
				delete(embyPanMock189Access.m, k)
			}
		}
	}
	embyPanMock189Access.m[sid] = embyPanMock189AccessEntry{
		AccessCode: ac,
		ExpireAt:   now.Add(embyPanMock189AccessTTL),
	}
}

func embyPanMock189AccessGet(shareID string) (string, bool) {
	sid := strings.TrimSpace(shareID)
	if sid == "" {
		return "", false
	}
	now := time.Now()

	embyPanMock189Access.mu.Lock()
	defer embyPanMock189Access.mu.Unlock()
	if embyPanMock189Access.m == nil {
		return "", false
	}
	e, ok := embyPanMock189Access.m[sid]
	if !ok {
		return "", false
	}
	if !e.ExpireAt.IsZero() && now.After(e.ExpireAt) {
		delete(embyPanMock189Access.m, sid)
		return "", false
	}
	ac := strings.TrimSpace(e.AccessCode)
	if ac == "" {
		return "", false
	}
	return ac, true
}

func embyIsPanMockEnabled(detailRaw map[string]any) bool {
	if detailRaw == nil {
		return false
	}
	v, ok := detailRaw["pan_mock"]
	if !ok || v == nil {
		return false
	}
	switch x := v.(type) {
	case bool:
		return x
	case string:
		s := strings.TrimSpace(x)
		return s == "1" || strings.EqualFold(s, "true")
	case float64:
		return int(x) == 1
	default:
		return false
	}
}

func embyExtractMockPasscodeFromEpisodeURL(episodeURL string) string {
	names := smartExtractRawNamesFromEpisodeURL(episodeURL)
	if len(names) == 0 {
		return ""
	}
	raw := strings.TrimSpace(names[0])
	if raw == "" {
		return ""
	}
	if !strings.HasSuffix(strings.ToLower(raw), ".mp4") {
		// Only placeholder filenames encode passcodes as "<pass>.mp4".
		return ""
	}
	out := raw
	if strings.HasSuffix(strings.ToLower(out), ".mp4") {
		out = strings.TrimSpace(out[:len(out)-4])
	}
	if strings.EqualFold(out, "nopass") {
		return ""
	}
	return strings.TrimSpace(out)
}

func embyExtractTianyiMockMeta(panLabel string, episodeURL string) (shareCode string, accessCode string) {
	label := strings.TrimSpace(panLabel)
	url := strings.TrimSpace(episodeURL)

	// Fallback: shareCode might already be embedded in the label like "天意-XXXX" / "天翼-XXXX".
	if m := regexp.MustCompile(`(?:天意|天翼)-([A-Za-z0-9]{6,64})`).FindStringSubmatch(label); len(m) == 2 {
		shareCode = strings.TrimSpace(m[1])
	}

	pass := strings.TrimSpace(embyExtractMockPasscodeFromEpisodeURL(url))
	if pass == "" {
		return shareCode, ""
	}
	if strings.Contains(pass, "-") {
		seg := strings.SplitN(pass, "-", 2)
		if shareCode == "" && strings.TrimSpace(seg[0]) != "" {
			shareCode = strings.TrimSpace(seg[0])
		}
		if len(seg) == 2 {
			accessCode = strings.TrimSpace(seg[1])
		}
	} else {
		accessCode = pass
	}
	if strings.EqualFold(accessCode, "nopass") {
		accessCode = ""
	}
	return shareCode, accessCode
}

func embyResolvePanMockDetailPans(database *db.DB, pans []catpawopen.Pan) ([]catpawopen.Pan, map[string]string) {
	out := make([]catpawopen.Pan, 0, len(pans))
	for _, p := range pans {
		out = append(out, p)
	}
	accessByShareID := map[string]string{}
	if database == nil || len(out) == 0 {
		return out, accessByShareID
	}

	var wg sync.WaitGroup
	var mu sync.Mutex

	for idx := range out {
		i := idx
		label := strings.TrimSpace(out[i].Label)
		pid := smartPanMockProviderID(label)
		if pid == "" {
			continue
		}
		firstURL := ""
		if len(out[i].Episodes) > 0 {
			firstURL = strings.TrimSpace(out[i].Episodes[0].URL)
		}
		wg.Add(1)
		go func() {
			defer wg.Done()

			switch pid {
			case "tianyi":
				sc, ac := embyExtractTianyiMockMeta(label, firstURL)
				if sc == "" {
					return
				}
				flag := "天意-" + sc
				vod, shareID, _, err := netdisk.Tianyi189List(database, flag, ac)
				if err != nil || strings.TrimSpace(vod) == "" {
					return
				}
				eps := smartParseVodPlayURLToEpisodes(vod)
				for k := range eps {
					eps[k].Flag = label
				}
				mu.Lock()
				out[i].Episodes = eps
				if strings.TrimSpace(shareID) != "" && strings.TrimSpace(ac) != "" {
					sid := strings.TrimSpace(shareID)
					acc := strings.TrimSpace(ac)
					accessByShareID[sid] = acc
					embyPanMock189AccessPut(sid, acc)
				}
				mu.Unlock()
			case "quark":
				pass := embyExtractMockPasscodeFromEpisodeURL(firstURL)
				vod, _, err := netdisk.QuarkList(database, label, pass)
				if err != nil || strings.TrimSpace(vod) == "" {
					return
				}
				eps := smartParseVodPlayURLToEpisodes(vod)
				for k := range eps {
					eps[k].Flag = label
				}
				mu.Lock()
				out[i].Episodes = eps
				mu.Unlock()
			case "uc":
				pass := embyExtractMockPasscodeFromEpisodeURL(firstURL)
				vod, _, err := netdisk.UCList(database, label, pass)
				if err != nil || strings.TrimSpace(vod) == "" {
					return
				}
				eps := smartParseVodPlayURLToEpisodes(vod)
				for k := range eps {
					eps[k].Flag = label
				}
				mu.Lock()
				out[i].Episodes = eps
				mu.Unlock()
			case "139":
				pass := embyExtractMockPasscodeFromEpisodeURL(firstURL)
				vod, _, err := netdisk.Yun139List(database, label, pass)
				if err != nil || strings.TrimSpace(vod) == "" {
					return
				}
				eps := smartParseVodPlayURLToEpisodes(vod)
				for k := range eps {
					eps[k].Flag = label
				}
				mu.Lock()
				out[i].Episodes = eps
				mu.Unlock()
			case "baidu":
				pass := embyExtractMockPasscodeFromEpisodeURL(firstURL)
				vod, _, err := netdisk.BaiduList(database, label, pass)
				if err != nil || strings.TrimSpace(vod) == "" {
					return
				}
				eps := smartParseVodPlayURLToEpisodes(vod)
				for k := range eps {
					eps[k].Flag = label
				}
				mu.Lock()
				out[i].Episodes = eps
				mu.Unlock()
			default:
				return
			}
		}()
	}

	wg.Wait()
	return out, accessByShareID
}
