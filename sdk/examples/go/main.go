package main

import (
	"context"
	"fmt"
	"net/http"
	"os"

	lakehold "github.com/skuirrels/LakeHold/sdk/go"
)

func main() {
	err := lakehold.StreamQuery(context.Background(), http.DefaultClient, required("LAKEHOLD_URL"),
		required("LAKEHOLD_TOKEN"), required("LAKEHOLD_TENANT"), required("LAKEHOLD_CATALOG"),
		"SELECT 1 AS value", func(event lakehold.StreamEvent) error {
			fmt.Println(string(event.Payload))
			return nil
		})
	if err != nil {
		panic(err)
	}
}

func required(name string) string {
	value := os.Getenv(name)
	if value == "" {
		panic(name + " is required")
	}
	return value
}
