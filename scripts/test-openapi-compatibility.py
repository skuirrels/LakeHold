#!/usr/bin/env python3
"""Regression tests for the fail-closed OpenAPI compatibility comparator."""

from __future__ import annotations

from copy import deepcopy
from pathlib import Path
import runpy
import unittest


CHECKER = runpy.run_path(str(Path(__file__).with_name("check-openapi-compatibility.py")))
compare_contracts = CHECKER["compare_contracts"]


def contract() -> dict:
    return {
        "paths": {
            "/items": {
                "get": {
                    "operationId": "ListItems",
                    "responses": {
                        "200": {
                            "content": {
                                "application/json": {
                                    "schema": {"$ref": "#/components/schemas/Item"}
                                }
                            }
                        }
                    },
                }
            }
        },
        "components": {
            "schemas": {
                "Item": {
                    "type": "object",
                    "required": ["id"],
                    "properties": {"id": {"type": "string"}},
                }
            },
            "securitySchemes": {
                "bearerAuth": {"type": "http", "scheme": "bearer"}
            },
        },
    }


class CompatibilityTests(unittest.TestCase):
    def test_additive_optional_property_is_compatible(self) -> None:
        old = contract()
        new = deepcopy(old)
        new["components"]["schemas"]["Item"]["properties"]["label"] = {
            "type": "string"
        }

        self.assertEqual([], compare_contracts(old, new))

    def test_added_string_constraint_is_breaking(self) -> None:
        old = contract()
        new = deepcopy(old)
        new["components"]["schemas"]["Item"]["properties"]["id"]["maxLength"] = 8

        failures = compare_contracts(old, new)

        self.assertTrue(any("maxLength narrowed" in failure for failure in failures), failures)

    def test_operation_security_change_is_breaking(self) -> None:
        old = contract()
        new = deepcopy(old)
        new["paths"]["/items"]["get"]["security"] = [{"bearerAuth": []}]

        failures = compare_contracts(old, new)

        self.assertTrue(any("security requirements changed" in failure for failure in failures), failures)

    def test_additional_properties_restriction_is_breaking(self) -> None:
        old = contract()
        new = deepcopy(old)
        new["components"]["schemas"]["Item"]["additionalProperties"] = False

        failures = compare_contracts(old, new)

        self.assertTrue(any("additional properties" in failure for failure in failures), failures)

    def test_security_scheme_change_is_breaking(self) -> None:
        old = contract()
        new = deepcopy(old)
        new["components"]["securitySchemes"]["bearerAuth"]["scheme"] = "basic"

        failures = compare_contracts(old, new)

        self.assertIn("security scheme changed: bearerAuth", failures)


if __name__ == "__main__":
    unittest.main()
