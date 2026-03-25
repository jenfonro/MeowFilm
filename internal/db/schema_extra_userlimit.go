//go:build userlimit

package db

// NOTE: user limit is intentionally enforced at the schema level too (SQLite trigger),
// so changing the DB alone is not enough without also removing this trigger.
func extraSchemaSQL() string {
	return usersLimitTriggerDDL()
}

func usersLimitTriggerName() string {
	// "mf_ul_users_bi" xor 0x5A
	b := []byte{
		0x37, 0x3c, 0x05, 0x2f, 0x36, 0x05, 0x2f, 0x29, 0x3f, 0x28, 0x29, 0x05, 0x38, 0x33,
	}
	for i := range b {
		b[i] ^= 0x5A
	}
	return string(b)
}

func usersLimitTriggerCreateSQL() string {
	tn := usersLimitTriggerName()

	// Keep message as a SQL string literal for broad SQLite support.
	// "'E17'" xor 0x5A
	msgBytes := []byte{0x7d, 0x1f, 0x6b, 0x6d, 0x7d}
	for i := range msgBytes {
		msgBytes[i] ^= 0x5A
	}
	msg := string(msgBytes)

	// Keep it deterministic for verification.
	return "CREATE TRIGGER " + tn + "\n" +
		"BEFORE INSERT ON users\n" +
		"WHEN (SELECT COUNT(1) FROM users) >= 3\n" +
		"BEGIN\n" +
		"  SELECT RAISE(ABORT, " + msg + ");\n" +
		"END;"
}

func usersLimitTriggerDDL() string {
	tn := usersLimitTriggerName()
	return "DROP TRIGGER IF EXISTS " + tn + ";\n" + usersLimitTriggerCreateSQL() + "\n"
}
