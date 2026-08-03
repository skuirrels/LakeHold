# Streaming examples

Each example reads `LAKEHOLD_URL`, `LAKEHOLD_TOKEN`, `LAKEHOLD_TENANT`, and `LAKEHOLD_CATALOG`, then
streams `SELECT 1 AS value`. Build the source package first; the examples intentionally do not imply
that a public registry package already exists.

- Java: `mvn -f ../java/pom.xml install -DskipTests && mvn -f examples/java/pom.xml compile exec:java`
- Go: `(cd examples/go && go run .)`
- .NET: `dotnet run --project examples/dotnet/Lakehold.Example.csproj`
- Python: `PYTHONPATH=python python examples/python/stream_query.py`
