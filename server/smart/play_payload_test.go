package smart

import (
	"testing"

	"github.com/jenfonro/meowfilm/server/netdisk"
)

func TestSmartExtractPlayHeadersFromHeaderMapAny(t *testing.T) {
	playRaw := map[string]any{
		"header": map[string]any{
			"User-Agent": "UA",
			"Referer":    "https://a",
		},
	}

	headers := smartExtractPlayHeaders(playRaw)
	if len(headers) != 2 {
		t.Fatalf("expected 2 headers, got %d", len(headers))
	}
	if headers["User-Agent"] != "UA" || headers["Referer"] != "https://a" {
		t.Fatalf("expected headers to be preserved, got %#v", headers)
	}
}

func TestSmartExtractPlayHeadersFromHeadersMapAny(t *testing.T) {
	playRaw := map[string]any{
		"headers": map[string]any{
			"User-Agent": "UA",
		},
	}

	headers := smartExtractPlayHeaders(playRaw)
	if len(headers) != 1 || headers["User-Agent"] != "UA" {
		t.Fatalf("expected User-Agent header to be preserved, got %#v", headers)
	}
}

func TestSmartExtractPlayHeadersFromHeaderMapString(t *testing.T) {
	playRaw := map[string]any{
		"header": map[string]string{
			"Cookie": "x=1",
		},
	}

	headers := smartExtractPlayHeaders(playRaw)
	if len(headers) != 1 || headers["Cookie"] != "x=1" {
		t.Fatalf("expected Cookie header to be preserved, got %#v", headers)
	}
}

func TestSmartExtractPlayHeadersFallsBackToHeaders(t *testing.T) {
	playRaw := map[string]any{
		"header": map[string]any{},
		"headers": map[string]string{
			"Cookie": "x=1",
		},
	}

	headers := smartExtractPlayHeaders(playRaw)
	if len(headers) != 1 || headers["Cookie"] != "x=1" {
		t.Fatalf("expected fallback headers value to be preserved, got %#v", headers)
	}
}

func TestSmartExtractPlayHeadersFiltersEmptyValues(t *testing.T) {
	playRaw := map[string]any{
		"header": map[string]any{
			"":             "bad",
			"X-Empty":      "",
			"X-Whitespace": "   ",
			" User-Agent ": " UA ",
		},
	}

	headers := smartExtractPlayHeaders(playRaw)
	if len(headers) != 1 || headers["User-Agent"] != "UA" {
		t.Fatalf("expected trimmed User-Agent header only, got %#v", headers)
	}
}

func TestBuildCatpawPlayPayloadIncludesHeaders(t *testing.T) {
	playRaw := map[string]any{
		"url": "https://example.com/video.m3u8",
		"headers": map[string]any{
			"User-Agent": "UA",
		},
	}

	payload := BuildCatpawPlayPayload(playRaw, "http://api.example.com/", "tester")
	finalURL, finalHeaders := netdisk.PlayPayloadURLHeaders(payload)
	if finalURL != "https://example.com/video.m3u8" {
		t.Fatalf("expected final url to be preserved, got %q", finalURL)
	}
	if len(finalHeaders) != 1 || finalHeaders["User-Agent"] != "UA" {
		t.Fatalf("expected extracted headers to be preserved, got %#v", finalHeaders)
	}
}

func TestBuildCatpawPlayPayloadWithoutHeaders(t *testing.T) {
	playRaw := map[string]any{
		"url":    "https://example.com/video.m3u8",
		"header": map[string]any{},
	}

	payload := BuildCatpawPlayPayload(playRaw, "http://api.example.com/", "tester")
	finalURL, finalHeaders := netdisk.PlayPayloadURLHeaders(payload)
	if finalURL != "https://example.com/video.m3u8" {
		t.Fatalf("expected final url to be preserved, got %q", finalURL)
	}
	if finalHeaders != nil {
		t.Fatalf("expected no headers, got %#v", finalHeaders)
	}
}
