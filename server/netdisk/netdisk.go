package netdisk

// Package netdisk provides unified netdisk (pan) integrations.
//
// NOTE: For now, this package primarily hosts existing QR-login flows and settings
// endpoints used by the dashboard. Over time, it should evolve towards a unified
// provider interface (Login/Refresh/List/Download) for multiple netdisk vendors.

type ProviderID string

const (
	ProviderBaidu ProviderID = "baidu"
	ProviderQuark ProviderID = "quark"
	ProviderUC    ProviderID = "uc"
	Provider115   ProviderID = "115"
	ProviderBili  ProviderID = "bili"
)
