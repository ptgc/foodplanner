# Spec: Oda cart, session-only (T-053)
## Goal
On the shopping list, an Owner or Adult taps "Add to Oda cart", signs in to the household's shared Oda account at that moment, and Foodplanner puts the list's items in the Oda cart and closes the connection. Nothing is ordered or paid, nothing from Oda is kept, and when anything fails the text share/copy export (T-050) still works.

Decision record: [Q-024](../questions/Q-024.md) and [Q-025](../questions/Q-025.md), answered 2026-11-20. ADR 0008 (accepted, verdict fragile) still describes the stored-token route and is superseded on that point by those answers; the architect updates the ADR.

## Users & roles
| Action | Anonymous | Non-member | Child member | Kid-mode session | Adult | Owner |
|---|---|---|---|---|---|---|
| Start the Oda flow (`oda/start`) | 401 | 403 | 403 | 403 | yes | yes |
| Complete the Oda flow (`oda/cart`) | 401 | 403 | 403 | 403 | yes | yes |
| Text share/copy export (T-050) | unchanged | unchanged | unchanged | unchanged | unchanged | unchanged |

- Both endpoints carry policy `HouseholdAdult` (T-011) and policy `FullSession` (T-026/T-105). No endpoint code reads the role (rule 2). A Child member with their own login and any kid-mode session can never send to Oda, start an order or change the Oda cart (architect, 2026-11-03). Failures are ProblemDetails with status 403 (`urn:foodplanner:problem:forbidden` or `urn:foodplanner:problem:kid-mode`, whichever the policies give), and no call to Oda is made.
- The Oda account is the household's one shared account. Foodplanner does not know which account it is and does not link it to a user or household: the person who taps signs in to Oda in their own browser.
- Tenancy: the list is always read server-side from the household in the route, through the tenant-filtered sets. The client never sends items, ids or names. A caller never reads or writes another household's list (an Adult of A calling under B gets 403).

## Behaviour

### User flow
1. On the shopping list of a week the Adult or Owner taps "Add to Oda cart". The app shows a confirmation that lists what will be sent (the unchecked items in the "To buy" section by name; "Check you have" staples are not sent) and says that Oda opens for sign-in, that one pack of each item is added, that the cart is not ordered or paid, and that sending twice adds twice (cart quantities are deltas). The user confirms (screens are T-054).
2. The app calls `oda/start` and sends the browser to Oda's authorize URL. The user signs in to Oda, or is already signed in, and approves.
3. Oda redirects the browser to Foodplanner's own origin (the callback route of the web app). The page calls `oda/cart` with the `code` and `state` it received. In this one request the server exchanges the code, searches, fills the cart, closes the connection and returns the result.
4. The app shows the result: what was added, what was not, any cart messages from Oda, and a link to the Oda cart where the user does final swaps, fixes and payment. If anything failed, the text export is offered right there.

### OAuth with Oda
- Authorization code with PKCE `S256`, scope `mcp`, public client: token endpoint auth method `none`, no client secret anywhere. Endpoints come from Oda's published metadata (`/.well-known/oauth-protected-resource/mcp`, `/o/.well-known/oauth-authorization-server`, saved for tests in `spikes/oda/findings/as-metadata.json`), cached in memory only.
- One app-wide client per environment, not one per user or household. It is registered once per environment (dynamic client registration at `/o/register/` through a documented script, or an equivalent registration) with the environment's redirect URI. Its `client_id` is non-secret configuration (`Oda:ClientId`). The app never registers a client during a user request.
- Redirect URI: `<public origin of the web app>/oda/callback`, on Foodplanner's own origin (same origin as the SPA and the `/api` proxy). Whether Oda's registration accepts such a URI is **unverified** (the spike used a loopback redirect) and is an implementation check, see "Implementation checks". If Oda refuses, stop and raise a question; do not fall back to a different approach on your own.
- The redirect URI must be https, except on a local development host (`localhost`, `127.0.0.1`, `[::1]`). A plain-http non-local `Oda:WebOrigin` makes Oda count as not configured and logs one startup warning (T-115). The token revoke uses its own timeout (`Oda:RevokeTimeoutSeconds`, default 10), not the request's cancellation token.
- Only hosts on a configured allow-list (default `oda.com`) are called, including every URL taken from discovery metadata. A metadata or tool response pointing elsewhere is rejected.
- `state` is 32 random bytes, base64url, compared in constant time. The `code_verifier` is 32 random bytes, base64url. Flow state (verifier, state, household id, week start, user id, expiry 10 minutes) is single use and bound to the user who started the flow. Which replica serves the two calls must not matter. How the flow state survives across replicas is **not decided here**: [Q-033](../questions/Q-033.md). Whatever Q-033 decides, the Oda access token itself is never carried between requests (see below).

### Token handling
- The access token (and a refresh token, if the token response contains one) lives only in the memory of the single `oda/cart` request, in a type whose `ToString()` is redacted. It is never written to the database, a cache, a cookie, a file, a header sent to the browser, a log, a trace or an exception message. Refresh is never used: one fill must finish within the access token lifetime, and if it does not, the action fails and degrades (see Failures).
- No entity, table or migration for Oda credentials. T-099 is not needed for Oda.
- The authorization code, `code_verifier`, `state` and tokens are not logged either. One log line per action may carry household id, user id, counts (added, not added), duration and whether the connection was closed.

### Closing the connection
- At the end of every `oda/cart` request that obtained a token, in a `finally` path (success, partial result, any error, request cancellation), the server revokes the token at Oda's `/o/revoke_token/` (a refresh token too, if one was issued) and drops it from memory. If the user denies access at Oda or abandons the flow, no token exists, so there is nothing to revoke; the flow state is consumed or expires.
- A failed revoke call is retried once. If it still fails, the response says `connectionClosed: false` and tells the user to remove the connection under Privacy & security in their Oda account; the token is dropped from memory anyway. Revoking is never skipped to speed things up.
- That revoke works against Oda, and that the connection then disappears under Privacy & security in Oda, is **unverified** (the spike never called it). It is an implementation check with a manual check.

### Search, matching and the cart
- Items sent: the unchecked items of the "Main" ("To buy") section, built from the same `ShoppingListExport` input as the text export (T-050): the name in the request language, or the manual item's label. Checked items and "Check you have" staples are not sent. More than `Oda:MaxItemsPerAction` (default 100) items is refused at start.
- Product search runs only for those items, with `product_search` (batches of at most 10 queries, a small page size), query = item name. Of the hits for one item the first one whose `availability.isAvailable` is true is used. Quantity is one pack (a `manipulate_cart` delta of +1) per item. Pack-size or quantity maths and brand preferences are not done: the household fixes those in Oda afterwards (Q-024 answer: final swaps and fixes are done in Oda; "one pack per item" is this spec's reading of that and the architect should confirm it when reviewing the spec). Which ingredient maps to which product is not remembered; see [Q-032](../questions/Q-032.md) (T-052 mapping), and until it is answered T-053 does not read or write any mapping.
- No product, price, hit list or any other Oda data is collected or kept beyond the request: nothing is stored or cached, no searches beyond the items being added, no crawling of results pages, no use of recommendation tools. Usage limits are respected; a 429 or a limit response from Oda ends the action (it is never retried in a loop or worked around).
- Cart fill: `manipulate_cart` with the matched product ids. A `manipulate_cart` call whose outcome is unknown (timeout after sending) is **not** repeated, because quantities are deltas and a repeat would add twice. Afterwards `get_cart` is read once for the cart `url` and Oda's validation messages (for example a sold-out line); these are passed on to the user and not stored.
- Not used: `clear_cart`, product lists, recipes, orders, delivery slots, checkout (not offered by Oda). Nothing is ordered or paid by Foodplanner; the user pays in Oda via the cart link.

### Endpoints
Both under `/api/households/{householdId}/shopping-lists/{weekStart}/oda` (`weekStart` a Monday `yyyy-MM-dd`, else 400 on `weekStart`), `MapOdaEndpoints`, unsafe methods under the antiforgery group (T-009), rate-limit policy `oda-action` per user (429 `rate-limited` with `Retry-After`).

- `POST start` with `{ "confirmed": true }`: returns 200 `{ "authorizeUrl": "..." }` (`response_type=code`, `client_id`, `redirect_uri`, `scope=mcp`, `code_challenge`, `code_challenge_method=S256`, `state`). `confirmed` missing or false: 400 `urn:foodplanner:problem:oda-confirmation-required`. Nothing to send (no list, or no unchecked Main items): 409 `urn:foodplanner:problem:oda-nothing-to-add`. Too many items: 409 `urn:foodplanner:problem:oda-too-many-items`. Oda is not configured (`Oda:ClientId` empty): 404 `urn:foodplanner:problem:oda-not-configured`, so the client hides the button and offers only the text export. `start` makes no call to Oda.
- `POST cart` with `{ "state": "...", "code": "..." }` or `{ "state": "...", "error": "access_denied" }`:
  - Flow state missing, expired, already used, started by another user, or for another household or week: 400 `urn:foodplanner:problem:oda-state-invalid`, identical body in every case, no call to Oda.
  - `error` present (the user cancelled at Oda): 200 `{ "status": "cancelled" }`, flow state consumed, no token exists.
  - Otherwise 200 `{ status, cartUrl, added, notAdded, cartMessages, connectionClosed }`:
    - `status`: `done` (all items added), `partial` (some not added), `nothing-added`.
    - `added`: `[{ itemId, name, productName, quantity }]` (quantity 1); `productName` comes from this request's search result and is not stored.
    - `notAdded`: `[{ itemId, name, reason }]` with `reason` `no-match` (no hit), `unavailable` (hits exist, none available) or `rejected` (Oda's cart refused the line).
    - `cartMessages`: Oda's validation messages as text; `cartUrl`: the Oda cart link from `get_cart` (null if it could not be read).
    - `connectionClosed`: whether the revoke succeeded.

### Failures
- Oda unreachable, token exchange refused, tool schema not as expected, 429, timeout, or a token that expires mid-action: 502 ProblemDetails `urn:foodplanner:problem:oda-unavailable`. The body carries an `added` list of what was already put in the cart before the failure (so the user does not add it twice), `connectionClosed`, and `fallback`: `{ "export": "text" }`, which points the client to the T-050 text export. The response never includes upstream error bodies, tokens or the code.
- The text export is independent of every Oda failure and works when Oda is not configured.

### Architecture
- The Oda client lives in `Infrastructure/Oda` behind the adapter boundary, with a provider-neutral port for the cart fill and an Oda `IShoppingListExporter` (name `oda`, T-050's `Name` selector) for the summary and cart `Url`. No Oda or MCP types in Domain (the existing `ExporterBoundaryTests` stay green and cover the new code). Oda's MCP server changes over time (2.0.0 to 2.3.0 within weeks, verdict fragile), so the client treats tool schemas as unstable: it fails closed with `oda-unavailable` when a needed tool or field is missing.
- Contract tests run against recorded fixtures under the Api/Infrastructure test project (scrubbed responses from `spikes/oda/findings/`: discovery, token, `product_search` hits, `manipulate_cart`, `get_cart`, and a `tools.json`-style schema snapshot). verify.sh never calls the real Oda.

### Implementation checks (unverified at spec time; evidence in PR notes)
1. Oda's registration accepts a redirect URI on Foodplanner's own origin (the dev, test and prod origins), not only loopback.
2. `POST /o/revoke_token/` with auth method `none` returns success for the access token (and a refresh token if issued), and a later call with that token is refused by the MCP server.
3. Manual check by the implementer or architect with a real Oda account: after one run, Privacy & security in Oda no longer lists the Foodplanner connection, and the cart holds the added items.
4. Token and refresh token lifetime are observed once (for the PR notes only), to confirm one fill fits comfortably in the access token lifetime.

## Out of scope
Everything not in the goal: UI and screens (T-054, which also needs to drop "map products"), the ingredient to product mapping (T-052, see Q-032), stored credentials or any standing Oda connection (T-099 is not needed for Oda), ordering, checkout, delivery slots, payment, `clear_cart`, product lists, recipes, recommendations and offers (T-107), pack-size or quantity optimisation, remembering product choices, Oda image or SKU data (not offered), caching of products or prices, automatic or scheduled sending, multiple Oda accounts, the assistant-bridge or Foodplanner MCP server, persisting the Data Protection key ring or other infrastructure for multi-replica state (Q-033), and the text export itself (T-050, unchanged).

## Acceptance criteria (each must be testable)
All Api tests run against a fake Oda server (recorded fixtures) and Testcontainers SQL Server.
- [ ] `Oda_roles`: `oda/start` and `oda/cart` called by an Owner and an Adult succeed; by a Child member, a kid-mode session and a non-member they return 403 ProblemDetails; anonymous returns 401. In every 403/401 case the fake Oda receives no request.
- [ ] `Oda_other_household_forbidden`: an Adult of household A calling either endpoint under household B gets 403, and B's list is not read.
- [ ] `Oda_endpoints_policies`: a test enumerating `EndpointDataSource` finds both Oda endpoints under `/api/households/{householdId}` with `HouseholdAdult` and `FullSession`, and the architecture test `No_role_checks_outside_policies` (T-011) still passes.
- [ ] `Start_requires_confirmation`: `start` without `confirmed: true` returns 400 `oda-confirmation-required`; the fake Oda receives no request, and no flow state is created.
- [ ] `Start_builds_pkce_authorize_url`: the returned `authorizeUrl` points at the allow-listed Oda authorize endpoint with `response_type=code`, the configured `client_id`, the configured redirect URI on the app's own origin, `scope=mcp`, `code_challenge_method=S256`, and a `code_challenge` equal to the base64url SHA-256 of the verifier held in the flow state; `state` has at least 32 bytes of entropy; the fake Oda is not called.
- [ ] `Start_edge_cases`: an empty list or a list with only checked items returns 409 `oda-nothing-to-add`; more than `Oda:MaxItemsPerAction` items returns 409 `oda-too-many-items`; an empty `Oda:ClientId` returns 404 `oda-not-configured`; a non-Monday `weekStart` returns 400 on `weekStart`.
- [ ] `Token_exchange_is_public_client_pkce`: in `cart`, the fake Oda token endpoint receives `grant_type=authorization_code`, the code, the original `code_verifier`, the `redirect_uri` and `client_id`, and no client secret or Authorization header for client authentication.
- [ ] `State_invalid_cases`: unknown, expired (fake `TimeProvider` past 10 minutes), reused, tampered, another user's and another household's or week's `state` each return 400 `oda-state-invalid` with identical bodies, and the fake Oda receives no request.
- [ ] `State_survives_second_replica`: two `WebApplicationFactory` hosts configured as two replicas (separate memory, shared configuration as defined by Q-033): `start` on host 1 and `cart` on host 2 succeed. Blocked until Q-033 is answered.
- [ ] `Cancelled_at_oda`: `cart` with `error=access_denied` returns 200 `status` `cancelled`, the fake Oda receives no token or revoke request, and the flow state cannot be reused.
- [ ] `Cart_sends_only_unchecked_main_items`: a list with checked items, "Check you have" staples and manual items results in `product_search` queries only for the unchecked Main items (generated names in the request language, manual labels), and in no other query.
- [ ] `Search_is_batched_and_minimal`: with 25 items the fake receives 3 `product_search` calls of at most 10 queries each with the small page size, and no other tool calls other than `manipulate_cart` and one `get_cart`.
- [ ] `Matching_rules`: the first available hit per item is added with quantity 1; a first hit that is unavailable is skipped for the next available one; no hits gives `notAdded` `no-match`; hits all unavailable give `unavailable`; a line refused by the cart gives `rejected`. `status` is `done`, `partial` or `nothing-added` accordingly, and `cartUrl` and `cartMessages` come from `get_cart`.
- [ ] `Unknown_outcome_not_repeated`: when the fake `manipulate_cart` times out after receiving the call, the endpoint does not send it again (the fake counts one call), and the response is 502 `oda-unavailable`.
- [ ] `Token_revoked_after_success`: after a successful `cart` the fake Oda received exactly one revoke call at `/o/revoke_token/` for the access token (and for the refresh token if the fixture includes one), and the response has `connectionClosed: true`.
- [ ] `Token_revoked_on_error_and_cancellation`: with the fake failing at search, at `manipulate_cart`, at `get_cart` and with the request cancelled mid-action, the revoke call is still made each time.
- [ ] `Revoke_failure_reported`: when the fake revoke fails twice, the response is still returned with `connectionClosed: false` and a message about Privacy & security; the revoke was attempted twice.
- [ ] `Tokens_never_persisted`: after a complete run with a recognisable fake token, a scan of every column of every table in the database finds neither the access token, the refresh token nor the code (nor the verifier, unless Q-033 chooses option 3, where it may exist for at most 10 minutes and is gone after the run); the EF model has no Oda credential entity; and no migration is added by this task unless Q-033 chooses option 3.
- [ ] `Tokens_never_logged`: with a capturing logger at trace level, no log entry, exception message or ProblemDetails body of any success or failure run contains the access token, refresh token, code, verifier or `state` value; the token type's `ToString()` returns a redacted value.
- [ ] `Token_not_sent_to_browser`: no response header or body of `start` or `cart` contains the access or refresh token.
- [ ] `Oda_failures_degrade`: with the fake Oda returning 5xx, 429, a refused code exchange, a missing tool and a missing response field, `cart` returns 502 `oda-unavailable` with `fallback.export` `text`, `connectionClosed`, an `added` list of lines put in before the failure, and no upstream body; the T-050 text export for the same week still returns 200 with the same text as before.
- [ ] `Oda_hosts_allow_listed`: a discovery or token response pointing at a host outside the allow-list makes `cart` fail with `oda-unavailable` and the fake of that host receives no request.
- [ ] `No_data_kept`: after runs, the Oda client has no cache or store of products or prices (no instance state survives the request, verified by resolving it twice and by the absence of any static collection in the Oda namespace through an architecture test), and the database contains no Oda product ids or names.
- [ ] `Nothing_is_ordered`: the fake Oda records only `product_search`, `manipulate_cart` (add only, positive quantities), `get_cart`, and the token and revoke calls; any other tool call (including `clear_cart`, checkout, orders) fails the test.
- [ ] `Rate_limited`: with a low configured limit, repeated `start` or `cart` calls from one user return 429 `rate-limited` with `Retry-After`.
- [ ] Architecture tests: no Oda or MCP type in `Domain`; `ExporterBoundaryTests` covers the new exporter; the Oda exporter is selectable by `IShoppingListExporterSelector.Find("oda")`; the `text` exporter and its endpoint are unchanged.
- [ ] `Contract_fixtures`: the recorded fixtures for discovery, token, `product_search`, `manipulate_cart`, `get_cart` and the tool-schema snapshot deserialise with the real client, and removing a field or tool the client needs from the fixture produces the `oda-unavailable` failure rather than an exception.
- [ ] Implementation checks 1 to 4 above are recorded in the PR notes with evidence (redirect URI accepted, revoke response, Privacy & security screenshot or description, observed lifetimes).
- [ ] `verify.sh` is green and never calls the real Oda.

## Open questions
- [Q-032](../questions/Q-032.md): may Foodplanner keep the household's chosen Oda product per ingredient (T-052 mapping)? Until answered, T-053 works with search only and neither reads nor writes a mapping.
- [Q-033](../questions/Q-033.md): where does the OAuth flow state (PKCE verifier, `state`) live across API replicas? Blocks the flow-state part of the implementation and the `State_survives_second_replica` test. May add `Migration: yes` if option 3 is chosen.
- Answered: [Q-024](../questions/Q-024.md) (household's shared Oda account, session-only connection), [Q-025](../questions/Q-025.md) (nothing stored; T-099 not needed for Oda).
