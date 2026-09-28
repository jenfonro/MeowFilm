package emby

import (
	"strings"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/clientmeta"
)

func buildEmbyAuthUser(row db.UserAuthRow, serverID string, protocolUserID string, now time.Time) embyAuthUserDTO {
	createdAt := time.Unix(row.CreatedAt, 0).UTC()
	if row.CreatedAt <= 0 {
		createdAt = now.UTC()
	}
	return embyAuthUserDTO{
		Name:                  row.Username,
		ServerID:              serverID,
		Prefix:                clientmeta.Prefix(row.Username),
		DateCreated:           embyTime(createdAt),
		ID:                    protocolUserID,
		HasPassword:           true,
		HasConfiguredPassword: true,
		LastLoginDate:         embyTime(now),
		LastActivityDate:      embyTime(now),
		Configuration:         defaultUserConfiguration(),
		Policy:                defaultUserPolicy(strings.TrimSpace(row.Role) == "admin"),
	}
}
