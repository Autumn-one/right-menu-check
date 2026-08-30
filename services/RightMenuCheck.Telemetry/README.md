# RightMenuCheck Telemetry Service

This is the bounded lifecycle telemetry service for RightMenuCheck. It is a standalone Go HTTP service backed by SQLite.

## Network boundary

The process only accepts a numeric loopback listen address such as 127.0.0.1:8787 or [::1]:8787. Wildcard, LAN, public, and hostname listeners are rejected even when an admin token is configured. The listener address is checked again after net.Listen.

The only supported public entry is an HTTPS reverse proxy running on the same machine and forwarding to this loopback service. The proxy must:

- expose the telemetry routes over HTTPS only;
- disable access logging for /v1/telemetry/* so client IP addresses are not collected;
- avoid exposing /v1/admin/* publicly when possible; and
- preserve the request body and Authorization header without recording either.

Direct non-loopback plaintext operation is not supported.

## Data boundary

The database stores:

- a client-generated SHA-256 machine identifier;
- a random session identifier used as a selector;
- only the SHA-256 digest of the server-generated session token;
- server-received start and last-heartbeat times;
- startup, duration, normal-segment, and abnormal-segment aggregates; and
- raw closed-session rows for a configured retention period.

The service does not read or persist request IP addresses, User-Agent, usernames, filesystem paths, hardware details, raw session tokens, or heartbeat history. It has no HTTP access log. Error responses and operational logs never contain machine IDs, session IDs, tokens, request bodies, or client network information.

## Client protocol

All JSON is strict camelCase. Unknown fields and multiple JSON values are rejected. The default body limit is 4 KiB. All timestamps are server-received UTC timestamps.

### Start

POST /v1/telemetry/start

No authorization header is used for the initial request:

~~~json
{
  "machineId": "64-hex-character-sha256-machine-id",
  "sessionId": "32-hex-character-guid-n"
}
~~~

Response:

~~~json
{
  "startupCount": 1,
  "startedAtUtc": "2026-08-31T06:07:08.901Z",
  "sessionToken": "43-character-base64url-token"
}
~~~

sessionToken contains 256 random bits. The client keeps it only for the current lifecycle. Repeating start with the same open machineId and sessionId does not increment startupCount; it rotates the token, so only the latest successful response remains valid. A client retrying after a lost response must reuse the same session ID.

### Heartbeat and end

- POST /v1/telemetry/heartbeat
- POST /v1/telemetry/end

Both use the same machineId/sessionId JSON as start and require:

~~~text
Authorization: Bearer <sessionToken>
~~~

Success is 204. A missing, malformed, forged, rotated, or wrong-machine token returns 401. Token digests are compared in constant time.

### Resume

POST /v1/telemetry/resume

Resume is used after a service restart, long sleep, network outage, or session_ended response. It authenticates with the previous segment token:

~~~text
Authorization: Bearer <previousSessionToken>
~~~

Request:

~~~json
{
  "machineId": "64-hex-character-sha256-machine-id",
  "previousSessionId": "previous-32-hex-character-guid-n",
  "sessionId": "new-32-hex-character-guid-n"
}
~~~

The response has the same shape as start and contains a new token. Resume creates a new timing segment but copies the original launch ordinal, so it never increments startupCount. If the previous segment is still open, resume atomically settles it as abnormal at its last heartbeat before creating the new segment. The offline or sleep gap is not counted as use time.

The previous raw session row must still be inside RMC_TELEMETRY_CLOSED_SESSION_TTL; after retention deletes it, resume returns 404 and the client must begin a new launch with a new session ID when appropriate.

## Settlement and retention

Normal end, stale-session settlement, and resume settlement update the machine duration and normal/abnormal counters in the same SQLite transaction as the raw session row. Repeated settlement is idempotent.

A dedicated cleanup goroutine:

1. settles open sessions whose last heartbeat is older than RMC_TELEMETRY_SESSION_TIMEOUT; and
2. deletes closed raw session rows older than RMC_TELEMETRY_CLOSED_SESSION_TTL.

Machine aggregates remain after raw rows are deleted. Therefore admin totals do not change across retention cleanup. Startup cleanup also recovers stale sessions left open across a service stop or process termination.

Schema v1 databases migrate atomically to schema v2. Legacy open rows are settled as abnormal, all existing closed rows are aggregated, and legacy rows have no valid session token.

## Abuse and capacity controls

The service applies:

- a process-wide token bucket to all HTTP requests;
- a durable SQLite new-session limit per UTC minute;
- maximum machine and active-session counts;
- a SQLite max_page_count limit for the main database;
- bounded WAL auto-checkpointing and journal_size_limit;
- bounded admin pagination and in-flight request concurrency; and
- a context deadline propagated to every handler and database operation.

Capacity checks reject only creation of new sessions. Existing authenticated heartbeat/end and cleanup operations remain available under machine or active-session limits.

RMC_TELEMETRY_MAX_DATABASE_BYTES limits SQLite main-database pages. WAL, SHM, filesystem metadata, migration headroom, and temporary files are not included. Production must also use an operating-system or hosting disk quota.

Common error codes:

| HTTP | Code | Meaning |
| --- | --- | --- |
| 401 | invalid_session_token | Missing, malformed, or forged lifecycle token |
| 409 | session_ended | Segment was already settled |
| 429 | rate_limited | Global token bucket exhausted |
| 429 | new_session_rate_limited | Per-minute durable admission limit reached |
| 503 | server_busy | In-flight request capacity reached |
| 504 | request_timeout | Handler/database deadline reached |
| 507 | capacity_exceeded | Machine, active-session, or database capacity reached |

## Management API

| Method | Path | Result |
| --- | --- | --- |
| GET | /v1/admin/summary | durable startup, segment, exit, and duration totals |
| GET | /v1/admin/machines?limit=100&offset=0 | hashed machine IDs and durable aggregates |
| GET | /v1/admin/sessions?limit=100&offset=0 | retained raw session timing rows, without token data |

Management requests require:

~~~text
Authorization: Bearer <RMC_TELEMETRY_ADMIN_TOKEN>
~~~

The token must contain at least 32 characters. Missing or invalid authorization is rejected even for loopback requests.

Only local integration tests may omit the token by explicitly setting:

~~~text
RMC_TELEMETRY_ALLOW_UNAUTHENTICATED_LOOPBACK_ADMIN=true
~~~

This switch never authorizes non-loopback clients. If an admin token is configured, the token remains required even when the switch is true.

GET /health reports only {"status":"ok"} when SQLite is reachable.

## Configuration

| Environment variable | Default | Purpose |
| --- | --- | --- |
| RMC_TELEMETRY_LISTEN_ADDRESS | 127.0.0.1:8787 | Numeric loopback address and port |
| RMC_TELEMETRY_DATABASE_PATH | data/telemetry.db | SQLite database location |
| RMC_TELEMETRY_ADMIN_TOKEN | none, required | Management bearer token, at least 32 characters |
| RMC_TELEMETRY_ALLOW_UNAUTHENTICATED_LOOPBACK_ADMIN | false | Explicit integration-test-only bypass |
| RMC_TELEMETRY_SESSION_TIMEOUT | 3m | Time without heartbeat before abnormal settlement |
| RMC_TELEMETRY_CLOSED_SESSION_TTL | 168h | Raw closed-session retention |
| RMC_TELEMETRY_SWEEP_INTERVAL | 30s | Background maintenance interval |
| RMC_TELEMETRY_HANDLER_TIMEOUT | 5s | Request and database operation deadline |
| RMC_TELEMETRY_SHUTDOWN_TIMEOUT | 10s | HTTP graceful-shutdown deadline |
| RMC_TELEMETRY_MAX_REQUEST_BYTES | 4096 | JSON body limit, 256 bytes to 1 MiB |
| RMC_TELEMETRY_MAX_CONCURRENT | 64 | In-flight request limit |
| RMC_TELEMETRY_REQUESTS_PER_SECOND | 200 | Global token refill rate |
| RMC_TELEMETRY_REQUEST_BURST | 400 | Global token bucket capacity |
| RMC_TELEMETRY_NEW_SESSIONS_PER_MINUTE | 1000 | Durable new start/resume limit |
| RMC_TELEMETRY_MAX_MACHINES | 1000000 | Durable machine aggregate limit |
| RMC_TELEMETRY_MAX_ACTIVE_SESSIONS | 100000 | Open timing segment limit |
| RMC_TELEMETRY_MAX_DATABASE_BYTES | 536870912 | SQLite main-database page limit |

RMC_TELEMETRY_CLOSED_SESSION_TTL must be at least the session timeout. The service refuses to start with unsafe or malformed values.

## Build and test

Go 1.26.6 or later is required. modernc.org/sqlite is a pure-Go SQLite driver, so no C compiler or native SQLite runtime is needed.

~~~powershell
go test -count=1 ./...
go vet ./...
go build -trimpath -o artifacts/rightmenucheck-telemetry-v2.exe ./cmd/rightmenucheck-telemetry

$env:RMC_TELEMETRY_ADMIN_TOKEN = [Convert]::ToHexString(
    [Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
./artifacts/rightmenucheck-telemetry-v2.exe
~~~

Stop the process with Ctrl+C or the service manager termination signal. The HTTP server drains in-flight requests, cleanup stops before SQLite closes, and asynchronous logging has a bounded shutdown wait.
