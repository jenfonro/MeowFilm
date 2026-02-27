package db

func (d *DB) VerifyUsersLimitTrigger() (bool, error) { return verifyUsersLimitTrigger(d) }

func (d *DB) RepairUsersLimitTrigger() error { return repairUsersLimitTrigger(d) }

func (d *DB) EnforceUsersLimitBeforeInsert() error { return enforceUsersLimitBeforeInsert(d) }

func (d *DB) VerifyUsersTableShape() (bool, error) { return verifyUsersTableShape(d) }
