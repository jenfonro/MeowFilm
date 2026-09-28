package emby_service

import "time"

func EmptyPeople() []PersonDTO {
	return []PersonDTO{}
}

func EmptyStrings() []string {
	return []string{}
}

func EmptyNamedIDs() []NamedIDDTO {
	return []NamedIDDTO{}
}

func EmptyAnySlice() []any {
	return []any{}
}

func EmptyRemoteTrailers() []any {
	return EmptyAnySlice()
}

func EmptyLockedFields() []string {
	return EmptyStrings()
}

func EmptyExternalURLs() []ExternalURLDTO {
	return []ExternalURLDTO{}
}

func EmptyAnyExternalURLs() []any {
	return EmptyAnySlice()
}

func ProtocolEtag() string {
	return ""
}

func ProtocolPresentationUniqueKey() string {
	return ""
}

func ProtocolDisplayPreferencesID() string {
	return ""
}

func ProtocolDatePairFromUnix(ts int64) (string, string) {
	if ts <= 0 {
		zero := EmbyZeroTimeString()
		return zero, zero
	}
	v := embyTimeString(time.Unix(ts, 0))
	if v == "" {
		zero := EmbyZeroTimeString()
		return zero, zero
	}
	return v, v
}

func StableItemEtag(itemID string) string {
	return StableMD5Hex(itemID + "|etag")
}

func StablePresentationUniqueKey(itemID string) string {
	return StableMD5Hex(itemID + "|presentation")
}

func StableDisplayPreferencesID(itemID string) string {
	return StableMD5Hex(itemID + "|display")
}
