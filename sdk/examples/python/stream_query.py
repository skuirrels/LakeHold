"""Stream one query using the source checkout."""

import os

import lakehold_sdk
from lakehold_sdk.runtime import LakeholdApiClient, stream_query


def required(name: str) -> str:
    value = os.getenv(name)
    if not value:
        raise RuntimeError(f"{name} is required")
    return value


configuration = lakehold_sdk.Configuration(
    host=required("LAKEHOLD_URL"),
    access_token=required("LAKEHOLD_TOKEN"),
)
with LakeholdApiClient(configuration, timeout=30) as client:
    for event in stream_query(
        client,
        tenant=required("LAKEHOLD_TENANT"),
        catalog=required("LAKEHOLD_CATALOG"),
        sql="SELECT 1 AS value",
    ):
        print(event.payload)
