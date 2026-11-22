# Spec: Oda cart fill prefers what the household usually buys (T-114)
## Goal
When an Owner or Adult taps "Add to Oda cart", the fill puts in the product the household has bought before (for example the usual cherry tomatoes) instead of the first search hit, and the result says which lines came from "usually bought". It happens inside the same session-only connection as T-053; nothing from Oda is stored, cached or logged.

Decision record: architect, 2026-11-22 (task file T-114): history first, then T-052. Priority order in the fill, once both exist: the household's own pick (T-052), then purchase history (this task), then the first available hit. T-053 spec ([T-053](T-053-oda-cart-session.md)) stays the base; this spec changes its "Search, matching and the cart" section only as written below.

## Users & roles
No new endpoint and no new role rule. The fill is `POST .../oda/cart` of T-053 (policies `HouseholdAdult` + `FullSession`):

| Action | Anonymous | Non-member | Child member | Kid-mode session | Adult | Owner |
|---|---|---|---|---|---|---|
| Fill with "usually bought" (the existing `oda/cart`) | 401 | 403 | 403 | 403 | yes | yes |

- No Oda call is made for any 401/403 (unchanged from T-053).
- Tenancy: the history belongs to the household's shared Oda account, read with the token of this one request. Foodplanner does not link it to a household or user and keeps none of it. The list is still read server-side from the household in the route.

## Behaviour

### Which history is read
- `likely_to_buy` (Oda tool: products the user usually buys, from purchase history and reorder patterns): exactly one call per fill, no arguments except `intent` when the tool schema has it. It returns up to 50 products with the same fields as search. Only the product `id` of each entry is used; names, prices and the rest are ignored and dropped.
- `get_orders` / `get_order` are not used. `get_orders` already lists products per order (up to 20 orders per page, up to 50 pages), but it needs several calls to reach the same depth, returns far more data than we need, and `likely_to_buy` already summarises the same history. Whether to add one bounded `get_orders` call is [an open question](#open-questions).
- When: after all searches are done and before the first `manipulate_cart`, so a search failure still touches nothing. The call is skipped (no `likely_to_buy` request) when no item has an available hit, and, once T-052 exists, when the household's own pick already decides every item.
- Calls per fill, new total: `product_search` batches (as before), plus at most 1 `likely_to_buy`, `manipulate_cart` batches and 1 `get_cart` (as before). No paging of `likely_to_buy` (the schema has none).

### Matching rule (pure, `Domain/ShoppingLists/Cart`)
Input per item: its search hits in Oda's rank order (the page of `Oda:SearchPageSize` hits that T-053 already fetches), and the set of "usually bought" product ids. Rule:
1. Only hits with `availability.isAvailable = true` are candidates (unchanged).
2. The first candidate whose product id is in the usually-bought set wins, source `usually-bought`. A usual product is only ever chosen from among the item's own search hits: a product from the history list that the search for this item did not return is never added (so "smør" can never get a usual product for something else).
3. Otherwise the first available hit wins, source `first-hit` (T-053 behaviour).
4. Tie-break: several usual products among the hits: the best search rank wins (not the order of the history list, whose meaning is not documented). Equal ids cannot occur twice in one hit list; if they do, the first occurrence counts. Usual product present but unavailable: skipped, the next candidate rules apply.
5. `no-match` and `unavailable` reasons are unchanged: no hits, or no available hit.
6. The match compares Oda product ids exactly (the integer id as a string, as `ProductCandidate.ProductId`). No name matching and no fuzzy matching.
7. One pack per item (quantity 1) is unchanged. Two items that land on the same product get one line each, as in T-053.

### When history cannot be read
- Any failure of the `likely_to_buy` step (tool missing from the tool list, argument or response shape not as expected, `isError`, timeout, 4xx, 5xx, 429) is not a failure of the fill: no further history call or retry is made, the fill continues with rule 3 for every item, and the response says `usuallyBought.status = "unavailable"`.
- A 429 or limit response is therefore never retried or worked around (T-053); if Oda is really limiting us, the next required call (`manipulate_cart`) fails as in T-053 and gives `oda-unavailable` with what was added so far. A token that is no longer accepted fails the same way there.
- `likely_to_buy` is optional for the client: its absence from the tool list does not fail the connection check, which still requires `product_search`, `manipulate_cart` and `get_cart`. An empty history is `status = "used"` with nothing matched, not an error.

### Nothing kept
- The set of ids lives in one local variable of the `oda/cart` request and is dropped with it. It is not written to the database, a cache, a file, a static field or an instance field that outlives the request, and it is not logged. Product names from the history are not even read.
- The single log line per action may add a count (items from "usually bought") and whether history was read; it carries no product id or name (extends T-053).

### Result
- `added[]` lines get `source`: `usually-bought` or `first-hit` (T-052 adds `preferred`). It is also present on the `added` list of a 502 `oda-unavailable` body.
- The response gets `usuallyBought`: `{ "status": "used" | "unavailable" | "skipped", "count": n }`. `used` = the call succeeded; `unavailable` = it failed; `skipped` = it was not called (see "When"). `count` = number of added lines with source `usually-bought`.
- Web result screen (T-054 screens, small change in this task): a line from `usually-bought` shows a short label "Usually bought" / "Du kjøper ofte denne" (nb + en); when status is `unavailable` one plain line says the usual products could not be read and the first matches were used. The labels are plain text (React escapes), nothing is written to browser storage. The existing rule stays: the "connection not closed" alert is shown only when the revoke failed, and no line is shown when it was closed (architect: no "connection closed" line).
- The OpenAPI document gets the new fields; the TS client is regenerated, never hand-edited.

## Out of scope
Storing anything from Oda (including remembering which products were usual); prices and offers (T-107); picking a preferred product by hand and the household's own pick (T-052); `unique_for_you` or any recommendation tool; `get_orders`/`get_order` (unless the architect decides otherwise below); searching extra pages for items; pack-size or quantity logic; new endpoints, entities or migrations.

## Acceptance criteria (each must be testable)
All Api tests run against the fake Oda (recorded, scrubbed fixtures from `spikes/oda/findings/`, extended with a `likely_to_buy` fixture of 50 products) and Testcontainers SQL Server, and never call the real Oda.
- [ ] `Usual_matcher_rules` (Domain unit tests, `ProductMatcher` or a new matcher): first available hit in the usual set wins over an earlier available non-usual hit with source `usually-bought`; no usual hit gives the first available with `first-hit`; a usual hit that is unavailable is skipped; two usual hits give the better-ranked one; a usual id that is not among the hits is never chosen; no hits gives `NoMatch`; hits all unavailable give `Unavailable`; an empty usual set equals the T-053 rule; null arguments throw.
- [ ] `Fill_prefers_usual_product`: with the fake serving history ids that appear as the second hit of two items, the fake receives `manipulate_cart` operations for those second hits and the first hit for the other items; the response `added[].source` is `usually-bought` for exactly those lines and `first-hit` for the rest; `usuallyBought` is `{ status: "used", count: 2 }`.
- [ ] `History_is_one_call`: with 25 items the fake receives 3 `product_search` calls, exactly one `likely_to_buy` call (after the last search and before the first `manipulate_cart`), the `manipulate_cart` batches and one `get_cart`, and no `get_orders`, `get_order` or other tool call.
- [ ] `History_skipped_when_no_hits`: when no item has an available hit, the fake receives no `likely_to_buy` call and `usuallyBought.status` is `skipped`.
- [ ] `History_failure_falls_back`: with the fake failing `likely_to_buy` by 5xx, 429, timeout, `isError`, a changed response shape, and a tool list without `likely_to_buy`, the endpoint still returns 200 with first-hit lines (identical to the T-053 result), `usuallyBought.status = "unavailable"`, the fake counts at most one `likely_to_buy` call per fill, and the token is revoked as usual.
- [ ] `History_429_not_worked_around`: after a 429 on `likely_to_buy` that is followed by a 429 on `manipulate_cart`, the response is 502 `oda-unavailable` with `added` empty and `connectionClosed`, and no call was repeated.
- [ ] `History_unavailable_usual_skipped`: a usual product whose hit has `isAvailable = false` is not added; the next available hit is used with the right source.
- [ ] `History_never_persisted_or_logged`: after a run with recognisable fake ids and names in the history, a scan of every column of every table finds none of them; a capturing logger at trace level sees no log entry or exception message containing a history id or name; no response body contains a history name; no static collection and no instance field holding history exists in the Oda namespace (extends the `No_data_kept` architecture test).
- [ ] `Usual_roles_unchanged`: `Oda_roles` and `Oda_other_household_forbidden` of T-053 still pass; in every 401/403 case the fake receives no request, including no `likely_to_buy`.
- [ ] `Contract_fixture_likely_to_buy`: the recorded `likely_to_buy` fixture and a tool-schema snapshot deserialise with the real client; removing `result`, or an entry's `id`, from the fixture gives `status = "unavailable"` (and an entry without a valid `id` is ignored when the rest is fine), never an exception or a failed fill.
- [ ] `Result_marks_usual_lines` (web, Vitest): lines with `source = "usually-bought"` show the label in nb and en, lines with `first-hit` do not; status `unavailable` shows the one-line note; nothing is written to `localStorage`/`sessionStorage`.
- [ ] `Usual_e2e` (Playwright, faked Oda API, `./scripts/e2e.sh --workers=1`): a fill whose fake history contains the second hit of one item shows that item with the label "Usually bought"; the other items do not; at 390px width the label does not overflow.
- [ ] OpenAPI contains `source` and `usuallyBought`; the generated TS client is regenerated and up to date (the existing client-drift check passes).
- [ ] `docs/domain.md` section "Oda cart, session-only" is updated with this rule (which history, matching, tie-break, fallback, nothing kept).
- [ ] `./scripts/verify.sh` is green and never calls the real Oda.

## Open questions
Each is answered by the architect when approving the spec. The acceptance criteria above assume the recommendation.
1. **How many hits per item are matched against the history?** Today the search page is `Oda:SearchPageSize` (default 5), so a usual product ranked 6th or lower is missed. Options: (a) keep 5, no extra calls; (b) raise the default page size to 10 (still one `product_search` call per 10 items, a larger response, `size` max is 20); (c) fetch page 2 for items that had no usual hit (extra calls, grows with the list size, against the "keep call count small" intent). Recommendation: (b). It adds no calls, and T-052 needs the same room for its alternatives and its exact-id match. If accepted, the default changes in `OdaOptions` and the existing `Search_is_batched_and_minimal` test uses the new size.
2. **Add one bounded `get_orders` call?** `likely_to_buy` returns only 50 products. Options: (a) no, `likely_to_buy` only; (b) additionally one `get_orders` call (page 1, size 20, products come with each order, so no `get_order` calls) for ids not already in the likely set; (c) several pages. Recommendation: (a) now, and look at real-Oda try-out results before adding (b); (b) is a small, separate addition (ids only, same fallback rule) and would raise the cost to at most one extra call.
3. **"No connection closed line".** The architect's note says no such line is wanted on the result screen. This spec reads it as: nothing is shown when the connection was closed (already the case), while the existing alert when the revoke failed stays, because the user then has a real step to take in Oda's Privacy & security. Options: (a) keep the failure alert (recommended); (b) remove it too. Answer needed only if (b) is meant.
