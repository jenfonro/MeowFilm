package netdisk

import (
	"sync"

	"github.com/jenfonro/meowfilm/internal/db"
)

type providerListKey struct {
	database *db.DB
	provider string
}

type providerListGate struct {
	token chan struct{}
	users int
}

var providerLists = struct {
	sync.Mutex
	gates map[providerListKey]*providerListGate
}{gates: make(map[providerListKey]*providerListGate)}

// Acquire only inside a list cache producer. Cache hits and existing in-flight
// waiters bypass it; all API/smart callers with the same configured account
// share one provider queue. Playback does not use this gate.
func acquireProviderList(database *db.DB, provider string) func() {
	key := providerListKey{database, provider}
	providerLists.Lock()
	gate := providerLists.gates[key]
	if gate == nil {
		gate = &providerListGate{token: make(chan struct{}, 1)}
		providerLists.gates[key] = gate
	}
	gate.users++
	providerLists.Unlock()
	gate.token <- struct{}{}
	return func() {
		<-gate.token
		providerLists.Lock()
		gate.users--
		if gate.users == 0 {
			delete(providerLists.gates, key)
		}
		providerLists.Unlock()
	}
}
