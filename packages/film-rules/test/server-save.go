// Isolated save-stage reference for compare-server.mjs, not a runtime dependency.
// dashboard.go's string-array handlers and config_repo.go's replaceStringTable
// both apply strings.TrimSpace and discard empty rows before returning them.
package main

import (
	"encoding/json"
	"os"
	"strings"
)

func main() {
	var document map[string][]string
	if err := json.NewDecoder(os.Stdin).Decode(&document); err != nil {
		panic(err)
	}
	for group, rows := range document {
		saved := []string{}
		for _, row := range rows {
			if value := strings.TrimSpace(row); value != "" {
				saved = append(saved, value)
			}
		}
		document[group] = saved
	}
	if err := json.NewEncoder(os.Stdout).Encode(document); err != nil {
		panic(err)
	}
}
