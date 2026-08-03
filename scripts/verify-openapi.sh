#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
contract="${1:-${repository_root}/openapi/lakehold-v1.json}"

jq -e '.openapi | startswith("3.1.")' "${contract}" >/dev/null
jq -e '.info.title == "LakeHold Public API" and .info.version == "v1"' "${contract}" >/dev/null
jq -e '.servers == [{"url":"/"}]' "${contract}" >/dev/null
jq -e '.components.securitySchemes.bearerAuth.type == "http"
    and .components.securitySchemes.bearerAuth.scheme == "bearer"' "${contract}" >/dev/null
jq -e '[.paths | keys[] | select(startswith("/api/v1/") | not)] | length == 0' "${contract}" >/dev/null
jq -e '[.paths[] | to_entries[] | select(.key != "parameters") | .value.operationId]
    | all(. != null and length > 0)
    and (length == (unique | length))' "${contract}" >/dev/null
jq -e '.paths["/api/v1/capabilities"].get.security == null' "${contract}" >/dev/null
jq -e '[.paths | to_entries[] as $path
    | $path.value | to_entries[] | select(.key != "parameters")
    | {path: $path.key, security: .value.security}]
    | all(if .path == "/api/v1/capabilities"
        then .security == null
        else .security == [{"bearerAuth":[]}]
        end)' "${contract}" >/dev/null
jq -e '[.paths[] | to_entries[] | select(.key != "parameters") | .value.responses
    | to_entries[] | select(.key | test("^[45]"))
    | .value.content["application/problem+json"].schema["$ref"]]
    | length > 0
    and all(. == "#/components/schemas/PublicApiProblemDetails")' "${contract}" >/dev/null
jq -e '[.paths[] | to_entries[] | select(.key != "parameters") | .value.responses]
    | all(any(to_entries[]; .key | test("^2")))' "${contract}" >/dev/null
jq -e '[.paths[] | to_entries[] | select(.key != "parameters") | .value.responses
    | to_entries[] | .value.headers["X-Request-Id"].schema.type]
    | length > 0 and all(. == "string")' "${contract}" >/dev/null
jq -e '.paths["/api/v1/operations/{id}"].get.responses["200"]
    .content["application/json"].schema["$ref"]
    == "#/components/schemas/PublicApiOperationDto"' "${contract}" >/dev/null
jq -e '.components.schemas.PublicApiProblemDetails.required
    | contains(["code", "requestId"])' "${contract}" >/dev/null
jq -e '.paths["/api/v1/tenants/{tenantSlug}/tokens"].post.parameters
    | all(.name != "Idempotency-Key")' "${contract}" >/dev/null
jq -e '[.paths[] | to_entries[] | select(.key != "parameters") | .value.parameters[]?
    | select(.name == "Idempotency-Key")]
    | length > 0
    and all(.in == "header"
        and .required != true
        and .schema.type == "string"
        and .schema.minLength == 16
        and .schema.maxLength == 128
        and .schema.pattern == "^[!-~]{16,128}$")' "${contract}" >/dev/null
jq -e '[.paths[] | to_entries[] | select(.key != "parameters") | .value
    | select(any(.responses | to_entries[];
        (.key | test("^2"))
        and (.value.content["application/json"].schema["$ref"] // ""
            | startswith("#/components/schemas/CursorPageOf"))))
    | {
        limits: [.parameters[] | select(.name == "limit" and .in == "query")],
        cursors: [.parameters[] | select(.name == "cursor" and .in == "query")]
      }]
    | length > 0
    and all((.limits | length) == 1
        and (.cursors | length) == 1
        and .limits[0].required != true
        and .limits[0].schema.type == "integer"
        and .limits[0].schema.minimum == 1
        and .limits[0].schema.maximum >= 100
        and .limits[0].schema.maximum <= 500
        and .limits[0].schema.default == 100
        and .cursors[0].required != true
        and .cursors[0].schema.type == "string")' "${contract}" >/dev/null

printf 'Verified OpenAPI contract %s\n' "${contract}"
