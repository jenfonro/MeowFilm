package smart

import (
	"testing"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/catpawrunner"
)

func TestExplicitHistoryListCanonicalNamesReuseExistingShareCache(t *testing.T) {
	for _, tc := range []struct{ flag, provider, id, password string }{
		{"百度-1Abcdefghij-klmn_opqrs-a1b2", "baidu", "1Abcdefghij-klmn_opqrs", "a1b2"},
		{"百度-shareA", "baidu", "shareA", ""},
		{"夸克-shareA-abcd", "quark", "shareA", "abcd"},
		{"UC-shareA-abcd", "uc", "shareA", "abcd"},
		{"天翼-shareA-abcd", "189", "shareA", "abcd"},
		{"移动-shareA-abcd", "139", "shareA", "abcd"},
		{"夸父-shareA", "quark", "shareA", ""},
		{"百度原画(无限)-1shareA", "baidu", "shareA", ""},
		{"百度原画HDR-1shareA", "baidu", "shareA", ""},
		{"百度原画(极速)-1shareA#旧后缀", "baidu", "shareA", ""},
	} {
		t.Run(tc.flag, func(t *testing.T) {
			key := tc.provider + ":" + tc.id + "|" + tc.password
			smartSharedPanList.mu.Lock()
			previous, existed := smartSharedPanList.m[key]
			smartSharedPanList.mu.Unlock()
			t.Cleanup(func() {
				smartSharedPanList.mu.Lock()
				defer smartSharedPanList.mu.Unlock()
				if existed {
					smartSharedPanList.m[key] = previous
				} else {
					delete(smartSharedPanList.m, key)
				}
			})
			smartSharedPanListSetResolved(key, smartSharedPanListResult{
				State: smartSharedPanListResolved, Status: "ok", FromCache: true,
				Episodes: []catpawrunner.Episode{{Name: "E1", URL: "existing-list-file"}},
			})
			// Emby/history use this existing explicit list entry, with a flag
			// but no detail share URL. A cache hit requires no database/network.
			episodes, _, _, status, started, err := ResolvePanMockEpisodesBySourceValue(&db.DB{}, tc.flag, "")
			if err != nil || status != "ok" || started || len(episodes) != 1 || episodes[0].URL != "existing-list-file" {
				t.Fatalf("canonical history lookup bypassed the share cache: %v %s %v %v", episodes, status, started, err)
			}
			if _, ok := catpawrunner.ParsePanShareInput(tc.flag, "File$private-id"); ok {
				t.Fatal("standalone history adaptation must not relax normal detail ownership")
			}
		})
	}
}

func TestExplicitDecoratedHistoryListKeepsSuppliedPassword(t *testing.T) {
	const key = "baidu:historyShareWithPassword|a1b2"
	smartSharedPanListSetResolved(key, smartSharedPanListResult{
		State: smartSharedPanListResolved, Status: "ok", FromCache: true,
		Episodes: []catpawrunner.Episode{{Name: "E1", URL: "existing-password-list"}},
	})
	t.Cleanup(func() {
		smartSharedPanList.mu.Lock()
		delete(smartSharedPanList.m, key)
		smartSharedPanList.mu.Unlock()
	})
	episodes, _, _, status, started, err := ResolvePanMockEpisodesBySourceValue(
		&db.DB{}, "百度原画(无限)-1historyShareWithPassword", "a1b2",
	)
	if err != nil || status != "ok" || started || len(episodes) != 1 || episodes[0].URL != "existing-password-list" {
		t.Fatalf("saved flag/password no longer reaches its existing list: %v %s %v %v", episodes, status, started, err)
	}
}
