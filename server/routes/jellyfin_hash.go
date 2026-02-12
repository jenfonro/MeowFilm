package routes

import (
	"crypto/md5"
	"encoding/hex"
	"strings"
)

func jellyfinStableHex32(s string) string {
	sum := md5.Sum([]byte(strings.TrimSpace(s)))
	return hex.EncodeToString(sum[:])
}
