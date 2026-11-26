# Remote delivery API v1

The plugin backend and the uploader gateway call a private HTTPS service. Configure the base URL and a distinct high-entropy bearer token for each caller. Require verified TLS outside a shared Unix socket. Disable redirects. The browser never receives service credentials.

The plugin supplies the authenticated Jellyfin user's 32-character hexadecimal ID in `X-Delivery-User`. Existing services may configure another identity header ending in `-User` through the Jellyfin process environment variable `KINDLE_DELIVERY_USER_HEADER`. Accept this header only from an authenticated backend. Use the session's identity for every call.

The plugin checks current item visibility, Book type and download permission before submitting. The service gateway independently resolves the ID in that user's current Jellyfin library and checks download permission. Only regular files below the configured Books mount qualify. Repeat authorization on replay, options and status. Status also requires job ownership. Keep the SMTP worker separate with a read-only books mount, private writable job store/cache and egress restricted to the TLS SMTP endpoint. The gateway handles Jellyfin lookups.

| Method | Path | Result |
| --- | --- | --- |
| GET | /v1/books/{bookId}/options | 200 with devices (id, label) and formats (strings). Original comes first. |
| POST | /v1/jobs | 202 for a new job. 200 with the same receipt and current status for a replay. |
| GET | /v1/jobs/{jobId} | 200 with the current receipt. |

POST accepts a JSON object with exactly the four string fields in the fixture. Use a 32-hex Jellyfin item ID and a canonical lowercase UUID v4 idempotency key generated once per user action. Reuse it after a lost response. The unique key is scoped to the user across both interfaces. Changed book, device or format with the same key returns 409. A deliberate send to another device uses a new key. Format IDs are original, epub, reflow-pdf and ocr-pdf. Offer derivatives only when the service lists them for this book.

Receipts contain only jobId (lowercase UUID) and status. Status values are queued, submitting, smtp_accepted, failed and unknown. A worker interrupted during ambiguous submission records unknown and requires reconciliation before another send. SMTP acceptance establishes provider acceptance only.

Errors use {"error":"code"}. Use 400 invalid_request for malformed IDs, fields or paths, 401 unauthorized for invalid caller credentials, 404 unavailable for inaccessible/non-book items and other users' jobs, 422 unknown_device or unavailable_format for rejected choices, 409 key_conflict for conflicting replays and 503 unavailable for dependency failure. Responses are private and no-store.

[Request and response examples](fixtures/delivery-v1.json)
