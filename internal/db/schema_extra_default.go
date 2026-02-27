//go:build !userlimit

package db

func extraSchemaSQL() string { return "" }
