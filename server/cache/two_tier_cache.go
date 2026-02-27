package cache

import "time"

// TwoTierTTLInflightCache composes two TTLInflightCache layers:
// - core: longer TTL for successful results
// - storm: shorter TTL that also caches errors to suppress request storms
//
// Do checks core first. On miss, it uses storm.DoWithOptions(CacheErrors=true) to de-duplicate
// concurrent callers and optionally cache failures. On successful fetches, it backfills core.
type TwoTierTTLInflightCache[V any] struct {
	core  *TTLInflightCache[V]
	storm *TTLInflightCache[V]
}

func NewTwoTierTTLInflightCache[V any](coreTTL time.Duration, coreMaxItems int, stormTTL time.Duration, stormMaxItems int) *TwoTierTTLInflightCache[V] {
	return &TwoTierTTLInflightCache[V]{
		core:  NewTTLInflightCache[V](coreTTL, coreMaxItems),
		storm: NewTTLInflightCache[V](stormTTL, stormMaxItems),
	}
}

func (t *TwoTierTTLInflightCache[V]) Core() *TTLInflightCache[V]  { return t.core }
func (t *TwoTierTTLInflightCache[V]) Storm() *TTLInflightCache[V] { return t.storm }

func (t *TwoTierTTLInflightCache[V]) Do(key string, fn func() (V, error)) (val V, fromCache bool, err error) {
	if t == nil {
		v, e := fn()
		return v, false, e
	}
	if t.core != nil {
		if v, ok := t.core.Get(key); ok {
			return v, true, nil
		}
	}
	if t.storm == nil {
		v, e := fn()
		if e == nil && t.core != nil {
			t.core.Set(key, v)
		}
		return v, false, e
	}

	return t.storm.DoWithOptions(key, TTLInflightCacheOptions{CacheErrors: true}, func() (V, error) {
		if t.core != nil {
			if v, ok := t.core.Get(key); ok {
				return v, nil
			}
		}
		v, e := fn()
		if e == nil && t.core != nil {
			t.core.Set(key, v)
		}
		return v, e
	})
}

