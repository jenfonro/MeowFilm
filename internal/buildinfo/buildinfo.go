package buildinfo

import "strings"

// Watermark is a per-build identifier injected via -ldflags "-X ...=...".
// It is intentionally not derived from runtime config to keep the public build "hard".
var Watermark string

func WatermarkTrim() string { return strings.TrimSpace(Watermark) }
