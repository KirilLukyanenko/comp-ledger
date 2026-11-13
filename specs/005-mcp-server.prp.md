# PRP-005: MCP server over the compensation API

| Field      | Value |
|------------|-------|
| Status     | Draft |
| Version    | 1.0 |
| Owner      | KirilLukyanenko |
| Created    | 2026-11-13 |
| Depends on | PRP-001 (2.1, implemented), PRP-002 (1.3, implemented) |
| Unlocks    | AI assistants (Claude Code, Claude Desktop, agents) reading compensation data through the same rules as the API |

---

## 1. Goal

The service speaks the **Model Context Protocol** (MCP) as well as REST. An MCP client such as Claude Code connects to `/mcp` on the running API with a tenant header and gets seven tools: read an employee's compensation (now, as of a date, as known at an instant), read the history, read a batch, list, read and create snapshots. Every tool is the matching REST endpoint with the same inputs, the same answers and the same errors. Nothing an MCP client can do is beyond what the REST API already allows.

## 2. Why

- **Business.** HR analysts and reviewers increasingly ask an assistant instead of writing API calls: "what was E-1001's base salary when the FY2026 review started?" MCP is the standard way to give an assistant that access without custom glue per client.
- **Engineering.** A second front door is a second place for rules to drift: a tool that trims input the endpoint doesn't, or answers when the endpoint says `404`. This PRP fixes one rule above all: **a tool is its endpoint**. Both run the same handler, and the holdout scenarios compare them on the same data.
- **Safety.** An assistant is a client that reads whatever it is shown. Tenant isolation, logging rules and the absence of upload and admin operations matter more here than for a hand-written client.

## 3. What

### 3.1 Concepts

- **MCP endpoint.** `/mcp` on the API, Streamable HTTP transport, stateless: every request stands alone, there is no MCP session, and any instance can answer any request.
- **Tool.** A named operation an MCP client calls with `tools/call` and a JSON object of arguments. Each tool in 3.3 maps to exactly one REST endpoint, its **endpoint**.
- **Equivalent request.** For a tool call, the REST request the endpoint would get with the same tenant and the arguments placed in its path and query (3.2). The tool's answer is defined as the equivalent request's answer (3.4).
- **Tool error.** A `tools/call` result with `isError: true`. Every outcome the endpoint reports with a 4xx or 5xx status is a tool error, never a protocol error.
- **Protocol error.** A JSON-RPC error response. Only for problems with the MCP message itself (3.5).

### 3.2 Inputs and validation

**Tenant.** Every HTTP request to `/mcp`, whatever its method and whatever JSON-RPC message it carries (`initialize`, `tools/list`, `tools/call`, notifications), needs the `X-Tenant-Id` header with exactly the PRP-001 3.6 rules. Without a valid tenant the HTTP response is `401` with ProblemDetails, before any MCP processing. The tenant of a tool call is the tenant of the HTTP request that carries it.

**Arguments.** Every tool argument is a JSON string. The input schema of every tool says `"type": "string"` for every property, lists the required ones in `required`, and allows no other types.

- A string argument is used **exactly** as the value of its path segment or query parameter: not trimmed, not decoded, not normalised. The validation rules of the endpoint (PRP-001 3.6, PRP-002 3.2) then apply unchanged. So `knownAt: " 2026-11-13T10:00:00Z"` is `400`, as `?knownAt=%202026-11-13T10:00:00Z` is.
- An optional argument that is absent or JSON `null` is a parameter that was not given. An empty string is a parameter given empty (`asOf: ""` is `400`, as `?asOf=` is).
- A required argument that is absent or `null`, or any argument whose value is not a JSON string (number, boolean, object, array), gives a tool error with status `400`.
- Argument names are case-sensitive and must match 3.3 exactly. Arguments a tool does not name are ignored, as unknown query parameters are.
- An arguments object with the same key twice is not part of the contract. The endpoints' "given more than once" rule has no tool equivalent.

**Path arguments.** `employeeId`, `snapshotId` and `batchId` take the place of a path segment, but there is no URL, so any string can arrive.

- `employeeId`: a value that is not a valid employee id under PRP-001 3.3 (`^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$`) can't have values, so the lookup gives `404` after the other checks, without a database query. That includes values containing U+0000, `/`, or whitespace.
- `snapshotId`, `batchId`: the PRP-001 and PRP-002 rules (hyphenated UUID, either case for `snapshotId`; anything else `404`).

### 3.3 Tools

| Tool | Arguments (`*` required) | Endpoint (equivalent request) | Annotations |
|------|--------------------------|-------------------------------|-------------|
| `get_compensation` | `employeeId*`, `asOf`, `knownAt` | `GET /api/v1/employees/{employeeId}/compensation?asOf=&knownAt=` | read-only |
| `get_compensation_history` | `employeeId*`, `component`, `knownAt` | `GET /api/v1/employees/{employeeId}/compensation/history?component=&knownAt=` | read-only |
| `get_ingestion` | `batchId*` | `GET /api/v1/ingestions/{batchId}` | read-only |
| `list_snapshots` | none | `GET /api/v1/snapshots` | read-only |
| `get_snapshot` | `snapshotId*` | `GET /api/v1/snapshots/{snapshotId}` | read-only |
| `get_snapshot_compensation` | `snapshotId*`, `employeeId*` | `GET /api/v1/snapshots/{snapshotId}/employees/{employeeId}/compensation` | read-only |
| `create_snapshot` | `name*`, `asOf*` | `POST /api/v1/snapshots` with body `{"name": …, "asOf": …}` | not read-only, not destructive, not idempotent |

- **Annotations.** Read-only tools: `readOnlyHint: true`. `create_snapshot`: `readOnlyHint: false`, `destructiveHint: false`, `idempotentHint: false`. Every tool: `openWorldHint: false`.
- **Titles and descriptions** are written for a language model: one sentence on what the tool answers, then the formats it expects (`yyyy-MM-dd` dates, UTC instants with `Z` and up to six fractional digits, case-sensitive component codes from PRP-001 3.2) and the main errors. Each argument has a description of its own. Descriptions are not part of the contract beyond being present and non-empty.
- `create_snapshot` builds the body from its two arguments, so the snapshot body rules of PRP-002 3.2 apply to their values, and the content-type rules don't arise.
- **No other tools.** In particular, nothing uploads a CSV, creates a tenant or deletes anything (5).

### 3.4 Results

**Success** (the endpoint would answer `200` or `201`):

- `isError` is absent or `false`.
- `structuredContent` is the JSON body the endpoint returns: the same properties, names, order-independent values and formats (dates `yyyy-MM-dd`, instants with six fractional digits and `Z`, amounts as JSON numbers).
- `content` holds exactly one item of type `text` whose text is that same JSON, serialised.
- `create_snapshot` has no `Location`; the snapshot's `snapshotId` is in the body.

**Tool error** (the endpoint would answer with a 4xx or 5xx status):

- `isError: true`.
- `structuredContent` is the ProblemDetails object the endpoint returns, with the same `status`, `title` and `type`, and `detail` and other extensions where the endpoint sets them. Per-request values such as `traceId` may differ.
- `content` holds exactly one `text` item with that same JSON.
- An unexpected exception in a tool gives status `500`, as it would on the endpoint. Its text says nothing about the exception beyond what the endpoint's `500` says.

**Equivalence.** For any tool call and its equivalent request, made for the same tenant on the same data, the status (`200`/`201` for success, else the ProblemDetails `status`) and the body or ProblemDetails `status`, `title` and `type` are equal. Waiting is equal too: a call with `knownAt`, `get_snapshot_compensation` and `create_snapshot` wait for a batch being applied exactly as PRP-002 3.4 item 4 says.

### 3.5 MCP protocol

- **`initialize`.** `serverInfo.name` is `comp-ledger`; `serverInfo.version` is the API's informational version (any non-empty string). Capabilities: `tools` only. No resources, prompts, completions, logging or sampling.
- **`tools/list`** returns exactly the seven tools of 3.3, with their input schemas and annotations, in one page (no `nextCursor`).
- **Protocol errors**, answered as the SDK answers them (the codes are not part of the contract): unknown tool name, unknown method, malformed JSON-RPC. An unknown tool is never a tool error.
- **HTTP.** `POST /mcp` carries messages. Other methods on `/mcp`, and the response media type (`application/json` or `text/event-stream`), are as the SDK answers them in stateless mode, after the tenant check; `/mcp` is excluded from the PRP-001 rule "known path, wrong method gives `405` with ProblemDetails". No `Mcp-Session-Id` is issued or required.
- The REST API is unchanged: every PRP-001 and PRP-002 endpoint answers exactly as before.

### 3.6 Non-functional budgets and logging

| Budget | Target |
|--------|--------|
| Tool latency | On the PRP-002 load-gate tenant (1M rows plus a 100k correction), 200 sequential `get_compensation` calls with `knownAt` for different employees, over one HTTP connection: p95 of the HTTP round trip 100 ms or less on a GitHub-hosted runner |
| Memory | The PRP-001 3.7 budgets still hold with the MCP endpoint mapped |
| Logging | One JSON log entry per `tools/call` with `Tool` (the tool name), `TenantId`, `Status` (the integer status of 3.4) and `DurationMs`, under `State` as in PRP-001 3.7. No argument value, no employee id, no snapshot name and no amount, in this entry or in any entry of the MCP SDK. `create_snapshot` also writes the PRP-002 snapshot entry when it succeeds. Calls rejected before the tool runs (`401`, protocol errors) write no entry of their own. REST reads still log nothing of their own (PRP-002 3.6) |

Log categories of the MCP SDK (`ModelContextProtocol.*`) are not framework logs under PRP-002 3.6: they count as the application's.

## 4. Acceptance examples (public)

A separate holdout suite checks the same rules through `/mcp` and the REST API with different data and more edge cases, using an MCP client. **Implement the rules in section 3, not these examples.** T1 and T2 are tenants; T1 holds the data of PRP-002 A1 (`E-1001`, base salary 50000 then corrected to 51000, apply times t1 and t2).

**M1. Tool list.** `tools/list` with T1's header returns exactly `get_compensation`, `get_compensation_history`, `get_ingestion`, `list_snapshots`, `get_snapshot`, `get_snapshot_compensation`, `create_snapshot`. Six are read-only; `create_snapshot` is not, and is neither destructive nor idempotent. Every argument is of type `string`.

**M2. Same answer as REST.** `get_compensation {"employeeId": "E-1001", "asOf": "2026-03-01", "knownAt": t1}` gives `structuredContent` equal to the body of `GET /api/v1/employees/E-1001/compensation?asOf=2026-03-01&knownAt=t1` (50000, `knownAt` = t1 with six digits), and a text item with the same JSON.

**M3. Same errors as REST.** `get_compensation {"employeeId": "E-1001", "knownAt": "2026-11-13T10:00:00+01:00"}` is a tool error with ProblemDetails status `400`. `{"employeeId": "E-9999"}` is status `404`. `{"employeeId": "E-1001", "asOf": " 2026-03-01"}` is `400`.

**M4. Wrong types.** `get_compensation {"employeeId": 1001}` and `get_compensation {}` are tool errors with status `400`, not protocol errors.

**M5. Snapshots.** `create_snapshot {"name": "Q1 review", "asOf": "2026-03-31"}` succeeds with the snapshot body. The same call again is a tool error with status `409`. `list_snapshots` and `GET /api/v1/snapshots` show it identically. `get_snapshot_compensation` for it and `E-1001` gives 51000 with its `snapshotId`.

**M6. Isolation.** With T2's header, `get_snapshot` for T1's snapshot id is a tool error with status `404`, and `list_snapshots` is empty.

**M7. Tenant.** `POST /mcp` with an `initialize` message and no `X-Tenant-Id` gives HTTP `401` with ProblemDetails.

**M8. Unknown tool.** `tools/call` for `delete_snapshot` gives a JSON-RPC error, not a tool result.

## 5. Out of scope

- Uploading CSV files through MCP (files are up to 256 MiB; clients use `POST /api/v1/ingestions`), and creating tenants.
- Resources, prompts, completions, sampling, elicitation, server-sent notifications and progress.
- The stdio transport, a separate MCP server process, and stateful sessions.
- MCP authorization (OAuth) and any authentication: the tenant header stays the stand-in it is in PRP-001.
- Output schemas for tools.
- Changes to the README or other documentation: the owner adds a usage section after merge.
- Any change to REST behaviour.

## 6. Context for the agent

### 6.1 Read first

- `CLAUDE.md`, `specs/README.md`, **PRP-001 and PRP-002 in full**: every tool here is one of their endpoints.
- MCP specification, tools and Streamable HTTP: https://modelcontextprotocol.io/specification/2025-06-18/server/tools and https://modelcontextprotocol.io/specification/2025-06-18/basic/transports
- MCP C# SDK: https://github.com/modelcontextprotocol/csharp-sdk (packages `ModelContextProtocol.AspNetCore` for the server, `ModelContextProtocol` for the client in tests).

### 6.2 Current state of the repository

PRP-001 2.1 and PRP-002 1.3 are implemented on `main`: migrations `0001` to `0007`, 228 unit and 377 integration tests. The relevant parts:

```
src/CompLedger.Api/Program.cs                         # pipeline: ProblemDetails, tenant middleware, endpoint maps
src/CompLedger.Api/Tenancy/TenantResolutionMiddleware.cs
src/CompLedger.Api/Endpoints/{Compensation,Ingestion,Snapshot}Endpoints.cs, RequestChecks.cs
src/CompLedger.Infrastructure/Queries/CompensationQueries.cs
src/CompLedger.Infrastructure/Snapshots/SnapshotStore.cs
src/CompLedger.Infrastructure/Ingestion/IngestionBatch.cs   # InstantJsonConverter
src/CompLedger.Api/appsettings.json                   # JSON console logging, levels
```

### 6.3 Target tree after this PRP

```
src/CompLedger.Api/Operations/          # one handler per operation, shared by endpoint and tool (6.4)
src/CompLedger.Api/Mcp/CompLedgerTools.cs   # the seven tools
src/CompLedger.Api/Mcp/McpLogging.cs        # the per-call log entry (or equivalent)
tests/CompLedger.IntegrationTests/Mcp*Tests.cs
```

Names inside `src/` may differ if the shape is the same: shared handlers, a thin endpoint, a thin tool.

### 6.4 Technical decisions (fixed: changing one requires a PRP update first)

| Decision | Reason |
|----------|--------|
| The MCP server runs inside the API process, mapped at `/mcp` with the Streamable HTTP transport in **stateless** mode | One image and one deployment; the same tenant middleware, database access and logging; testable from outside over HTTP; any instance can serve any request |
| Packages `ModelContextProtocol.AspNetCore` and, in tests only, `ModelContextProtocol`, both **2.3.1**, in `Directory.Packages.props` | The official SDK; 2.3.1 is the version the owner already runs elsewhere. Newer versions are days old |
| **One handler per operation.** The REST endpoint and the tool both call a handler that takes the raw input strings and the tenant and returns a status and a body (or ProblemDetails). The endpoint turns that into an HTTP response, the tool into a tool result | Equivalence (3.4) by construction instead of by discipline |
| No HTTP calls from the tools to the API itself | A loopback call would need the port and the headers, double the logging and the connection count, and could reach another instance |
| Every tool argument is a string, bound by the tool itself from the raw arguments | The endpoints validate strings; a typed parameter would let the SDK reject or convert values before the rules of 3.2 run |
| Tool errors carry the endpoint's ProblemDetails in `structuredContent` and as text | One error shape for both front doors; clients without structured content still see it |
| No output schemas | Results are the REST bodies, which differ between success and error |

### 6.5 Known gotchas

1. **The SDK binds typed parameters before your code runs.** With `string employeeId` as a C# parameter, a JSON number may be converted, or rejected with a protocol error or a generic tool error, depending on the SDK. 3.2 needs a `400` tool error with ProblemDetails. Take the raw arguments (for example a `JsonElement` or the request's `Arguments` dictionary) and check the JSON kind yourself, and write a test for each wrong kind. The input schema must still list every argument as `"type": "string"`; check what `tools/list` returns.
2. **Exceptions inside a tool.** The SDK turns an exception into a tool error with its own text, and may include the exception message. Catch inside the handler boundary and return the same `500` ProblemDetails the endpoint gives; never return exception text.
3. **The tenant comes from the HTTP request.** In stateless mode each `tools/call` arrives on its own HTTP request, so `IHttpContextAccessor` (or the SDK's request context) gives the tenant the middleware resolved. Never cache a tenant across calls, in a static, or in a singleton tool instance.
4. **The tenant middleware must cover `/mcp`** for every method, and run before `MapMcp`. Today it only runs for the REST tenant endpoints. A test sends `initialize` without the header and expects `401`.
5. **JSON options differ.** The SDK serialises with its own `JsonSerializerOptions`. `structuredContent` must be byte-for-byte the REST body's JSON shape: camelCase, `InstantJsonConverter` for instants, the same null handling. Serialise the body with the API's options into a `JsonElement` / `JsonNode` and hand that to the result, rather than letting the SDK serialise a C# object.
6. **SDK logging counts as application logging** (3.6). The SDK logs under `ModelContextProtocol.*`; at Information or Debug it may log method names or arguments. Set those categories to `Warning` in `appsettings.json`, and add a test that captures the log output of a tool call with a known employee id, snapshot name and amount and finds none of them.
7. **`employeeId` with U+0000** reaches the tool (there is no Kestrel path parser on this route) and PostgreSQL rejects it with an exception. 3.2 makes any invalid id a `404` without a query; check the pattern first.
8. **Unique violation on `create_snapshot`.** The `23505` → `409` mapping and the PRP-002 snapshot log entry must come from the shared handler, so the tool can't skip them.
9. **The 405 rule.** The PRP-001 code answers wrong methods on known paths with `405` ProblemDetails. `/mcp` is excluded (3.5); make sure the existing method check doesn't swallow the SDK's `GET` or `DELETE` handling, or turn its answers into ProblemDetails.
10. **Integration tests through the real protocol.** Use the `ModelContextProtocol` client against the `WebApplicationFactory` server (the client's HTTP transport accepts an `HttpClient`; pass one from the factory with the tenant header). Asserting on hand-built JSON-RPC is fine for `401` and protocol errors, not for tool results.
11. **Refactoring endpoints into handlers must not change REST behaviour.** All 377 integration tests and the PRP-001/PRP-002 holdout suites must pass with no test edited in T1.

## 7. Implementation blueprint

### 7.1 Data model

No database change. No migration.

### 7.2 Request flow

```
POST /mcp  (X-Tenant-Id)
  TenantResolutionMiddleware            -> 401 ProblemDetails, nothing else runs
  MCP SDK (stateless): parse JSON-RPC   -> protocol errors (3.5)
  tools/call name, arguments
    tool: check argument kinds          -> 400 ProblemDetails as tool error
          build the operation's raw inputs (strings, null for absent)
          handler(tenant, inputs)       -> (status, body | ProblemDetails)   // same call the endpoint makes
          log one entry (Tool, TenantId, Status, DurationMs)
          result: structuredContent = body as JsonNode (API JSON options), content = [text: same JSON], isError = status >= 400

GET /api/v1/employees/{employeeId}/compensation   (unchanged behaviour)
  endpoint: raw inputs from route and query -> handler(tenant, inputs) -> HTTP response
```

### 7.3 Tasks (in order; one commit per task, message `PRP-005 Tn: <summary>`)

| #  | Task | Done when |
|----|------|-----------|
| T1 | Move the logic of the six read endpoints and snapshot creation into shared handlers; endpoints become thin | Every existing unit and integration test passes with no test file changed; no new behaviour |
| T2 | Packages, `/mcp` (stateless Streamable HTTP), tenant check on `/mcp`, `initialize`, `tools/list` with the seven tools, argument kinds and schemas; SDK log levels | Integration tests: M1, M7, M8; every argument of every tool is `"type": "string"`; wrong kinds and missing required arguments give `400` tool errors (M4) |
| T3 | The six read tools on the shared handlers, results and tool errors as 3.4 | Integration tests: M2, M3, M6, plus equivalence tests that call each read tool and its REST endpoint on the same data and compare, for success and for each error status the endpoint can give |
| T4 | `create_snapshot`, the per-call log entry, the SDK log check | Integration tests: M5; one log entry per `tools/call` with the four properties; no argument value, employee id, name or amount in any log line of a tool call; the snapshot entry on success |
| T5 | Measure | The PR description gives local numbers for 3.6: tool latency p95 on a 1M-row tenant with a 100k correction, and the 1M-row ingestion memory with `/mcp` mapped |

## 8. Validation loop

```bash
dotnet build CompLedger.slnx -warnaserror
dotnet format CompLedger.slnx --verify-no-changes
dotnet test tests/CompLedger.UnitTests
dotnet test tests/CompLedger.IntegrationTests
```

All PRP-001 and PRP-002 tests must keep passing, and in T1 unchanged. Never weaken, skip or delete a test to make a level pass. If this PRP looks wrong or contradicts PRP-001 or PRP-002, stop and say so in the PR description.

## 9. Definition of done

- [ ] Every rule in section 3 is implemented and covered by a test; M1 to M8 pass as integration tests.
- [ ] All validation levels are green on a clean checkout; PRP-001 and PRP-002 tests green, none edited.
- [ ] No migration added; migrations `0001` to `0007` untouched.
- [ ] No secrets, hostnames or credentials in the repository; no `.mcp.json` committed.
- [ ] Nothing under `specs/`, `.claude/`, `.github/` or `metrics/` was changed, and `README.md` is unchanged.
- [ ] The PR description maps each section-3 rule to its test, pastes the validation summaries and gives the T5 measurements.

## 10. Anti-patterns

- A tool that calls the API over HTTP, or reimplements validation the endpoint already has.
- Typed C# parameters that let the SDK accept `1001` as `"1001"`, or reject it with a protocol error.
- Returning success with an error message in the text, or a protocol error for a `400`, `404` or `409`.
- Tools for uploads, tenants, deletes or anything not in 3.3, "to be helpful".
- Logging tool arguments, or leaving the SDK's own logging at Information.
- Stateful sessions, or any per-session or static tenant.
- Changing a REST response, status or log entry.

## 11. How this PRP is checked beyond the agent's view

1. **Holdout scenarios** for 3.1 to 3.5 through `/mcp` with an MCP client and through the REST API, with other data. The core of the suite is equivalence: the same inputs through both doors, compared.
2. **Load gate** for 3.6: tool latency on the 1M-row tenant, and the PRP-001 memory budgets with the endpoint mapped.
3. **AI review against this PRP** on the pull request.
4. All PRP-001 and PRP-002 holdout scenarios still run and must still pass.

## Changelog

| Version | Date       | Change |
|---------|------------|--------|
| 1.0     | 2026-11-13 | First version (Draft) |
