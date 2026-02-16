package emby

import (
	"net/http"
	"strings"
)

func embyRequireSameUserOrNotFound(w http.ResponseWriter, expectedUserID string, actualUserID string) bool {
	if w == nil {
		return false
	}
	if strings.TrimSpace(expectedUserID) == "" {
		embyNotFound(w)
		return false
	}
	if strings.TrimSpace(actualUserID) != strings.TrimSpace(expectedUserID) {
		embyNotFound(w)
		return false
	}
	return true
}

func embyAllowEmptyOrRequireSameUserOrNotFound(w http.ResponseWriter, expectedUserID string, actualUserID string) bool {
	if w == nil {
		return false
	}
	if strings.TrimSpace(actualUserID) == "" {
		return true
	}
	return embyRequireSameUserOrNotFound(w, expectedUserID, actualUserID)
}
