package emby

import (
	"strings"
	"sync"
	"time"
)

type embyStreamSession struct {
	URL    string
	Meta   embyStreamMeta
	Expire time.Time
}

type embyStreamMeta struct {
	SiteKey    string
	SiteDetail string
	PanFlag    string
	Provider   string
}

type embyStreamStore struct {
	mu sync.Mutex
	m  map[string]embyStreamSession
}

func newEmbyStreamStore() *embyStreamStore {
	return &embyStreamStore{m: map[string]embyStreamSession{}}
}

func (s *embyStreamStore) Set(id string, url string, ttl time.Duration) {
	s.SetMeta(id, url, ttl, embyStreamMeta{})
}

func (s *embyStreamStore) SetMeta(id string, url string, ttl time.Duration, meta embyStreamMeta) {
	if s == nil {
		return
	}
	key := strings.TrimSpace(id)
	val := strings.TrimSpace(url)
	if key == "" || val == "" {
		return
	}
	now := time.Now()
	exp := now.Add(ttl)
	s.mu.Lock()
	s.m[key] = embyStreamSession{URL: val, Meta: meta, Expire: exp}
	s.cleanupLocked(now)
	s.mu.Unlock()
}

func (s *embyStreamStore) Get(id string) (string, bool) {
	if s == nil {
		return "", false
	}
	key := strings.TrimSpace(id)
	if key == "" {
		return "", false
	}
	now := time.Now()
	s.mu.Lock()
	sess, ok := s.m[key]
	s.cleanupLocked(now)
	s.mu.Unlock()
	if !ok || strings.TrimSpace(sess.URL) == "" || sess.Expire.Before(now) {
		return "", false
	}
	return strings.TrimSpace(sess.URL), true
}

func (s *embyStreamStore) GetMeta(id string) (embyStreamMeta, bool) {
	if s == nil {
		return embyStreamMeta{}, false
	}
	key := strings.TrimSpace(id)
	if key == "" {
		return embyStreamMeta{}, false
	}
	now := time.Now()
	s.mu.Lock()
	sess, ok := s.m[key]
	s.cleanupLocked(now)
	s.mu.Unlock()
	if !ok || strings.TrimSpace(sess.URL) == "" || sess.Expire.Before(now) {
		return embyStreamMeta{}, false
	}
	if strings.TrimSpace(sess.Meta.SiteKey) == "" || strings.TrimSpace(sess.Meta.SiteDetail) == "" {
		return embyStreamMeta{}, false
	}
	return sess.Meta, true
}

func (s *embyStreamStore) cleanupLocked(now time.Time) {
	if s == nil || s.m == nil {
		return
	}
	for k, v := range s.m {
		if v.Expire.Before(now) {
			delete(s.m, k)
		}
	}
}

// ExtendIfLow extends an existing session TTL when it is close to expiring.
// - Only extends when remaining <= capRemaining.
// - Extends by add, but caps the new expiry to now+capRemaining.
func (s *embyStreamStore) ExtendIfLow(id string, add time.Duration, capRemaining time.Duration) bool {
	if s == nil {
		return false
	}
	key := strings.TrimSpace(id)
	if key == "" {
		return false
	}
	now := time.Now()
	s.mu.Lock()
	defer s.mu.Unlock()
	sess, ok := s.m[key]
	if !ok || strings.TrimSpace(sess.URL) == "" || sess.Expire.Before(now) {
		s.cleanupLocked(now)
		return false
	}
	remain := sess.Expire.Sub(now)
	if remain > capRemaining {
		s.cleanupLocked(now)
		return false
	}
	newExp := sess.Expire.Add(add)
	maxExp := now.Add(capRemaining)
	if newExp.After(maxExp) {
		newExp = maxExp
	}
	if newExp.After(sess.Expire) {
		sess.Expire = newExp
		s.m[key] = sess
	}
	s.cleanupLocked(now)
	return true
}
