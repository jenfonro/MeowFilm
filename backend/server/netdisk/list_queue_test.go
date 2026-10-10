package netdisk

import (
	"errors"
	"testing"
	"time"

	"github.com/jenfonro/meowfilm/server/cache"
)

func TestListProviderQueueAndCacheBoundary(t *testing.T) {
	release := acquireProviderList(nil, "baidu")
	waiting := make(chan struct{})
	go func() {
		unlock := acquireProviderList(nil, "baidu")
		defer unlock()
		close(waiting)
	}()
	other := make(chan struct{})
	go func() {
		unlock := acquireProviderList(nil, "quark")
		defer unlock()
		close(other)
	}()
	select {
	case <-other:
	case <-time.After(time.Second):
		t.Fatal("different provider was blocked")
	}
	select {
	case <-waiting:
		t.Fatal("same provider overlapped")
	default:
	}
	c := cache.NewTwoTierTTLInflightCache[string](time.Minute, 10, time.Second, 10)
	c.Core().Set("cached", "ready")
	value, hit, err := c.Do("cached", func() (string, error) {
		unlock := acquireProviderList(nil, "baidu")
		defer unlock()
		return "", errors.New("cache producer should not run")
	})
	if err != nil || !hit || value != "ready" {
		t.Fatal("cache hit waited for the network list queue")
	}
	release()
	select {
	case <-waiting:
	case <-time.After(time.Second):
		t.Fatal("queue was not released")
	}
}

func TestListProviderReleaseOnFailure(t *testing.T) {
	func() {
		defer func() { _ = recover() }()
		release := acquireProviderList(nil, "uc")
		defer release()
		panic("test")
	}()
	done := make(chan struct{})
	go func() {
		release := acquireProviderList(nil, "uc")
		defer release()
		close(done)
	}()
	select {
	case <-done:
	case <-time.After(time.Second):
		t.Fatal("failed list poisoned its provider queue")
	}
}
