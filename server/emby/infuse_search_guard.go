package emby

import (
	"fmt"
	"strings"
	"sync"
	"time"
)

type infuseSearchGuardEntry struct {
	At         time.Time
	RawTerm    string
	FoldedTerm string
}

var infuseSearchGuard struct {
	mu sync.Mutex
	m  map[string]infuseSearchGuardEntry
}

func infuseSearchGuardKey(userID string, deviceID string) string {
	uid := strings.TrimSpace(userID)
	did := strings.TrimSpace(deviceID)
	if uid == "" {
		uid = "0"
	}
	if did == "" {
		did = "unknown"
	}
	return fmt.Sprintf("u:%s|d:%s", uid, did)
}

// InfuseShouldDropFoldedDuplicate returns true when Infuse sends a follow-up search request
// whose SearchTerm differs only by (lightweight) Traditional/Simplified folding, within a short window.
//
// This is a pragmatic workaround for Infuse "double probing" that can otherwise produce duplicated cards.
func InfuseShouldDropFoldedDuplicate(userID string, deviceID string, rawTerm string, foldedTerm string, now time.Time) bool {
	key := infuseSearchGuardKey(userID, deviceID)
	rt := strings.TrimSpace(rawTerm)
	ft := strings.TrimSpace(foldedTerm)
	if ft == "" {
		ft = CanonicalSearchTerm(rt)
	}

	infuseSearchGuard.mu.Lock()
	defer infuseSearchGuard.mu.Unlock()
	if infuseSearchGuard.m == nil {
		infuseSearchGuard.m = map[string]infuseSearchGuardEntry{}
	}
	prev, ok := infuseSearchGuard.m[key]
	infuseSearchGuard.m[key] = infuseSearchGuardEntry{At: now, RawTerm: rt, FoldedTerm: ft}
	if !ok {
		return false
	}
	if prev.FoldedTerm == "" || ft == "" {
		return false
	}
	if !prev.At.IsZero() && now.Sub(prev.At) > 6*time.Second {
		return false
	}
	if prev.FoldedTerm == ft && strings.TrimSpace(prev.RawTerm) != rt {
		return true
	}
	return false
}
