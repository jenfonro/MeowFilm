package limit

import (
	"database/sql"
	"errors"
	"strings"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/cache"
)

var (
	apiCountCache      = cache.NewTTLInflightCache[int](2*time.Second, 8)
	dashboardOffsetHit = cache.NewTTLInflightCache[bool](2*time.Second, 8)
	embyScanCache      = cache.NewTTLInflightCache[bool](2*time.Second, 8)
	loginCountCache    = cache.NewTTLInflightCache[int](2*time.Second, 8)
)

func Enabled() bool { return defaultMaxUsers > 0 }

func MaxUsers() int { return defaultMaxUsers }

func Code() string {
	// "E17" xor 0x5A
	b := []byte{0x1f, 0x6b, 0x6d}
	for i := range b {
		b[i] ^= 0x5A
	}
	return string(b)
}

func PublicCode() string {
	// "USER_LIMIT_EXCEEDED" xor 0x5A
	b := []byte{
		0x0f, 0x09, 0x1f, 0x08, 0x05, 0x16, 0x13, 0x17, 0x13, 0x0e, 0x05, 0x1f, 0x02, 0x19, 0x1f, 0x1f, 0x1e, 0x1f, 0x1e,
	}
	for i := range b {
		b[i] ^= 0x5A
	}
	return string(b)
}

func GuardAPI(database *db.DB) (exceeded bool, err error) {
	if !Enabled() || database == nil {
		return false, nil
	}
	max := MaxUsers()
	n, _, e := apiCountCache.Do("api_count_users", func() (int, error) {
		return database.CountUsers()
	})
	if e != nil {
		return false, e
	}
	return n > max, nil
}

func GuardDashboard(database *db.DB) (exceeded bool, err error) {
	if !Enabled() || database == nil {
		return false, nil
	}
	max := MaxUsers()

	hit, _, e := dashboardOffsetHit.Do("dash_offset_hit", func() (bool, error) {
		raw := database.SQL()
		if raw == nil {
			return false, nil
		}
		var v int
		err := raw.QueryRow(`SELECT 1 FROM users LIMIT 1 OFFSET ?`, max).Scan(&v)
		if err != nil {
			if errors.Is(err, sql.ErrNoRows) {
				return false, nil
			}
			return false, err
		}
		return true, nil
	})
	if e != nil {
		return false, e
	}
	return hit, nil
}

func GuardEmby(database *db.DB) (exceeded bool, err error) {
	if !Enabled() || database == nil {
		return false, nil
	}
	max := MaxUsers()

	ex, _, e := embyScanCache.Do("emby_scan", func() (bool, error) {
		raw := database.SQL()
		if raw == nil {
			return false, nil
		}

		ok, err := database.VerifyUsersLimitTrigger()
		if err != nil {
			return false, err
		}
		if !ok {
			// Best-effort repair; if it still doesn't match, refuse.
			_ = database.RepairUsersLimitTrigger()
			Audit("tr", "r")
			ok2, err2 := database.VerifyUsersLimitTrigger()
			if err2 != nil {
				return false, err2
			}
			if !ok2 {
				Audit("tr", "x")
				return true, nil
			}
		}

		rows, err := raw.Query(`SELECT id FROM users LIMIT ?`, max+1)
		if err != nil {
			return false, err
		}
		defer rows.Close()
		n := 0
		var id int64
		for rows.Next() {
			_ = rows.Scan(&id)
			n++
			if n > max {
				return true, nil
			}
		}
		return false, nil
	})
	if e != nil {
		return false, e
	}
	return ex, nil
}

func GuardLogin(database *db.DB) (exceeded bool, err error) {
	if !Enabled() || database == nil {
		return false, nil
	}
	max := MaxUsers()

	n, _, e := loginCountCache.Do("login_count_users", func() (int, error) {
		return database.CountUsers()
	})
	if e != nil {
		return false, e
	}
	if n > max {
		return true, nil
	}

	// Additional trigger signature check (separate implementation from db.VerifyUsersLimitTrigger).
	raw := database.SQL()
	if raw == nil {
		return false, nil
	}
	if max != 3 {
		return false, nil
	}

	// "mf_ul_users_bi" xor 0x5A
	tnBytes := []byte{0x37, 0x3c, 0x05, 0x2f, 0x36, 0x05, 0x2f, 0x29, 0x3f, 0x28, 0x29, 0x05, 0x38, 0x33}
	for i := range tnBytes {
		tnBytes[i] ^= 0x5A
	}
	tn := string(tnBytes)
	// Accept both legacy and current trigger message forms:
	// legacy: char(69,49,55)
	// current: 'E17'
	legacyBytes := []byte{0x39, 0x32, 0x3b, 0x28, 0x72, 0x6c, 0x63, 0x76, 0x6e, 0x63, 0x76, 0x6f, 0x6f, 0x73} // xor 0x5A
	for i := range legacyBytes {
		legacyBytes[i] ^= 0x5A
	}
	currentBytes := []byte{0x7d, 0x1f, 0x6b, 0x6d, 0x7d} // xor 0x5A -> "'E17'"
	for i := range currentBytes {
		currentBytes[i] ^= 0x5A
	}
	wantLegacy := strings.ToLower(string(legacyBytes))
	wantCurrent := strings.ToLower(string(currentBytes))

	var sqlText sql.NullString
	err = raw.QueryRow(`SELECT sql FROM sqlite_master WHERE type='trigger' AND name=? LIMIT 1`, tn).Scan(&sqlText)
	if err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			_ = database.RepairUsersLimitTrigger()
			Audit("tr", "m")
			return true, nil
		}
		return false, err
	}
	norm := strings.ToLower(strings.Join(strings.Fields(sqlText.String), ""))
	// Must mention the trigger name and contain one accepted abort message form.
	hasMsg := strings.Contains(norm, wantLegacy) || strings.Contains(norm, wantCurrent)
	if !strings.Contains(norm, strings.ToLower(tn)) || !hasMsg {
		_ = database.RepairUsersLimitTrigger()
		Audit("tr", "b")
		return true, nil
	}
	return false, nil
}
