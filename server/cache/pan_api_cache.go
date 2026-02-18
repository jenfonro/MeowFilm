package cache

import (
	"sync"
	"time"
)

type ttlEntry[V any] struct {
	expireAt   time.Time
	createdAt  time.Time
	refreshes  int
	value      V
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

type TTLInflightCacheOptions struct {
	// Sliding refreshes expireAt on cache hits (bounded by MaxLife/MaxRefreshes when set).
	Sliding bool
	// MaxLife caps total cache lifetime since the entry is created (0 = unlimited).
	MaxLife time.Duration
	// MaxRefreshes caps how many times an entry can be refreshed (0 = unlimited).
	MaxRefreshes int
	// CacheErrors stores failed results too (default false, because errors are often transient).
	CacheErrors bool
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
		c.items[k] = ttlEntry[V]{expireAt: now.Add(c.ttl), createdAt: now, refreshes: 0, value: v}
	}
	close(in.done)
	c.mu.Unlock()

	return v, false, e
}

func (c *TTLInflightCache[V]) DoWithOptions(key string, opt TTLInflightCacheOptions, fn func() (V, error)) (val V, fromCache bool, err error) {
	k := key
	if k == "" {
		v, e := fn()
		return v, false, e
	}
	now := time.Now()

	c.mu.Lock()
	if e, ok := c.items[k]; ok && !e.expireAt.IsZero() && now.Before(e.expireAt) {
		if opt.Sliding {
			allowRefresh := true
			if opt.MaxLife > 0 && !e.createdAt.IsZero() && now.Sub(e.createdAt) >= opt.MaxLife {
				allowRefresh = false
			}
			if opt.MaxRefreshes > 0 && e.refreshes >= opt.MaxRefreshes {
				allowRefresh = false
			}
			if allowRefresh {
				e.expireAt = now.Add(c.ttl)
				e.refreshes++
				c.items[k] = e
			}
		}
		v := e.value
		c.mu.Unlock()
		return v, true, nil
	}
	if in, ok := c.inflight[k]; ok && in != nil && in.done != nil {
		done := in.done
		c.mu.Unlock()
		<-done
		// Optionally refresh after waiting, to support "sliding window" behavior for waiters too.
		if opt.Sliding && in.err == nil {
			now2 := time.Now()
			c.mu.Lock()
			if e, ok := c.items[k]; ok && !e.expireAt.IsZero() && now2.Before(e.expireAt) {
				allowRefresh := true
				if opt.MaxLife > 0 && !e.createdAt.IsZero() && now2.Sub(e.createdAt) >= opt.MaxLife {
					allowRefresh = false
				}
				if opt.MaxRefreshes > 0 && e.refreshes >= opt.MaxRefreshes {
					allowRefresh = false
				}
				if allowRefresh {
					e.expireAt = now2.Add(c.ttl)
					e.refreshes++
					c.items[k] = e
				}
			}
			c.mu.Unlock()
		}
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
	if e == nil || opt.CacheErrors {
		storeAt := time.Now()
		c.items[k] = ttlEntry[V]{expireAt: storeAt.Add(c.ttl), createdAt: storeAt, refreshes: 0, value: v}
	}
	close(in.done)
	c.mu.Unlock()

	return v, false, e
}
