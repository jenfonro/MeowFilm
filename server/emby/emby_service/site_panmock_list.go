package emby_service

import (
	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/catpawrunner"
	"github.com/jenfonro/meowfilm/server/smart"
)

// resolveSiteDetailRecordsForBrowse resolves browse/detail source records and
// derives edge Pans only for downstream browse presentation. The input records
// remain the sole internal processing model.
func resolveSiteDetailRecordsForBrowse(database *db.DB, rawRecords []smart.DetailSourceRecord) ([]catpawrunner.Pan, error) {
	if len(rawRecords) == 0 {
		return []catpawrunner.Pan{}, nil
	}
	resolved, _ := smart.ResolvePanMockSourceRecords(database, "", "", 0, nil, false, nil, nil, rawRecords)
	pans := smart.ResolvedRecordsToPans(resolved)
	if pans == nil {
		return []catpawrunner.Pan{}, nil
	}
	return pans, nil
}
