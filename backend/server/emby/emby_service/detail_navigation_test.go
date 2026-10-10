package emby_service

import (
	"context"
	"testing"
	"time"

	"github.com/jenfonro/meowfilm/server/smart"
)

func navigationTestControl(t *testing.T) *playbackControlEntry {
	entry := ensurePlaybackControlEntry(1, "", "", "", t.Name(), "", "tv", "episode", 1, false, smart.PlaybackSettings{})
	t.Cleanup(func() {
		MarkPlaybackDone(entry.PlaySessionID, "", "")
		playbackControl.mu.Lock()
		delete(playbackControl.bySession, entry.PlaySessionID)
		playbackControl.mu.Unlock()
	})
	return entry
}

func TestNavigationWaitsForPlayResultNotJustEnqueue(t *testing.T) {
	entry := navigationTestControl(t)
	offer := smart.PlaybackOffer{Cand: smart.Candidate{SiteKey: "site", SiteDetail: "movie", PanFlag: "蓝光HDR", RawName: "File"}}
	if !EnqueueFullOffer(entry.PlaySessionID, "", "", offer) {
		t.Fatal("enqueue failed")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
	defer cancel()
	done := make(chan struct{})
	go func() { navigationOfferWaiter(entry)(ctx, []smart.PlaybackOffer{offer}); close(done) }()
	CloseFullOffers(entry.PlaySessionID, "", "")
	select {
	case <-done:
		t.Fatal("enqueue/close was treated as play completion")
	case <-time.After(20 * time.Millisecond):
	}
	MarkPlaybackOfferFailed(entry.PlaySessionID, "", "", offer)
	select {
	case <-done:
	case <-ctx.Done():
		t.Fatal("play failure did not release the next navigation")
	}
}

func TestNavigationWaitStopsOnPlaybackSuccessOrCancellation(t *testing.T) {
	for _, success := range []bool{true, false} {
		t.Run(map[bool]string{true: "success", false: "cancel"}[success], func(t *testing.T) {
			entry := navigationTestControl(t)
			offer := smart.PlaybackOffer{Cand: smart.Candidate{SiteKey: "site", RawName: "File"}}
			EnqueueFullOffer(entry.PlaySessionID, "", "", offer)
			ctx, cancel := context.WithCancel(context.Background())
			defer cancel()
			done := make(chan struct{})
			go func() { navigationOfferWaiter(entry)(ctx, []smart.PlaybackOffer{offer}); close(done) }()
			if success {
				MarkPlaybackDone(entry.PlaySessionID, "", "")
			} else {
				cancel()
			}
			select {
			case <-done:
			case <-time.After(time.Second):
				t.Fatal("navigation waiter leaked")
			}
		})
	}
}

func TestNavigationWaitDoesNotWaitForSkippedOffers(t *testing.T) {
	entry := navigationTestControl(t)
	ctx, cancel := context.WithTimeout(context.Background(), time.Second)
	defer cancel()
	navigationOfferWaiter(entry)(ctx, []smart.PlaybackOffer{{Cand: smart.Candidate{RawName: "not-enqueued"}}})
	if ctx.Err() != nil {
		t.Fatal("skipped offer blocked subsequent navigation")
	}
}
