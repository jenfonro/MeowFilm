//go:build !userlimit

package db

func verifyUsersLimitTrigger(_ *DB) (bool, error) { return true, nil }

func repairUsersLimitTrigger(_ *DB) error { return nil }

func enforceUsersLimitBeforeInsert(_ *DB) error { return nil }

func verifyUsersTableShape(_ *DB) (bool, error) { return true, nil }
