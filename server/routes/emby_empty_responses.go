package routes

import "net/http"

func embyWriteEmptyArrayOK(w http.ResponseWriter) {
	writeJSON(w, http.StatusOK, []any{})
}
