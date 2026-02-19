//go:build !(cgo && (linux || darwin))

package magic

// QuickJS is mandatory for MeowFilm magic rules.
// Build with `CGO_ENABLED=1` on Linux/Darwin to enable the QuickJS engine.
var _ = QUICKJS_REQUIRED__BUILD_WITH_CGO_ENABLED_1_ON_LINUX_OR_DARWIN

