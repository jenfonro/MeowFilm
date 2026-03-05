//go:build !cgo

package magic

// QuickJS is mandatory for MeowFilm magic rules.
// Build with `CGO_ENABLED=1` to enable the QuickJS engine.
var _ = QUICKJS_REQUIRED__BUILD_WITH_CGO_ENABLED_1
