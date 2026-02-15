package routes

import (
	"fmt"
	"strings"
	"sync"
	"time"
)

type embyInfuseSearchGuardEntry struct {
	At         time.Time
	RawTerm    string
	FoldedTerm string
}

var embyInfuseSearchGuard struct {
	mu sync.Mutex
	m  map[string]embyInfuseSearchGuardEntry
}

func embyInfuseSearchGuardKey(userID string, deviceID string) string {
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

// embyInfuseShouldDropFoldedDuplicate returns true when Infuse sends a follow-up search request
// whose SearchTerm differs only by (lightweight) Traditional/Simplified folding, within a short window.
//
// This is a pragmatic workaround for Infuse "double probing" that can otherwise produce duplicated cards.
func embyInfuseShouldDropFoldedDuplicate(userID string, deviceID string, rawTerm string, foldedTerm string, now time.Time) bool {
	key := embyInfuseSearchGuardKey(userID, deviceID)
	rt := strings.TrimSpace(rawTerm)
	ft := strings.TrimSpace(foldedTerm)
	if ft == "" {
		ft = embyCanonicalSearchTerm(rt)
	}

	embyInfuseSearchGuard.mu.Lock()
	defer embyInfuseSearchGuard.mu.Unlock()
	if embyInfuseSearchGuard.m == nil {
		embyInfuseSearchGuard.m = map[string]embyInfuseSearchGuardEntry{}
	}
	prev, ok := embyInfuseSearchGuard.m[key]
	embyInfuseSearchGuard.m[key] = embyInfuseSearchGuardEntry{At: now, RawTerm: rt, FoldedTerm: ft}
	if !ok {
		return false
	}
	if prev.FoldedTerm == "" || ft == "" {
		return false
	}
	// If it's too old, don't treat as a paired request.
	if !prev.At.IsZero() && now.Sub(prev.At) > 6*time.Second {
		return false
	}
	// Only drop when the folded term matches and the raw term is different.
	if prev.FoldedTerm == ft && strings.TrimSpace(prev.RawTerm) != rt {
		return true
	}
	return false
}

