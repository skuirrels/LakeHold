#!/usr/bin/env python3
"""Fail when a LakeHold OpenAPI change removes or narrows a public contract."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import subprocess
import sys
from typing import Any

HTTP_METHODS = {"get", "put", "post", "delete", "options", "head", "patch", "trace"}
LOWER_BOUND_CONSTRAINTS = ("minimum", "minLength", "minItems", "minProperties", "minContains")
UPPER_BOUND_CONSTRAINTS = ("maximum", "maxLength", "maxItems", "maxProperties", "maxContains")
EXACT_NARROWING_CONSTRAINTS = (
    "pattern",
    "multipleOf",
    "const",
    "contentEncoding",
    "contentMediaType",
    "propertyNames",
    "contains",
    "not",
    "oneOf",
    "anyOf",
    "allOf",
    "discriminator",
)


def load_json(path: Path) -> dict[str, Any]:
    with path.open(encoding="utf-8") as stream:
        return json.load(stream)


def merge_base_contract(base_ref: str, contract_path: str) -> dict[str, Any]:
    merge_base = subprocess.check_output(
        ["git", "merge-base", "HEAD", base_ref], text=True
    ).strip()
    content = subprocess.check_output(
        ["git", "show", f"{merge_base}:{contract_path}"], text=True
    )
    return json.loads(content)


def parameters(operation: dict[str, Any], path_item: dict[str, Any]) -> dict[tuple[str, str], dict[str, Any]]:
    result: dict[tuple[str, str], dict[str, Any]] = {}
    for parameter in [*path_item.get("parameters", []), *operation.get("parameters", [])]:
        if "$ref" in parameter:
            result[("$ref", parameter["$ref"])] = parameter
        else:
            result[(parameter.get("in", ""), parameter.get("name", ""))] = parameter
    return result


def compare_schema(old: Any, new: Any, location: str, failures: list[str]) -> None:
    if not isinstance(old, dict) or not isinstance(new, dict):
        return
    if old.get("$ref") != new.get("$ref"):
        failures.append(f"{location}: schema reference changed from {old.get('$ref')!r} to {new.get('$ref')!r}")
    for attribute in ("type", "format"):
        if attribute in new and old.get(attribute) != new.get(attribute):
            failures.append(f"{location}: {attribute} changed from {old.get(attribute)!r} to {new.get(attribute)!r}")
    if old.get("nullable") is True and new.get("nullable") is False:
        failures.append(f"{location}: nullable values are no longer accepted")
    old_enum = set(map(str, old.get("enum", [])))
    new_enum = set(map(str, new.get("enum", [])))
    if old_enum and not old_enum.issubset(new_enum):
        failures.append(f"{location}: enum values removed: {sorted(old_enum - new_enum)}")
    elif not old_enum and new_enum:
        failures.append(f"{location}: enum restriction added: {sorted(new_enum)}")

    for attribute in LOWER_BOUND_CONSTRAINTS:
        if attribute in new and (attribute not in old or new[attribute] > old[attribute]):
            failures.append(
                f"{location}: {attribute} narrowed from {old.get(attribute)!r} to {new[attribute]!r}"
            )
    for attribute in UPPER_BOUND_CONSTRAINTS:
        if attribute in new and (attribute not in old or new[attribute] < old[attribute]):
            failures.append(
                f"{location}: {attribute} narrowed from {old.get(attribute)!r} to {new[attribute]!r}"
            )
    for attribute in EXACT_NARROWING_CONSTRAINTS:
        if attribute in new and old.get(attribute) != new.get(attribute):
            failures.append(f"{location}: {attribute} constraint changed or was added")
    if new.get("uniqueItems") is True and old.get("uniqueItems") is not True:
        failures.append(f"{location}: uniqueItems restriction added")
    for attribute in ("exclusiveMinimum", "exclusiveMaximum"):
        if new.get(attribute) is True and old.get(attribute) is not True:
            failures.append(f"{location}: {attribute} restriction added")

    old_additional = old.get("additionalProperties", True)
    new_additional = new.get("additionalProperties", True)
    if old_additional is not False and new_additional is False:
        failures.append(f"{location}: additional properties are no longer accepted")
    elif isinstance(old_additional, dict) and isinstance(new_additional, dict):
        compare_schema(old_additional, new_additional, f"{location} additionalProperties", failures)
    elif old_additional is True and isinstance(new_additional, dict):
        failures.append(f"{location}: additional properties gained a schema restriction")
    old_required = set(old.get("required", []))
    new_required = set(new.get("required", []))
    if not new_required.issubset(old_required):
        failures.append(f"{location}: properties became required: {sorted(new_required - old_required)}")
    old_properties = old.get("properties", {})
    new_properties = new.get("properties", {})
    for name, value in old_properties.items():
        if name not in new_properties:
            failures.append(f"{location}: property removed: {name}")
        else:
            compare_schema(value, new_properties[name], f"{location}.{name}", failures)
    if "items" not in old and "items" in new:
        failures.append(f"{location}: array item restriction added")
    elif "items" in old and "items" in new:
        compare_schema(old["items"], new["items"], f"{location}[]", failures)


def compare_contracts(old: dict[str, Any], new: dict[str, Any]) -> list[str]:
    failures: list[str] = []
    old_paths = old.get("paths", {})
    new_paths = new.get("paths", {})
    for path, old_path_item in old_paths.items():
        if path not in new_paths:
            failures.append(f"path removed: {path}")
            continue
        new_path_item = new_paths[path]
        for method, old_operation in old_path_item.items():
            if method not in HTTP_METHODS:
                continue
            if method not in new_path_item:
                failures.append(f"operation removed: {method.upper()} {path}")
                continue
            new_operation = new_path_item[method]
            if old_operation.get("operationId") != new_operation.get("operationId"):
                failures.append(
                    f"{method.upper()} {path}: operationId changed from "
                    f"{old_operation.get('operationId')!r} to {new_operation.get('operationId')!r}"
                )
            old_security = old_operation.get("security", old.get("security", []))
            new_security = new_operation.get("security", new.get("security", []))
            if old_security != new_security:
                failures.append(f"{method.upper()} {path}: security requirements changed")
            old_parameters = parameters(old_operation, old_path_item)
            new_parameters = parameters(new_operation, new_path_item)
            for key, old_parameter in old_parameters.items():
                if key not in new_parameters:
                    failures.append(f"{method.upper()} {path}: parameter removed: {key}")
                    continue
                new_parameter = new_parameters[key]
                if not old_parameter.get("required", False) and new_parameter.get("required", False):
                    failures.append(f"{method.upper()} {path}: optional parameter became required: {key}")
                compare_schema(
                    old_parameter.get("schema", {}),
                    new_parameter.get("schema", {}),
                    f"{method.upper()} {path} parameter {key}",
                    failures,
                )
            for key, new_parameter in new_parameters.items():
                if key not in old_parameters and new_parameter.get("required", False):
                    failures.append(f"{method.upper()} {path}: new required parameter: {key}")

            old_body = old_operation.get("requestBody")
            new_body = new_operation.get("requestBody")
            if old_body and not new_body:
                failures.append(f"{method.upper()} {path}: request body removed")
            elif old_body and new_body:
                if not old_body.get("required", False) and new_body.get("required", False):
                    failures.append(f"{method.upper()} {path}: request body became required")
                for media_type, old_media in old_body.get("content", {}).items():
                    if media_type not in new_body.get("content", {}):
                        failures.append(f"{method.upper()} {path}: request media type removed: {media_type}")
                    else:
                        compare_schema(
                            old_media.get("schema", {}),
                            new_body["content"][media_type].get("schema", {}),
                            f"{method.upper()} {path} request {media_type}",
                            failures,
                        )

            old_success = {code for code in old_operation.get("responses", {}) if str(code).startswith("2")}
            new_success = {code for code in new_operation.get("responses", {}) if str(code).startswith("2")}
            if not old_success.issubset(new_success):
                failures.append(
                    f"{method.upper()} {path}: successful responses removed: {sorted(old_success - new_success)}"
                )
            for code in old_success & new_success:
                old_response = old_operation["responses"][code]
                new_response = new_operation["responses"][code]
                for media_type, old_media in old_response.get("content", {}).items():
                    if media_type not in new_response.get("content", {}):
                        failures.append(
                            f"{method.upper()} {path} {code}: response media type removed: {media_type}"
                        )
                    else:
                        compare_schema(
                            old_media.get("schema", {}),
                            new_response["content"][media_type].get("schema", {}),
                            f"{method.upper()} {path} {code} response {media_type}",
                            failures,
                        )

    old_schemas = old.get("components", {}).get("schemas", {})
    new_schemas = new.get("components", {}).get("schemas", {})
    for name, old_schema in old_schemas.items():
        if name not in new_schemas:
            failures.append(f"component schema removed: {name}")
        else:
            compare_schema(old_schema, new_schemas[name], f"schema {name}", failures)

    old_schemes = old.get("components", {}).get("securitySchemes", {})
    new_schemes = new.get("components", {}).get("securitySchemes", {})
    for name, old_scheme in old_schemes.items():
        if name not in new_schemes:
            failures.append(f"security scheme removed: {name}")
        elif old_scheme != new_schemes[name]:
            failures.append(f"security scheme changed: {name}")
    return failures


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--base-ref", default="origin/main")
    parser.add_argument("--contract", default="openapi/lakehold-v1.json")
    parser.add_argument("--old", type=Path, help="Compare this file instead of the merge-base contract")
    args = parser.parse_args()
    current = load_json(Path(args.contract))
    previous = load_json(args.old) if args.old else merge_base_contract(args.base_ref, args.contract)
    failures = compare_contracts(previous, current)
    if failures:
        print("Breaking OpenAPI changes detected:", file=sys.stderr)
        for failure in failures:
            print(f"- {failure}", file=sys.stderr)
        return 1
    print("OpenAPI compatibility check passed: no removed or narrowed public contracts.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
