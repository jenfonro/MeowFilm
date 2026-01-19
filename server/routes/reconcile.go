package routes

import (
	"encoding/json"
	"strings"
)

type reconciledSiteState struct {
	Sites         []site
	Status        map[string]bool
	Home          map[string]bool
	Order         []string
	Availability  map[string]string
}

func normalizeSitesSlice(input []site) []site {
	out := make([]site, 0, len(input))
	seen := map[string]struct{}{}
	for _, s := range input {
		key := strings.TrimSpace(s.Key)
		api := strings.TrimSpace(s.API)
		if key == "" || api == "" {
			continue
		}
		if _, ok := seen[key]; ok {
			continue
		}
		seen[key] = struct{}{}
		out = append(out, site{Key: key, Name: s.Name, API: api, Type: s.Type})
	}
	return out
}

func parseAvailabilityJSON(text string) map[string]string {
	raw := parseJSONMap(text)
	out := map[string]string{}
	for k, v := range raw {
		key := strings.TrimSpace(k)
		if key == "" {
			continue
		}
		s, _ := v.(string)
		out[key] = normalizeAvailability(s)
	}
	return out
}

func reconcileSites(nextSites []site, prevStatus map[string]bool, prevHome map[string]bool, prevOrder []string, prevAvailability map[string]string) reconciledSiteState {
	normalizedNew := normalizeSitesSlice(nextSites)
	keysInNewOrder := make([]string, 0, len(normalizedNew))
	newKeySet := map[string]struct{}{}
	for _, s := range normalizedNew {
		keysInNewOrder = append(keysInNewOrder, s.Key)
		newKeySet[s.Key] = struct{}{}
	}

	nextStatus := map[string]bool{}
	for k, v := range prevStatus {
		key := strings.TrimSpace(k)
		if key == "" {
			continue
		}
		if _, ok := newKeySet[key]; !ok {
			continue
		}
		nextStatus[key] = v
	}

	nextHome := map[string]bool{}
	for k, v := range prevHome {
		key := strings.TrimSpace(k)
		if key == "" {
			continue
		}
		if _, ok := newKeySet[key]; !ok {
			continue
		}
		nextHome[key] = v
	}

	nextAvailability := map[string]string{}
	for k, v := range prevAvailability {
		key := strings.TrimSpace(k)
		if key == "" {
			continue
		}
		if _, ok := newKeySet[key]; !ok {
			continue
		}
		nextAvailability[key] = normalizeAvailability(v)
	}

	for _, s := range normalizedNew {
		if _, ok := nextStatus[s.Key]; !ok {
			nextStatus[s.Key] = true
		}
		if _, ok := nextHome[s.Key]; !ok {
			nextHome[s.Key] = defaultHomeForSite(s)
		}
		if _, ok := nextAvailability[s.Key]; !ok {
			nextAvailability[s.Key] = "unchecked"
		}
	}

	// Preserve old order for existing keys; insert new keys based on the new JS order.
	prevOrderFiltered := []string{}
	for _, k := range prevOrder {
		key := strings.TrimSpace(k)
		if key == "" {
			continue
		}
		if _, ok := newKeySet[key]; !ok {
			continue
		}
		prevOrderFiltered = append(prevOrderFiltered, key)
	}
	nextOrder := []string{}
	seenOrder := map[string]struct{}{}
	for _, key := range prevOrderFiltered {
		if _, ok := seenOrder[key]; ok {
			continue
		}
		seenOrder[key] = struct{}{}
		nextOrder = append(nextOrder, key)
	}

	lastIndex := -1
	for _, key := range keysInNewOrder {
		idx := indexOf(nextOrder, key)
		if idx >= 0 {
			lastIndex = idx
			continue
		}
		insertAt := lastIndex + 1
		if insertAt < 0 {
			insertAt = 0
		}
		if insertAt > len(nextOrder) {
			insertAt = len(nextOrder)
		}
		nextOrder = append(nextOrder[:insertAt], append([]string{key}, nextOrder[insertAt:]...)...)
		lastIndex = insertAt
	}

	return reconciledSiteState{
		Sites:        normalizedNew,
		Status:       nextStatus,
		Home:         nextHome,
		Order:        nextOrder,
		Availability: nextAvailability,
	}
}

func indexOf(list []string, v string) int {
	for i := 0; i < len(list); i++ {
		if list[i] == v {
			return i
		}
	}
	return -1
}

func marshalJSON(v any) string {
	b, _ := json.Marshal(v)
	return string(b)
}

