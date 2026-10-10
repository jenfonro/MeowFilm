package emby_service

import (
	"context"

	"github.com/jenfonro/meowfilm/server/smart"
)

// Reuse the existing offer queue's feedback, only for newly supported detail
// navigation. Do not enqueue the rest of a share tree while a playable offer
// still awaits the normal play attempt. History/manual stages are unchanged.
func navigationOfferWaiter(entry *playbackControlEntry) smart.NavigationOfferWait {
	if entry == nil {
		return nil
	}
	return func(ctx context.Context, offers []smart.PlaybackOffer) {
		stop := context.AfterFunc(ctx, func() {
			entry.mu.Lock()
			entry.cond.Broadcast()
			entry.mu.Unlock()
		})
		defer stop()
		entry.mu.Lock()
		defer entry.mu.Unlock()
		for ctx.Err() == nil && !entry.playbackDone && !IsPlaybackResolveStopped(entry.stopCh) {
			pending := false
			for _, offer := range offers {
				if _, enqueued := entry.offerSeen[playbackOfferCandidateKey(offer)]; !enqueued {
					continue
				}
				if _, failed := entry.triedFailed[smart.PlaybackAttemptKey(offer.Cand)]; !failed {
					pending = true
					break
				}
			}
			if !pending {
				return
			}
			entry.cond.Wait()
		}
	}
}
