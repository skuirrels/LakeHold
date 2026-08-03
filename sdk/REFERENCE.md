# SDK runtime reference

Generated API classes expose every operation in [`openapi/lakehold-v1.json`](../openapi/lakehold-v1.json).
The handwritten runtime layers cover behavior that OpenAPI generators do not express consistently.

| Behavior | Java | Go | .NET | Python |
|---|---|---|---|---|
| Configure timeout/user agent | `LakeholdRuntime.configure` | `ConfigureRuntime` | `LakeholdRuntime.Configure` | `LakeholdApiClient` |
| Stream query | `LakeholdRuntime.streamQuery` | `StreamQuery` | `LakeholdRuntime.StreamQueryAsync` | `stream_query` |
| Stream CDC | `LakeholdRuntime.streamChanges` | `StreamChanges` | `LakeholdRuntime.StreamChangesAsync` | `stream_changes` |
| Typed problem | `ProblemException` | `ProblemError` | `LakeholdProblemException` | `LakeholdProblemError` |
| Cursor traversal | `paginate` | `NewCursorPager` | `PaginateAsync` | `paginate` |
| Operation polling | `waitForOperation` | `WaitForOperation` | `WaitForOperationAsync` | `wait_for_operation` |

Query streams emit `schema`, zero or more `row`, then `complete`. CDC streams emit `stream`, zero or
more `change`, then `complete`. A terminal `error` record is raised as a typed failure and a response
that ends without `complete` is rejected as truncated. Consumers receive one record at a time; the
helpers do not accumulate the complete stream. Go and .NET propagate context/cancellation tokens,
Java honours thread interruption, and Python checks its cooperative cancellation callback between
transport chunks.

The MCP server is a complementary agent interface, not an SDK transport. Its bounded tools reuse the
same authorization and query services, while unbounded streaming remains on REST because MCP tool
results are materialized protocol messages.

See [`examples`](examples/) for executable clients and each generated package README for the full
operation and model list.
