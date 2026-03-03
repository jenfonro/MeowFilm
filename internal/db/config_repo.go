package db

import (
	"database/sql"
	"encoding/json"
	"errors"
	"sort"
	"strings"
	"time"
	"unicode"
)

type AppConfig struct {
	SiteName                   string
	DoubanDataProxy            string
	DoubanDataCustom           string
	DoubanImgProxy             string
	DoubanImgCustom            string
	VideoSourceAPIBase         string
	VideoSourceSearchCoverSite string
	SearchDisplayMode          string
	SmartSourceExtractPriority string
	SmartSiteCleanKeywords     string
	GoProxyEnabled             bool
	GoProxyAutoSelect          bool
	NetdiskProxyEnabled        bool
	NetdiskProxyURL            string
	TMDBAPIToken               string
	TMDBAPIBase                string
	TMDBImgBase                string
	TMDBLanguage               string
	TMDBRegion                 string
	TMDBIncludeAdult           bool
	CatpawrunnerActive         string
}

func (d *DB) ReadAppConfig() (AppConfig, error) {
	if d == nil || d.db == nil {
		return AppConfig{}, nil
	}

	var (
		siteName                                       sql.NullString
		dDataProxy, dDataCustom, dImgProxy, dImgCustom sql.NullString
		vsAPIBase, vsCover                             sql.NullString
		sDisplay                                       sql.NullString
		smartPriority                                  sql.NullString
		smartSiteClean                                 sql.NullString
		gEnabled, gAuto                                sql.NullInt64
		ndEnabled                                      sql.NullInt64
		ndProxy                                        sql.NullString
		tToken, tAPIBase, tImgBase, tLang, tRegion     sql.NullString
		tAdult                                         sql.NullInt64
		cActive                                        sql.NullString
	)

	_ = d.db.QueryRow(`SELECT site_name FROM app_site WHERE id=1 LIMIT 1`).Scan(&siteName)
	_ = d.db.QueryRow(`SELECT data_proxy, data_custom, img_proxy, img_custom FROM app_douban WHERE id=1 LIMIT 1`).Scan(&dDataProxy, &dDataCustom, &dImgProxy, &dImgCustom)
	_ = d.db.QueryRow(`SELECT api_base, search_cover_site FROM app_video_source WHERE id=1 LIMIT 1`).Scan(&vsAPIBase, &vsCover)
	_ = d.db.QueryRow(`SELECT display_mode FROM app_search WHERE id=1 LIMIT 1`).Scan(&sDisplay)
	_ = d.db.QueryRow(`SELECT source_extract_priority, site_clean_keywords FROM app_smart WHERE id=1 LIMIT 1`).Scan(&smartPriority, &smartSiteClean)
	_ = d.db.QueryRow(`SELECT enabled, auto_select FROM app_goproxy WHERE id=1 LIMIT 1`).Scan(&gEnabled, &gAuto)
	_ = d.db.QueryRow(`SELECT enabled, proxy_url FROM app_netdisk_proxy WHERE id=1 LIMIT 1`).Scan(&ndEnabled, &ndProxy)
	_ = d.db.QueryRow(`SELECT api_token, api_base, img_base, language, region, include_adult FROM app_tmdb WHERE id=1 LIMIT 1`).Scan(&tToken, &tAPIBase, &tImgBase, &tLang, &tRegion, &tAdult)
	_ = d.db.QueryRow(`SELECT active FROM app_catpawrunner WHERE id=1 LIMIT 1`).Scan(&cActive)

	cfg := AppConfig{
		SiteName:                   defaultIfEmpty(siteName.String, "MeowFilm"),
		DoubanDataProxy:            defaultIfEmpty(dDataProxy.String, "server-proxy"),
		DoubanDataCustom:           dDataCustom.String,
		DoubanImgProxy:             defaultIfEmpty(dImgProxy.String, "server-proxy"),
		DoubanImgCustom:            dImgCustom.String,
		VideoSourceAPIBase:         vsAPIBase.String,
		VideoSourceSearchCoverSite: vsCover.String,
		SearchDisplayMode:          defaultIfEmpty(sDisplay.String, "sites"),
		SmartSourceExtractPriority: defaultIfEmpty(smartPriority.String, "无"),
		SmartSiteCleanKeywords:     strings.TrimSpace(smartSiteClean.String),
		GoProxyEnabled:             gEnabled.Int64 != 0,
		GoProxyAutoSelect:          gAuto.Int64 != 0,
		NetdiskProxyEnabled:        ndEnabled.Int64 != 0,
		NetdiskProxyURL:            strings.TrimSpace(ndProxy.String),
		TMDBAPIToken:               tToken.String,
		TMDBAPIBase:                tAPIBase.String,
		TMDBImgBase:                tImgBase.String,
		TMDBLanguage:               defaultIfEmpty(tLang.String, "zh-CN"),
		TMDBRegion:                 defaultIfEmpty(tRegion.String, "CN"),
		TMDBIncludeAdult:           tAdult.Int64 != 0,
		CatpawrunnerActive:         cActive.String,
	}
	return cfg, nil
}

func (d *DB) UpdateAppConfig(update func(*AppConfig)) error {
	if d == nil || d.db == nil {
		return nil
	}
	cfg, err := d.ReadAppConfig()
	if err != nil {
		return err
	}
	update(&cfg)

	now := time.Now().Unix()
	tx, err := d.db.Begin()
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback() }()

	_, _ = tx.Exec(`
		INSERT INTO app_site(id, site_name, updated_at)
		VALUES(1, ?, ?)
		ON CONFLICT(id) DO UPDATE SET site_name=excluded.site_name, updated_at=excluded.updated_at
	`, strings.TrimSpace(cfg.SiteName), now)

	_, _ = tx.Exec(`
		INSERT INTO app_douban(id, data_proxy, data_custom, img_proxy, img_custom, updated_at)
		VALUES(1, ?, ?, ?, ?, ?)
		ON CONFLICT(id) DO UPDATE SET
		  data_proxy=excluded.data_proxy,
		  data_custom=excluded.data_custom,
		  img_proxy=excluded.img_proxy,
		  img_custom=excluded.img_custom,
		  updated_at=excluded.updated_at
	`, strings.TrimSpace(cfg.DoubanDataProxy), strings.TrimSpace(cfg.DoubanDataCustom),
		strings.TrimSpace(cfg.DoubanImgProxy), strings.TrimSpace(cfg.DoubanImgCustom), now)

	_, _ = tx.Exec(`
		INSERT INTO app_video_source(id, api_base, search_cover_site, updated_at)
		VALUES(1, ?, ?, ?)
		ON CONFLICT(id) DO UPDATE SET api_base=excluded.api_base, search_cover_site=excluded.search_cover_site, updated_at=excluded.updated_at
	`, strings.TrimSpace(cfg.VideoSourceAPIBase), strings.TrimSpace(cfg.VideoSourceSearchCoverSite), now)

	_, _ = tx.Exec(`
		INSERT INTO app_search(id, display_mode, updated_at)
		VALUES(1, ?, ?)
		ON CONFLICT(id) DO UPDATE SET display_mode=excluded.display_mode, updated_at=excluded.updated_at
	`, strings.TrimSpace(cfg.SearchDisplayMode), now)

	_, _ = tx.Exec(`
		INSERT INTO app_smart(id, source_extract_priority, site_clean_keywords, updated_at)
		VALUES(1, ?, ?, ?)
		ON CONFLICT(id) DO UPDATE SET
		  source_extract_priority=excluded.source_extract_priority,
		  site_clean_keywords=excluded.site_clean_keywords,
		  updated_at=excluded.updated_at
	`, strings.TrimSpace(cfg.SmartSourceExtractPriority), strings.TrimSpace(cfg.SmartSiteCleanKeywords), now)

	_, _ = tx.Exec(`
		INSERT INTO app_goproxy(id, enabled, auto_select, updated_at)
		VALUES(1, ?, ?, ?)
		ON CONFLICT(id) DO UPDATE SET enabled=excluded.enabled, auto_select=excluded.auto_select, updated_at=excluded.updated_at
	`, bool01Int(cfg.GoProxyEnabled), bool01Int(cfg.GoProxyAutoSelect), now)

	_, _ = tx.Exec(`
		INSERT INTO app_netdisk_proxy(id, enabled, proxy_url, updated_at)
		VALUES(1, ?, ?, ?)
		ON CONFLICT(id) DO UPDATE SET enabled=excluded.enabled, proxy_url=excluded.proxy_url, updated_at=excluded.updated_at
	`, bool01Int(cfg.NetdiskProxyEnabled), strings.TrimSpace(cfg.NetdiskProxyURL), now)

	_, _ = tx.Exec(`
		INSERT INTO app_tmdb(id, api_token, api_base, img_base, language, region, include_adult, updated_at)
		VALUES(1, ?, ?, ?, ?, ?, ?, ?)
		ON CONFLICT(id) DO UPDATE SET
		  api_token=excluded.api_token,
		  api_base=excluded.api_base,
		  img_base=excluded.img_base,
		  language=excluded.language,
		  region=excluded.region,
		  include_adult=excluded.include_adult,
		  updated_at=excluded.updated_at
	`, strings.TrimSpace(cfg.TMDBAPIToken), strings.TrimSpace(cfg.TMDBAPIBase), strings.TrimSpace(cfg.TMDBImgBase),
		strings.TrimSpace(cfg.TMDBLanguage), strings.TrimSpace(cfg.TMDBRegion), bool01Int(cfg.TMDBIncludeAdult), now)

	_, _ = tx.Exec(`
		INSERT INTO app_catpawrunner(id, active, updated_at)
		VALUES(1, ?, ?)
		ON CONFLICT(id) DO UPDATE SET active=excluded.active, updated_at=excluded.updated_at
	`, strings.TrimSpace(cfg.CatpawrunnerActive), now)

	return tx.Commit()
}

type CatpawrunnerServer struct {
	Name    string
	APIBase string
}

func (d *DB) ListcatpawrunnerServers() ([]CatpawrunnerServer, error) {
	rows, err := d.db.Query(`SELECT name, api_base FROM catpawrunner_server ORDER BY order_index ASC, name ASC`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	out := []CatpawrunnerServer{}
	for rows.Next() {
		var it CatpawrunnerServer
		_ = rows.Scan(&it.Name, &it.APIBase)
		it.Name = strings.TrimSpace(it.Name)
		it.APIBase = strings.TrimSpace(it.APIBase)
		if it.Name == "" || it.APIBase == "" {
			continue
		}
		out = append(out, it)
	}
	return out, nil
}

func (d *DB) ReplacecatpawrunnerServers(servers []CatpawrunnerServer) error {
	if d == nil || d.db == nil {
		return nil
	}
	now := time.Now().Unix()
	seen := map[string]struct{}{}
	list := make([]CatpawrunnerServer, 0, len(servers))
	for _, it := range servers {
		it.Name = strings.TrimSpace(it.Name)
		it.APIBase = strings.TrimSpace(it.APIBase)
		if it.Name == "" || it.APIBase == "" {
			continue
		}
		if _, ok := seen[it.Name]; ok {
			continue
		}
		seen[it.Name] = struct{}{}
		list = append(list, it)
	}
	tx, err := d.db.Begin()
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback() }()
	if _, err := tx.Exec(`DELETE FROM catpawrunner_server`); err != nil {
		return err
	}
	for i, it := range list {
		if _, err := tx.Exec(`INSERT INTO catpawrunner_server(name, api_base, order_index, updated_at) VALUES(?,?,?,?)`, it.Name, it.APIBase, i, now); err != nil {
			return err
		}
	}
	return tx.Commit()
}

type CatpawrunnerPan struct {
	Key     string
	Name    string
	Enabled bool
}

func (d *DB) ListcatpawrunnerPans() ([]CatpawrunnerPan, error) {
	rows, err := d.db.Query(`SELECT key, name, enabled FROM catpawrunner_pan ORDER BY key ASC`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	out := []CatpawrunnerPan{}
	for rows.Next() {
		var (
			key  string
			name string
			en   int
		)
		_ = rows.Scan(&key, &name, &en)
		key = strings.TrimSpace(key)
		if key == "" {
			continue
		}
		out = append(out, CatpawrunnerPan{Key: key, Name: name, Enabled: en != 0})
	}
	return out, nil
}

func (d *DB) ReplacecatpawrunnerPans(pans []CatpawrunnerPan) error {
	now := time.Now().Unix()
	seen := map[string]struct{}{}
	list := make([]CatpawrunnerPan, 0, len(pans))
	for _, it := range pans {
		it.Key = strings.TrimSpace(it.Key)
		if it.Key == "" {
			continue
		}
		if _, ok := seen[it.Key]; ok {
			continue
		}
		seen[it.Key] = struct{}{}
		list = append(list, it)
	}
	tx, err := d.db.Begin()
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback() }()
	if _, err := tx.Exec(`DELETE FROM catpawrunner_pan`); err != nil {
		return err
	}
	for _, it := range list {
		en := 0
		if it.Enabled {
			en = 1
		}
		if _, err := tx.Exec(`INSERT INTO catpawrunner_pan(key, name, enabled, updated_at) VALUES(?,?,?,?)`, it.Key, it.Name, en, now); err != nil {
			return err
		}
	}
	return tx.Commit()
}

type GoProxyServer struct {
	Name        string
	DisplayName string
	Base        string
	PansBaidu   bool
	PansQuark   bool
}

func (d *DB) ListGoProxyServers() ([]GoProxyServer, error) {
	rows, err := d.db.Query(`SELECT name, display_name, base, pans_baidu, pans_quark FROM goproxy_server ORDER BY order_index ASC, name ASC`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	out := []GoProxyServer{}
	for rows.Next() {
		var (
			name        string
			displayName string
			base        string
			bd          int
			qk          int
		)
		_ = rows.Scan(&name, &displayName, &base, &bd, &qk)
		name = strings.TrimSpace(name)
		base = strings.TrimSpace(base)
		if name == "" || base == "" {
			continue
		}
		out = append(out, GoProxyServer{Name: name, DisplayName: displayName, Base: base, PansBaidu: bd != 0, PansQuark: qk != 0})
	}
	return out, nil
}

func (d *DB) ReplaceGoProxyServers(servers []GoProxyServer) error {
	now := time.Now().Unix()
	seen := map[string]struct{}{}
	list := make([]GoProxyServer, 0, len(servers))
	for _, it := range servers {
		it.Name = strings.TrimSpace(it.Name)
		it.Base = strings.TrimSpace(it.Base)
		if it.Name == "" || it.Base == "" {
			continue
		}
		if _, ok := seen[it.Name]; ok {
			continue
		}
		seen[it.Name] = struct{}{}
		list = append(list, it)
	}
	tx, err := d.db.Begin()
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback() }()
	if _, err := tx.Exec(`DELETE FROM goproxy_server`); err != nil {
		return err
	}
	for i, it := range list {
		bd := 0
		qk := 0
		if it.PansBaidu {
			bd = 1
		}
		if it.PansQuark {
			qk = 1
		}
		if _, err := tx.Exec(`INSERT INTO goproxy_server(name, display_name, base, pans_baidu, pans_quark, order_index, updated_at) VALUES(?,?,?,?,?,?,?)`,
			it.Name, it.DisplayName, it.Base, bd, qk, i, now,
		); err != nil {
			return err
		}
	}
	return tx.Commit()
}

type VideoSourceSite struct {
	Key  string
	Name string
	API  string
	Type *int
}

type VideoSourceSiteState struct {
	Enabled      bool
	Home         bool
	Search       bool
	SmartSkip    bool
	Availability string
	Error        string
	OrderIndex   int
}

func (d *DB) ListVideoSourceSites() ([]VideoSourceSite, error) {
	rows, err := d.db.Query(`SELECT key, name, api, type FROM video_source_site ORDER BY key ASC`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	out := []VideoSourceSite{}
	for rows.Next() {
		var (
			key  string
			name string
			api  string
			typ  sql.NullInt64
		)
		_ = rows.Scan(&key, &name, &api, &typ)
		key = strings.TrimSpace(key)
		api = strings.TrimSpace(api)
		if key == "" || api == "" {
			continue
		}
		var tptr *int
		if typ.Valid {
			n := int(typ.Int64)
			tptr = &n
		}
		out = append(out, VideoSourceSite{Key: key, Name: name, API: api, Type: tptr})
	}
	return out, nil
}

func (d *DB) ReplaceVideoSourceSites(sites []VideoSourceSite) error {
	now := time.Now().Unix()
	seen := map[string]struct{}{}
	list := make([]VideoSourceSite, 0, len(sites))
	for _, it := range sites {
		it.Key = strings.TrimSpace(it.Key)
		it.API = strings.TrimSpace(it.API)
		if it.Key == "" || it.API == "" {
			continue
		}
		if _, ok := seen[it.Key]; ok {
			continue
		}
		seen[it.Key] = struct{}{}
		list = append(list, it)
	}
	tx, err := d.db.Begin()
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback() }()
	if _, err := tx.Exec(`DELETE FROM video_source_site`); err != nil {
		return err
	}
	for _, it := range list {
		if _, err := tx.Exec(`INSERT INTO video_source_site(key, name, api, type, updated_at) VALUES(?,?,?,?,?)`,
			it.Key, it.Name, it.API, it.Type, now,
		); err != nil {
			return err
		}
	}
	if err := tx.Commit(); err != nil {
		return err
	}
	_ = d.RecomputeSmartSkipSites()
	return nil
}

func (d *DB) ReadVideoSourceSiteStates() (map[string]VideoSourceSiteState, error) {
	rows, err := d.db.Query(`SELECT site_key, enabled, home, search, smart_skip, availability, error, order_index FROM video_source_site_state`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	out := map[string]VideoSourceSiteState{}
	for rows.Next() {
		var (
			key       string
			en        int
			home      int
			search    int
			smartSkip int
			avail     string
			errStr    string
			ord       int
		)
		_ = rows.Scan(&key, &en, &home, &search, &smartSkip, &avail, &errStr, &ord)
		key = strings.TrimSpace(key)
		if key == "" {
			continue
		}
		out[key] = VideoSourceSiteState{
			Enabled:      en != 0,
			Home:         home != 0,
			Search:       search != 0,
			SmartSkip:    smartSkip != 0,
			Availability: strings.TrimSpace(avail),
			Error:        strings.TrimSpace(errStr),
			OrderIndex:   ord,
		}
	}
	return out, nil
}

func (d *DB) UpsertVideoSourceSiteState(key string, patch func(*VideoSourceSiteState)) error {
	k := strings.TrimSpace(key)
	if k == "" {
		return errors.New("site key empty")
	}
	states, err := d.ReadVideoSourceSiteStates()
	if err != nil {
		return err
	}
	s := states[k]
	patch(&s)
	if strings.TrimSpace(s.Availability) == "" {
		s.Availability = "unchecked"
	}
	now := time.Now().Unix()
	_, err = d.db.Exec(`
		INSERT INTO video_source_site_state(site_key, enabled, home, search, availability, error, order_index, updated_at)
		VALUES(?,?,?,?,?,?,?,?)
		ON CONFLICT(site_key) DO UPDATE SET
		  enabled=excluded.enabled,
		  home=excluded.home,
		  search=excluded.search,
		  availability=excluded.availability,
		  error=excluded.error,
		  order_index=excluded.order_index,
		  updated_at=excluded.updated_at
	`, k, bool01Int(s.Enabled), bool01Int(s.Home), bool01Int(s.Search), s.Availability, s.Error, s.OrderIndex, now)
	return err
}

func (d *DB) ReplaceVideoSourceSiteOrder(order []string) error {
	now := time.Now().Unix()
	seen := map[string]struct{}{}
	next := []string{}
	for _, k := range order {
		key := strings.TrimSpace(k)
		if key == "" {
			continue
		}
		if _, ok := seen[key]; ok {
			continue
		}
		seen[key] = struct{}{}
		next = append(next, key)
	}
	tx, err := d.db.Begin()
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback() }()
	_, _ = tx.Exec(`UPDATE video_source_site_state SET order_index = 1000000000, updated_at = ?`, now)
	for i, key := range next {
		if _, err := tx.Exec(`
			INSERT INTO video_source_site_state(site_key, order_index, updated_at)
			VALUES(?,?,?)
			ON CONFLICT(site_key) DO UPDATE SET order_index=excluded.order_index, updated_at=excluded.updated_at
		`, key, i, now); err != nil {
			return err
		}
	}
	return tx.Commit()
}

func (d *DB) ListSmartSkipSiteKeys() ([]string, error) {
	if d == nil || d.db == nil {
		return []string{}, nil
	}
	rows, err := d.db.Query(`SELECT site_key FROM video_source_site_state WHERE smart_skip != 0 ORDER BY order_index ASC, site_key ASC`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	out := []string{}
	for rows.Next() {
		var k string
		_ = rows.Scan(&k)
		k = strings.TrimSpace(k)
		if k == "" {
			continue
		}
		out = append(out, k)
	}
	return out, nil
}

func (d *DB) RecomputeSmartSkipSites() error {
	if d == nil || d.db == nil {
		return nil
	}
	cfg, err := d.ReadAppConfig()
	if err != nil {
		return err
	}
	keywords := normalizeSmartSiteCleanKeywords(cfg.SmartSiteCleanKeywords)
	rawSites, err := d.ListVideoSourceSites()
	if err != nil {
		return err
	}

	now := time.Now().Unix()
	tx, err := d.db.Begin()
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback() }()

	for _, s := range rawSites {
		key := strings.TrimSpace(s.Key)
		if key == "" {
			continue
		}
		skip := smartSiteNameMatchesKeywords(s.Name, keywords)
		if _, err := tx.Exec(`
			INSERT INTO video_source_site_state(site_key, smart_skip, updated_at)
			VALUES(?,?,?)
			ON CONFLICT(site_key) DO UPDATE SET smart_skip=excluded.smart_skip, updated_at=excluded.updated_at
		`, key, bool01Int(skip), now); err != nil {
			return err
		}
	}

	return tx.Commit()
}

func normalizeSmartSiteCleanKeywords(text string) []string {
	raw := strings.TrimSpace(text)
	if raw == "" {
		return []string{}
	}
	parts := strings.Split(strings.ReplaceAll(raw, "，", ","), ",")
	seen := map[string]struct{}{}
	out := []string{}
	for _, p := range parts {
		s := normalizeSiteNameToken(p)
		if s == "" {
			continue
		}
		if _, ok := seen[s]; ok {
			continue
		}
		seen[s] = struct{}{}
		out = append(out, s)
	}
	return out
}

func normalizeSiteNameToken(text string) string {
	raw := strings.TrimSpace(text)
	if raw == "" {
		return ""
	}
	var b strings.Builder
	b.Grow(len(raw))
	for _, r := range raw {
		if unicode.IsLetter(r) || unicode.IsNumber(r) {
			b.WriteRune(unicode.ToLower(r))
			continue
		}
		if r >= 0x4E00 && r <= 0x9FFF {
			b.WriteRune(r)
		}
	}
	return b.String()
}

func smartSiteNameMatchesKeywords(siteName string, keywords []string) bool {
	if len(keywords) == 0 {
		return false
	}
	n := normalizeSiteNameToken(siteName)
	if n == "" {
		return false
	}
	for _, kw := range keywords {
		if kw == "" {
			continue
		}
		if strings.Contains(n, kw) {
			return true
		}
	}
	return false
}

func (d *DB) ReadPanLoginSettings() (map[string]map[string]any, error) {
	rows, err := d.db.Query(`SELECT provider, field, value FROM pan_login_setting ORDER BY provider ASC, field ASC`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	root := map[string]map[string]any{}
	for rows.Next() {
		var p, f, v string
		_ = rows.Scan(&p, &f, &v)
		p = strings.TrimSpace(p)
		f = strings.TrimSpace(f)
		if p == "" || f == "" {
			continue
		}
		m := root[p]
		if m == nil {
			m = map[string]any{}
		}
		var anyVal any
		if err := json.Unmarshal([]byte(v), &anyVal); err == nil {
			m[f] = anyVal
		} else {
			m[f] = v
		}
		root[p] = m
	}
	return root, nil
}

func (d *DB) ReplacePanLoginSettings(root map[string]map[string]any) error {
	type row struct {
		provider string
		field    string
		value    string
	}
	rows := []row{}
	for pk, pv := range root {
		provider := strings.TrimSpace(pk)
		if provider == "" || pv == nil {
			continue
		}
		for fk, fv := range pv {
			field := strings.TrimSpace(fk)
			if field == "" {
				continue
			}
			b, err := json.Marshal(fv)
			if err != nil {
				continue
			}
			rows = append(rows, row{provider: provider, field: field, value: string(b)})
		}
	}
	sort.Slice(rows, func(i, j int) bool {
		if rows[i].provider != rows[j].provider {
			return rows[i].provider < rows[j].provider
		}
		return rows[i].field < rows[j].field
	})
	tx, err := d.db.Begin()
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback() }()
	if _, err := tx.Exec(`DELETE FROM pan_login_setting`); err != nil {
		return err
	}
	for _, it := range rows {
		if _, err := tx.Exec(`INSERT INTO pan_login_setting(provider, field, value) VALUES(?,?,?)`, it.provider, it.field, it.value); err != nil {
			return err
		}
	}
	return tx.Commit()
}

func (d *DB) listStringTable(table, col string) ([]string, error) {
	t := strings.TrimSpace(table)
	c := strings.TrimSpace(col)
	if t == "" || c == "" {
		return nil, errors.New("invalid table")
	}
	rows, err := d.db.Query(`SELECT ` + c + ` FROM ` + t + ` ORDER BY pos ASC`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	out := []string{}
	for rows.Next() {
		var s sql.NullString
		_ = rows.Scan(&s)
		if s.Valid && strings.TrimSpace(s.String) != "" {
			out = append(out, s.String)
		}
	}
	return out, nil
}

func (d *DB) replaceStringTable(table, col string, list []string) error {
	t := strings.TrimSpace(table)
	c := strings.TrimSpace(col)
	if t == "" || c == "" {
		return errors.New("invalid table")
	}
	tx, err := d.db.Begin()
	if err != nil {
		return err
	}
	defer func() { _ = tx.Rollback() }()
	if _, err := tx.Exec(`DELETE FROM ` + t); err != nil {
		return err
	}
	pos := 0
	for _, it := range list {
		s := strings.TrimSpace(it)
		if s == "" {
			continue
		}
		if _, err := tx.Exec(`INSERT INTO `+t+`(pos, `+c+`) VALUES(?, ?)`, pos, s); err != nil {
			return err
		}
		pos++
	}
	return tx.Commit()
}

func (d *DB) ListMagicEpisodeRules() ([]string, error) {
	return d.listStringTable("magic_episode_rule", "rule_text")
}
func (d *DB) ReplaceMagicEpisodeRules(list []string) error {
	return d.replaceStringTable("magic_episode_rule", "rule_text", list)
}
func (d *DB) ListMagicEpisodeCleanRegexRules() ([]string, error) {
	return d.listStringTable("magic_episode_clean_regex_rule", "pattern")
}
func (d *DB) ReplaceMagicEpisodeCleanRegexRules(list []string) error {
	return d.replaceStringTable("magic_episode_clean_regex_rule", "pattern", list)
}
func (d *DB) ListMagicMovieRules() ([]string, error) {
	return d.listStringTable("magic_movie_rule", "rule_text")
}
func (d *DB) ReplaceMagicMovieRules(list []string) error {
	return d.replaceStringTable("magic_movie_rule", "rule_text", list)
}
func (d *DB) ListMagicAggregateRegexRules() ([]string, error) {
	return d.listStringTable("magic_aggregate_regex_rule", "pattern")
}
func (d *DB) ReplaceMagicAggregateRegexRules(list []string) error {
	return d.replaceStringTable("magic_aggregate_regex_rule", "pattern", list)
}
func (d *DB) ListSmartSourcePriorityTokens() ([]string, error) {
	return d.listStringTable("smart_source_priority_token", "token")
}
func (d *DB) ReplaceSmartSourcePriorityTokens(list []string) error {
	return d.replaceStringTable("smart_source_priority_token", "token", list)
}
func (d *DB) ListSmartPanMatchTokens() ([]string, error) {
	return d.listStringTable("smart_pan_match_token", "token")
}
func (d *DB) ReplaceSmartPanMatchTokens(list []string) error {
	return d.replaceStringTable("smart_pan_match_token", "token", list)
}

func bool01Int(b bool) int {
	if b {
		return 1
	}
	return 0
}

func defaultIfEmpty(v, def string) string {
	if strings.TrimSpace(v) == "" {
		return def
	}
	return v
}
