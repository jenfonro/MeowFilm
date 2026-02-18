package cache

import (
	"sync"
	"time"
)

type ttlEntry[V any] struct {
	expireAt time.Time
	value    V
}

type inflightCall[V any] struct {
	done chan struct{}
	val  V
	err  error
}

// TTLInflightCache provides:
// - TTL caching of successful results
// - in-flight de-duplication for concurrent callers of the same key
//
// Do returns fromCache=true only when a fresh TTL entry is used.
// Waiters of an in-flight request also get fromCache=true (because they do not trigger an upstream fetch).
type TTLInflightCache[V any] struct {
	mu       sync.Mutex
	ttl      time.Duration
	maxItems int
	items    map[string]ttlEntry[V]
	inflight map[string]*inflightCall[V]
}

func NewTTLInflightCache[V any](ttl time.Duration, maxItems int) *TTLInflightCache[V] {
	if maxItems <= 0 {
		maxItems = 4096
	}
	return &TTLInflightCache[V]{
		ttl:      ttl,
		maxItems: maxItems,
		items:    map[string]ttlEntry[V]{},
		inflight: map[string]*inflightCall[V]{},
	}
}

func (c *TTLInflightCache[V]) cleanupLocked(now time.Time) {
	if len(c.items) <= c.maxItems {
		return
	}
	for k, v := range c.items {
		if !v.expireAt.IsZero() && now.After(v.expireAt) {
			delete(c.items, k)
		}
	}
}

func (c *TTLInflightCache[V]) Do(key string, fn func() (V, error)) (val V, fromCache bool, err error) {
	k := key
	if k == "" {
		v, e := fn()
		return v, false, e
	}
	now := time.Now()

	c.mu.Lock()
	if e, ok := c.items[k]; ok && !e.expireAt.IsZero() && now.Before(e.expireAt) {
		v := e.value
		c.mu.Unlock()
		return v, true, nil
	}
	if in, ok := c.inflight[k]; ok && in != nil && in.done != nil {
		done := in.done
		c.mu.Unlock()
		<-done
		return in.val, true, in.err
	}

	in := &inflightCall[V]{done: make(chan struct{})}
	c.inflight[k] = in
	c.cleanupLocked(now)
	c.mu.Unlock()

	v, e := fn()

	c.mu.Lock()
	in.val = v
	in.err = e
	delete(c.inflight, k)
	if e == nil {
		c.items[k] = ttlEntry[V]{expireAt: now.Add(c.ttl), value: v}
	}
	close(in.done)
	c.mu.Unlock()

	return v, false, e
}
