package netdisk

import (
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"strings"
)

func writeJSON(w http.ResponseWriter, status int, payload any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(status)
	enc := json.NewEncoder(w)
	enc.SetEscapeHTML(false)
	_ = enc.Encode(payload)
}

func readJSONLoose(r *http.Request, dst any) error {
	if r == nil || dst == nil {
		return errors.New("invalid args")
	}
	defer func() { _ = r.Body.Close() }()
	dec := json.NewDecoder(io.LimitReader(r.Body, 1<<20))
	if err := dec.Decode(dst); err != nil {
		return err
	}
	return nil
}

func methodNotAllowed(w http.ResponseWriter) {
	writeJSON(w, http.StatusMethodNotAllowed, map[string]any{"success": false, "message": "Method not allowed"})
}

func parseForm(r *http.Request) {
	if r == nil {
		return
	}
	_ = r.ParseForm()
}

func boolFromForm(v string) bool {
	s := strings.ToLower(strings.TrimSpace(v))
	return s == "1" || s == "true" || s == "on" || s == "yes"
}

func parseJSONMap(text string) map[string]any {
	var m map[string]any
	if err := json.Unmarshal([]byte(text), &m); err != nil || m == nil {
		return map[string]any{}
	}
	return m
}
