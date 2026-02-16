package net

import (
	"bufio"
	"compress/gzip"
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

	// Some clients/proxies may send a gzipped request body (even without the proper headers).
	// Detect gzip magic bytes (0x1f 0x8b) and decode defensively with size limits.
	limited := io.LimitReader(r.Body, 1<<20)
	br := bufio.NewReader(limited)
	var reader io.Reader = br
	if peek, _ := br.Peek(2); len(peek) == 2 && peek[0] == 0x1f && peek[1] == 0x8b {
		if gz, err := gzip.NewReader(br); err == nil {
			defer func() { _ = gz.Close() }()
			reader = io.LimitReader(gz, 1<<20)
		}
	}
	dec := json.NewDecoder(reader)
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

	limited := io.LimitReader(r.Body, 1<<20)
	br := bufio.NewReader(limited)
	var reader io.Reader = br
	if peek, _ := br.Peek(2); len(peek) == 2 && peek[0] == 0x1f && peek[1] == 0x8b {
		if gz, err := gzip.NewReader(br); err == nil {
			defer func() { _ = gz.Close() }()
			reader = io.LimitReader(gz, 1<<20)
		}
	}
	dec := json.NewDecoder(reader)
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
