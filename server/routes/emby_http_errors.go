package routes

import "net/http"

func embyNotFound(w http.ResponseWriter) {
	embyWriteError(w, http.StatusNotFound, "")
}

func embyMethodNotAllowed(w http.ResponseWriter) {
	embyWriteError(w, http.StatusMethodNotAllowed, "")
}
