package routes

import (
	"strings"
	"sync"
	"time"
)

type embyStreamSession struct {
	URL    string
	Expire time.Time
}

type embyStreamStore struct {
	mu sync.Mutex
	m  map[string]embyStreamSession
}

func newEmbyStreamStore() *embyStreamStore {
	return &embyStreamStore{m: map[string]embyStreamSession{}}
}

func (s *embyStreamStore) Set(id string, url string, ttl time.Duration) {
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
	s.m[key] = embyStreamSession{URL: val, Expire: exp}
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
