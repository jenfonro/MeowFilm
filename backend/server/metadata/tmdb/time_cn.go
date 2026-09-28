package tmdb

import (
	"strings"
	"time"
)

// CNLocation returns the Asia/Shanghai location when available, otherwise a fixed UTC+8 zone.
func CNLocation() *time.Location {
	return tmdbCNLocation()
}

// CNDayStart returns Beijing (CN) local day start (00:00:00) for the given time.
func CNDayStart(t time.Time) time.Time {
	return tmdbCNDayStart(t)
}

// ParseAirDateCNMidnight parses a TMDB air_date (YYYY-MM-DD) and returns Beijing midnight for that day.
func ParseAirDateCNMidnight(dateText string) (time.Time, bool) {
	s := strings.TrimSpace(dateText)
	if s == "" {
		return time.Time{}, false
	}
	t, err := time.Parse("2006-01-02", s)
	if err != nil {
		return time.Time{}, false
	}
	loc := tmdbCNLocation()
	y, m, d := t.Date()
	return time.Date(y, m, d, 0, 0, 0, 0, loc), true
}

// IsAirDateAiredOrToday returns true when the given air_date (YYYY-MM-DD) is not after "today"
// in Beijing time (Asia/Shanghai), regardless of the server timezone.
func IsAirDateAiredOrToday(dateText string, now time.Time) bool {
	airDay, ok := ParseAirDateCNMidnight(dateText)
	if !ok {
		return false
	}
	return !airDay.After(CNDayStart(now))
}
