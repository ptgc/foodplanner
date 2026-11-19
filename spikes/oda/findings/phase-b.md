# T-001 Phase B findings (2026-11-19)

Run by the architect against their own Oda account with the spike client. Raw cart output and the personal recommendation lists (`likely_to_buy`, `unique_for_you`) are deliberately not committed (private shopping history); they are summarised here. `search-quality.md` and `tools.json` (tool schemas only, no tokens) are committed.

## Login
- OAuth 2.0 with dynamic client registration (`/o/register/`), PKCE S256 and a loopback redirect worked in a normal browser sign-in and approval.
- The token stayed in user secrets only; every later call authenticated.
- **Not tested:** token refresh after the access token expires (the 1-hour check was not run). Access/refresh lifetime and refresh rotation are therefore unknown.

## Server and tools
- Server reports itself as "Oda MCP Server" 2.3.0 (the Phase A manifest said 2.0.0, so the server changes underneath us).
- 25 tools, the same set as the Phase A public manifest. Full schemas in `tools.json`.
- Used: `product_search`, `get_cart`, `manipulate_cart`, `likely_to_buy`/`unique_for_you` (read-only, T-107 check). Not used: `clear_cart`, product lists, recipes, orders/delivery, addresses and delivery slots, checkout (not offered).

## Product fields
Only: `id`, `name`, `description`, `price`, `unitPrice`, `unitName`, `brand`, `brandId`, `url`, `availability{isAvailable,description,code}`.
- No image and no SKU/EAN. The Oda product id plus the shop URL are the only identifiers.
- So "name, price, image, SKU" holds partly: name, price and id yes; image and SKU no.
- `availability` is present per product, so sold-out items can be detected at search time.

## Search quality (nb names)
Table in `search-quality.md` (19 catalog names, top 3 hits each). Basic Norwegian names give relevant top hits (melk, smør, løk, hvitløk, gulrot, potet, tomat, ris, rømme, fløte, hvetemel, sukker, margarin, brokkoli, laks, pasta).
Weaknesses:
- "karbonadedeig": top hit is kjøttdeig (14%), the real karbonadedeig is second.
- "Nøytral olje": hits include olivenolje and kokosolje; rapsolje is first, so a mapping to a specific product is needed.
- "egg" and "kyllingfilet" return several pack sizes and prices (kyllingfilet from 90.90 to 185.70), so choosing pack size and quantity needs rules, not just the top hit.
- Hits are brand/variant products (e.g. fullkorn pasta first for "pasta"); preference such as organic, brand or house label must come from the household, not from search.

## Cart behaviour
- `add <id> 1` (a butter product, search price 44.90) took the cart from 8 items / 699.60 to 9 items / 744.50: the total moved by exactly the search price.
- **Open observation:** the cart line for that product showed 61.40, which does not match the +44.90 delta. The line amount may be pre-discount or computed differently; `get_cart` has a `total_breakdown` with discounts that was not examined. Do not treat the line amount as the price until understood.
- `clear-test-items` restored exactly 8 items / 699.60; the other cart lines were unchanged. Quantity is a delta (as documented), so add and remove round-trip cleanly.
- The cart carries a validation message (an existing item is sold out and blocks checkout), so the cart itself reports availability problems that an integration must surface.
- No checkout or payment through MCP; the user pays in the shop via the cart `url`.

## T-107 offer check
- No offer, campaign or discount field on `product_search` results (raw output checked for several products), on `likely_to_buy` (50 products) or on `unique_for_you` (19 products).
- Both recommendation tools return the same product schema as search and take only an optional `intent` parameter.
- Conclusion: offers are not reachable through MCP product data. Only `get_cart` may show discounts, and only for items already in the cart.

## Gaps
- Token refresh untested; token lifetime unknown.
- Cart line amount vs total delta unexplained (see above).
- Terms question (Q-024) and token storage (Q-025) unanswered, and not decided here.
