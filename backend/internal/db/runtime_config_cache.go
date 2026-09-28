package db

func cloneAppConfig(cfg AppConfig) AppConfig {
	out := cfg
	if len(cfg.SmartSourceRuleRows) > 0 {
		out.SmartSourceRuleRows = append([]SmartSourceRuleRow(nil), cfg.SmartSourceRuleRows...)
	} else {
		out.SmartSourceRuleRows = nil
	}
	return out
}

func cloneCatpawrunnerServers(rows []CatpawrunnerServer) []CatpawrunnerServer {
	if len(rows) == 0 {
		return []CatpawrunnerServer{}
	}
	out := make([]CatpawrunnerServer, len(rows))
	copy(out, rows)
	return out
}

func cloneVideoSourceSites(rows []VideoSourceSite) []VideoSourceSite {
	if len(rows) == 0 {
		return []VideoSourceSite{}
	}
	out := make([]VideoSourceSite, 0, len(rows))
	for _, row := range rows {
		cp := row
		if row.Type != nil {
			v := *row.Type
			cp.Type = &v
		}
		out = append(out, cp)
	}
	return out
}

func (d *DB) readAppConfigCache() (AppConfig, bool) {
	if d == nil {
		return AppConfig{}, false
	}
	d.mu.RLock()
	defer d.mu.RUnlock()
	if !d.appConfigCached {
		return AppConfig{}, false
	}
	return cloneAppConfig(d.appConfigCache), true
}

func (d *DB) setAppConfigCache(cfg AppConfig) {
	if d == nil {
		return
	}
	d.mu.Lock()
	d.appConfigCache = cloneAppConfig(cfg)
	d.appConfigCached = true
	d.mu.Unlock()
}

func (d *DB) refreshAppConfigCache() error {
	cfg, err := d.loadAppConfigFromDB()
	if err != nil {
		return err
	}
	d.setAppConfigCache(cfg)
	return nil
}

func (d *DB) readCatpawrunnerServersCache() ([]CatpawrunnerServer, bool) {
	if d == nil {
		return []CatpawrunnerServer{}, false
	}
	d.mu.RLock()
	defer d.mu.RUnlock()
	if !d.catpawrunnerServersCached {
		return []CatpawrunnerServer{}, false
	}
	return cloneCatpawrunnerServers(d.catpawrunnerServersCache), true
}

func (d *DB) setCatpawrunnerServersCache(rows []CatpawrunnerServer) {
	if d == nil {
		return
	}
	d.mu.Lock()
	d.catpawrunnerServersCache = cloneCatpawrunnerServers(rows)
	d.catpawrunnerServersCached = true
	d.mu.Unlock()
}

func (d *DB) refreshCatpawrunnerServersCache() error {
	rows, err := d.loadCatpawrunnerServersFromDB()
	if err != nil {
		return err
	}
	d.setCatpawrunnerServersCache(rows)
	return nil
}

func (d *DB) readVideoSourceSitesCache() ([]VideoSourceSite, bool) {
	if d == nil {
		return []VideoSourceSite{}, false
	}
	d.mu.RLock()
	defer d.mu.RUnlock()
	if !d.videoSourceSitesCached {
		return []VideoSourceSite{}, false
	}
	return cloneVideoSourceSites(d.videoSourceSitesCache), true
}

func (d *DB) setVideoSourceSitesCache(rows []VideoSourceSite) {
	if d == nil {
		return
	}
	d.mu.Lock()
	d.videoSourceSitesCache = cloneVideoSourceSites(rows)
	d.videoSourceSitesCached = true
	d.mu.Unlock()
}

func (d *DB) refreshVideoSourceSitesCache() error {
	rows, err := d.loadVideoSourceSitesFromDB()
	if err != nil {
		return err
	}
	d.setVideoSourceSitesCache(rows)
	return nil
}

func (d *DB) WarmRuntimeConfigSnapshot() error {
	if d == nil || d.db == nil {
		return nil
	}
	if err := d.refreshAppConfigCache(); err != nil {
		return err
	}
	if err := d.refreshCatpawrunnerServersCache(); err != nil {
		return err
	}
	if err := d.refreshVideoSourceSitesCache(); err != nil {
		return err
	}
	return nil
}
