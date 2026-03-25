package emby_service

import (
	"errors"
	"strings"
	"sync"
)

type playbackInflightResolve struct {
	done   chan struct{}
	target *PlaybackStreamTarget
	ok     bool
	err    error
}

var playbackResolveInflight = struct {
	mu sync.Mutex
	m  map[string]*playbackInflightResolve
}{
	m: map[string]*playbackInflightResolve{},
}

func ResolvePlaybackOnce(key string, fn func() (*PlaybackStreamTarget, bool, error)) (*PlaybackStreamTarget, bool, error) {
	k := stringsTrim(key)
	if k == "" {
		return nil, false, errors.New("missing resolve key")
	}
	playbackResolveInflight.mu.Lock()
	if in, ok := playbackResolveInflight.m[k]; ok && in != nil {
		done := in.done
		playbackResolveInflight.mu.Unlock()
		<-done
		if in.target == nil {
			return nil, in.ok, in.err
		}
		target := clonePlaybackTarget(*in.target)
		return &target, in.ok, in.err
	}
	in := &playbackInflightResolve{done: make(chan struct{})}
	playbackResolveInflight.m[k] = in
	playbackResolveInflight.mu.Unlock()

	defer func() {
		if r := recover(); r != nil {
			in.err = errors.New("panic in resolve")
		}
		playbackResolveInflight.mu.Lock()
		delete(playbackResolveInflight.m, k)
		close(in.done)
		playbackResolveInflight.mu.Unlock()
	}()

	target, ok, err := fn()
	if target != nil {
		copied := clonePlaybackTarget(*target)
		in.target = &copied
	}
	in.ok = ok
	in.err = err
	if in.target == nil {
		return nil, ok, err
	}
	out := clonePlaybackTarget(*in.target)
	return &out, ok, err
}

func stringsTrim(s string) string { return strings.TrimSpace(s) }
