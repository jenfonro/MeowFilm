package db

import (
	"path/filepath"
	"reflect"
	"testing"
)

func newVideoSourceDeleteTestDB(t *testing.T) *DB {
	t.Helper()
	t.Setenv("MEOWFILM_DB_FILE", filepath.Join(t.TempDir(), "sites.db"))
	database, err := Open()
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = database.Close() })
	if err := database.ReplaceVideoSourceSites([]VideoSourceSite{
		{Key: "alpha", Name: "Site A", API: "/spider/alpha/3"},
		{Key: "beta", Name: "Site B", API: "/spider/beta/3"},
	}); err != nil {
		t.Fatal(err)
	}
	for index, key := range []string{"alpha", "beta"} {
		if err := database.UpsertVideoSourceSiteState(key, func(state *VideoSourceSiteState) {
			state.Enabled = key == "alpha"
			state.Home = true
			state.Search = true
			state.Availability = "invalid"
			state.Error = "preserved diagnostic"
			state.OrderIndex = index + 7
		}); err != nil {
			t.Fatal(err)
		}
	}
	if err := database.UpdateAppConfig(func(cfg *AppConfig) {
		cfg.SiteName = "keep-other-settings"
		cfg.VideoSourceSearchCoverSite = "alpha"
	}); err != nil {
		t.Fatal(err)
	}
	return database
}

func TestDeleteVideoSourceSiteRemovesStateAndRefreshesCachedReads(t *testing.T) {
	database := newVideoSourceDeleteTestDB(t)
	beforeStates, err := database.ReadVideoSourceSiteStates()
	if err != nil {
		t.Fatal(err)
	}
	if rows, err := database.ListVideoSourceSites(); err != nil || len(rows) != 2 {
		t.Fatalf("warm sites cache: rows=%v err=%v", rows, err)
	}
	if cfg, err := database.ReadAppConfig(); err != nil || cfg.VideoSourceSearchCoverSite != "alpha" {
		t.Fatalf("warm config cache: cfg=%v err=%v", cfg, err)
	}
	deleted, err := database.DeleteVideoSourceSite(" alpha ")
	if err != nil || !deleted {
		t.Fatalf("delete: deleted=%v err=%v", deleted, err)
	}
	rows, err := database.ListVideoSourceSites()
	if err != nil || len(rows) != 1 || rows[0].Key != "beta" {
		t.Fatalf("stale or incorrect sites: rows=%v err=%v", rows, err)
	}
	states, err := database.ReadVideoSourceSiteStates()
	if err != nil {
		t.Fatal(err)
	}
	if _, exists := states["alpha"]; exists {
		t.Fatal("deleted site still has state")
	}
	if !reflect.DeepEqual(states["beta"], beforeStates["beta"]) {
		t.Fatal("another site's flags, diagnostics or order changed")
	}
	cfg, err := database.ReadAppConfig()
	if err != nil || cfg.VideoSourceSearchCoverSite != "" || cfg.SiteName != "keep-other-settings" {
		t.Fatalf("cover not cleared or unrelated config changed: cfg=%v err=%v", cfg, err)
	}

	// A fresh DB instance must also see the deletion, not just this process's cache.
	reopened, err := Open()
	if err != nil {
		t.Fatal(err)
	}
	defer reopened.Close()
	rows, err = reopened.ListVideoSourceSites()
	if err != nil || len(rows) != 1 || rows[0].Key != "beta" {
		t.Fatalf("deletion was not persisted: rows=%v err=%v", rows, err)
	}
	cfg, err = reopened.ReadAppConfig()
	if err != nil || cfg.VideoSourceSearchCoverSite != "" {
		t.Fatalf("deleted cover was not persisted: cfg=%v err=%v", cfg, err)
	}
}

func TestDeleteVideoSourceSitePreservesOtherCoverAndAllowsEmptyList(t *testing.T) {
	database := newVideoSourceDeleteTestDB(t)
	if deleted, err := database.DeleteVideoSourceSite("beta"); err != nil || !deleted {
		t.Fatalf("delete other site: deleted=%v err=%v", deleted, err)
	}
	cfg, err := database.ReadAppConfig()
	if err != nil || cfg.VideoSourceSearchCoverSite != "alpha" {
		t.Fatalf("unrelated cover changed: cfg=%v err=%v", cfg, err)
	}
	if deleted, err := database.DeleteVideoSourceSite("alpha"); err != nil || !deleted {
		t.Fatalf("delete last site: deleted=%v err=%v", deleted, err)
	}
	rows, err := database.ListVideoSourceSites()
	if err != nil || len(rows) != 0 {
		t.Fatalf("last site remained: rows=%v err=%v", rows, err)
	}
	states, err := database.ReadVideoSourceSiteStates()
	if err != nil || len(states) != 0 {
		t.Fatalf("orphaned states: states=%v err=%v", states, err)
	}
}

func TestDeleteVideoSourceSiteRejectsBlankAndDoesNotDeleteUnknownKeys(t *testing.T) {
	database := newVideoSourceDeleteTestDB(t)
	for _, key := range []string{"", " \n ", "missing", "alpha' OR 1=1 --"} {
		deleted, err := database.DeleteVideoSourceSite(key)
		if deleted {
			t.Fatalf("unexpected deletion for %q", key)
		}
		if (key == "" || key == " \n ") && err == nil {
			t.Fatalf("blank key accepted: %q", key)
		}
		if rows, err := database.ListVideoSourceSites(); err != nil || len(rows) != 2 {
			t.Fatalf("sites changed for %q: rows=%v err=%v", key, rows, err)
		}
	}
	var unavailable *DB
	if deleted, err := unavailable.DeleteVideoSourceSite("alpha"); deleted || err == nil {
		t.Fatal("unavailable database reported success")
	}
}

func TestDeleteVideoSourceSiteRollsBackAllChangesOnFailure(t *testing.T) {
	database := newVideoSourceDeleteTestDB(t)
	beforeRows, _ := database.ListVideoSourceSites()
	beforeStates, _ := database.ReadVideoSourceSiteStates()
	beforeConfig, _ := database.ReadAppConfig()
	if _, err := database.db.Exec(`
		CREATE TRIGGER reject_cover_update BEFORE UPDATE ON app_video_source
		BEGIN SELECT RAISE(ABORT, 'test failure'); END
	`); err != nil {
		t.Fatal(err)
	}
	if deleted, err := database.DeleteVideoSourceSite("alpha"); deleted || err == nil {
		t.Fatal("failed transaction reported successful deletion")
	}
	rows, err := database.loadVideoSourceSitesFromDB()
	if err != nil || !reflect.DeepEqual(rows, beforeRows) {
		t.Fatalf("site deletion was not rolled back: rows=%v err=%v", rows, err)
	}
	states, err := database.ReadVideoSourceSiteStates()
	if err != nil || !reflect.DeepEqual(states, beforeStates) {
		t.Fatalf("state deletion was not rolled back: states=%v err=%v", states, err)
	}
	cfg, err := database.ReadAppConfig()
	if err != nil || !reflect.DeepEqual(cfg, beforeConfig) {
		t.Fatalf("cached config changed after rollback: cfg=%v err=%v", cfg, err)
	}
}
