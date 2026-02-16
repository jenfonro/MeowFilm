package api

import (
	"net/http"

	mfnet "github.com/jenfonro/meowfilm/server/net"
)

func writeJSON(w http.ResponseWriter, status int, payload any) {
	mfnet.WriteJSON(w, status, payload)
}

func readJSON(r *http.Request, dst any) error {
	return mfnet.ReadJSONStrict(r, dst)
}

func readJSONLoose(r *http.Request, dst any) error {
	return mfnet.ReadJSONLoose(r, dst)
}

func methodNotAllowed(w http.ResponseWriter) {
	mfnet.MethodNotAllowed(w)
}

func parseForm(r *http.Request) {
	mfnet.ParseForm(r)
}

func boolFromForm(v string) bool {
	return mfnet.BoolFromForm(v)
}
