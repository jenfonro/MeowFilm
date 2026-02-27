package emby

import (
	"errors"
	"sync"
)

type embyInflightResolve struct {
	done chan struct{}
	url  string
	err  error
}

var embyStreamInflight = struct {
	mu sync.Mutex
	m  map[string]*embyInflightResolve
}{
	m: map[string]*embyInflightResolve{},
}

func embyResolveStreamOnce(key string, fn func() (string, error)) (string, error) {
	k := key
	if k == "" {
		return "", errors.New("missing resolve key")
	}

	embyStreamInflight.mu.Lock()
	if in, ok := embyStreamInflight.m[k]; ok && in != nil {
		done := in.done
		embyStreamInflight.mu.Unlock()
		<-done
		return in.url, in.err
	}
	in := &embyInflightResolve{done: make(chan struct{})}
	embyStreamInflight.m[k] = in
	embyStreamInflight.mu.Unlock()

	defer func() {
		if r := recover(); r != nil {
			in.err = errors.New("panic in resolve")
		}
		embyStreamInflight.mu.Lock()
		delete(embyStreamInflight.m, k)
		close(in.done)
		embyStreamInflight.mu.Unlock()
	}()

	in.url, in.err = fn()
	return in.url, in.err
}
