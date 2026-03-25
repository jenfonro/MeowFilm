package clientmeta

import (
	"net/http/httptest"
	"testing"
)

func TestResolveRequestClientMetaFromAuthorization(t *testing.T) {
	req := httptest.NewRequest("GET", "/", nil)
	req.Header.Set("X-Emby-Authorization", `MediaBrowser Client="Infuse", Device="iPhone", DeviceId="abc123", Version="8.0"`)

	meta := ResolveRequestClientMeta(req)
	if meta.Client != "Infuse" {
		t.Fatalf("Client = %q, want %q", meta.Client, "Infuse")
	}
	if meta.Device != "iPhone" {
		t.Fatalf("Device = %q, want %q", meta.Device, "iPhone")
	}
	if meta.DeviceID != "abc123" {
		t.Fatalf("DeviceID = %q, want %q", meta.DeviceID, "abc123")
	}
	if meta.Version != "8.0" {
		t.Fatalf("Version = %q, want %q", meta.Version, "8.0")
	}
}

func TestResolveRequestClientMetaHeaderPrecedence(t *testing.T) {
	req := httptest.NewRequest("GET", "/?X-Application=query-client", nil)
	req.Header.Set("X-Emby-Authorization", `MediaBrowser Client="auth-client", Device="Auth Device", DeviceId="auth-id", Version="1.0"`)
	req.Header.Set("X-Emby-Client", "header-client")
	req.Header.Set("X-Emby-Device-Name", "Header Device")
	req.Header.Set("X-Emby-Device-Id", "header-id")
	req.Header.Set("X-Emby-Client-Version", "2.0")

	meta := ResolveRequestClientMeta(req)
	if meta.Client != "header-client" {
		t.Fatalf("Client = %q, want %q", meta.Client, "header-client")
	}
	if meta.Device != "Header Device" {
		t.Fatalf("Device = %q, want %q", meta.Device, "Header Device")
	}
	if meta.DeviceID != "header-id" {
		t.Fatalf("DeviceID = %q, want %q", meta.DeviceID, "header-id")
	}
	if meta.Version != "2.0" {
		t.Fatalf("Version = %q, want %q", meta.Version, "2.0")
	}
}
