package staticassets

import "embed"

//go:embed settings/images/* settings/videos/*
var SettingsFS embed.FS

