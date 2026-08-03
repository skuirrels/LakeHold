package io.lakehold.examples;

import io.lakehold.sdk.ApiClient;
import io.lakehold.sdk.runtime.LakeholdRuntime;
import java.time.Duration;

public final class StreamQuery {
    private StreamQuery() { }

    public static void main(String[] args) throws Exception {
        ApiClient client = LakeholdRuntime.configure(
            new ApiClient().setBasePath(required("LAKEHOLD_URL")), Duration.ofSeconds(30));
        LakeholdRuntime.streamQuery(client, required("LAKEHOLD_TOKEN"),
            required("LAKEHOLD_TENANT"), required("LAKEHOLD_CATALOG"), "SELECT 1 AS value",
            event -> System.out.println(event.payload()));
    }

    private static String required(String name) {
        String value = System.getenv(name);
        if (value == null || value.isEmpty()) {
            throw new IllegalStateException(name + " is required");
        }
        return value;
    }
}
