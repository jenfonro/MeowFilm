package net

import (
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"strings"
)

func WriteJSON(w http.ResponseWriter, status int, payload any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(status)
	enc := json.NewEncoder(w)
	enc.SetEscapeHTML(false)
	_ = enc.Encode(payload)
}

func ReadJSONLoose(r *http.Request, dst any) error {
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

func ReadJSONStrict(r *http.Request, dst any) error {
	if r == nil || dst == nil {
		return errors.New("invalid args")
	}
	defer func() { _ = r.Body.Close() }()
	dec := json.NewDecoder(io.LimitReader(r.Body, 1<<20))
	dec.DisallowUnknownFields()
	if err := dec.Decode(dst); err != nil {
		return err
	}
	return nil
}

func MethodNotAllowed(w http.ResponseWriter) {
	WriteJSON(w, http.StatusMethodNotAllowed, map[string]any{"success": false, "message": "Method not allowed"})
}

func ParseForm(r *http.Request) {
	if r == nil {
		return
	}
	_ = r.ParseForm()
}

func BoolFromForm(v string) bool {
	s := strings.ToLower(strings.TrimSpace(v))
	return s == "1" || s == "true" || s == "on" || s == "yes"
}
