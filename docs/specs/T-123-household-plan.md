# Spec: Household plan, Claude limits per household (T-123)
## Goal
Every household has a plan ("abonnement") that says how much Claude help it may use: photo imports per day, and line help per day and per month, each either a number or unlimited. Households without a custom plan keep today's rules (from configuration: 2 photo imports per day). The architect's own household is unlimited. Only the super admin changes a plan, on the admin pages, inside a write support session, and every change is audited and emailed to the household's Owners like other support writes. The app-wide monthly caps stay as a safety net on the shared Anthropic key, whatever the plan says. Members can see how much of their own quota is left.

Decisions from Q-034 that this builds on: one shared app key for every household, limits as configuration, the household's own key postponed (T-120). Storage shape: [Q-038](../questions/Q-038.md); this spec assumes the recommendation (a tenant table).

## Users & roles
"Super admin" is the T-096 system admin (config allow-list, confirmed email, not in kid mode). It is not a household role.

| Action | Anonymous | Child member / kid mode | Adult | Owner | Admin, no session | Admin, read session for H | Admin, write session for H |
|---|---|---|---|---|---|---|---|
| `GET /api/households/{h}/imports/photo-usage` (own household, extended, below) | 401 | 403 | yes | yes | n/a (not a member: 403) | 403 (no membership) | 403 (no membership) |
| Photo import / line help being limited by the plan | 401 | 403 (unchanged, `HouseholdAdult` + `FullSession`) | yes | yes | n/a | n/a | n/a |
| `GET /api/support/households/{h}/plan` (view the plan) | 401 | 403 | 403 | 403 | 403 `support-session-required` | yes | yes |
| `PUT /api/support/households/{h}/plan` (set), `DELETE .../plan` (reset to defaults) | 401 | 403 | 403 | 403 | 403 `support-session-required` | 403 `support-write-required` | yes |
| Either of the above for a household other than the session's | 401 | 403 | 403 | 403 | 403 | 403 `support-session-mismatch` | 403 `support-session-mismatch` |

Rules that follow:
- No household role can read the plan details or change any plan. A household member sees only their own household's `dailyLimit` and `remainingToday` through the existing photo-usage endpoint; they never see the plan name, the line-help limits, or another household's plan or usage.
- An Owner cannot raise their own limits, not even to buy more: there is no member-facing plan endpoint at all.
- Tenancy: the plan belongs to one household (`HouseholdPlan` is an `ITenantEntity`). Inside a support session the tenant filter is the SESSION household's (T-096), and the write guard refuses a plan row for any other household. Handlers never read the route household id; they use `ICurrentHousehold`.

## Behaviour

### What a plan is
Three limits, each `Unlimited` or `Limited(n)` with `n` an integer 0 to 10000 (0 means "none allowed"), plus an optional label:

| Field | Meaning | Counter used (exists today) |
|---|---|---|
| `photoImportsPerDay` | recipe-from-photo imports per UTC day (T-118) | `AiHouseholdUsage.PhotoDay/PhotoDayCalls` |
| `lineHelpPerDay` | AI help for unresolved import lines per UTC day (T-077) | `AiHouseholdUsage.Day/DayCalls` |
| `lineHelpPerMonth` | the same per UTC month | `AiHouseholdUsage.MonthStart/MonthCalls` |
| `name` | optional label shown to the admin (for example "Unlimited", "Family"), at most 40 characters, no control characters | none |

There is no catalogue of named plans in this task: a plan is just the four values on one household. A "plan name" is a free label, not a reference to anything.

### Defaults and "no plan"
- The default plan is the configuration that exists today: `AiPhotoImport:DailyPerHousehold` (default 2), `AiImport:DailyCallsPerHousehold` (default 20), `AiImport:MonthlyCallsPerHousehold` (default 200). The keys keep their names and meaning ("the default plan"); nothing is renamed, so existing user secrets and Key Vault values keep working. Configuration cannot express "unlimited" as a default (a value at or below 0 keeps today's meaning: nothing allowed).
- No `HouseholdPlan` row means "the default plan, read live from configuration". New households do NOT get a row: when the configuration changes, every household without a custom plan follows it on the next request. A household that has a row is unaffected by configuration changes (except the global caps).
- `DELETE .../plan` removes the row (reset to default). `PUT` creates or fully replaces it.

### The global safety net (unchanged)
`AiImport:GlobalMonthlyCalls` (line help) and `AiPhotoImport:GlobalMonthly` (photos) are checked for every household, unlimited or not. An unlimited household whose request would pass the cap gets `GlobalLimit` (`503 photo-import-capacity` for photos, the existing "no AI help" fallback for line help). A global cap at or below 0 still refuses everyone. The feature switches (`Enabled`, the key) still win over any plan: a plan never turns Claude on.

### How `AiUsageLimiter` reads the plan
- Pure domain logic in `Domain` (no EF): `AiLimit` (`Unlimited` or a non-negative count), `HouseholdPlanLimits` (the three limits), and `HouseholdPlanLimits.Resolve(plan?, defaults)` which returns the plan's limits when a row exists and the defaults otherwise. A limit of 0 is "refuse" (`HouseholdLimit` without any database write), exactly as `DailyPerHousehold <= 0` behaves today.
- `TryReserveAsync` and `TryReservePhotoAsync` read the current household's `HouseholdPlan` once (`AsNoTracking`, through the tenant filter, so only the current household's row can match), resolve the limits, then run the SAME atomic conditional UPDATE as today. An unlimited limit is passed to the update as `int.MaxValue`, so the condition never fails and the counters keep counting (the data is needed for the usage metrics, T-121). The check-and-increment stays one statement; the plan read before it is the only new query, and a plan changed a moment before can apply to at most the requests already in flight (accepted, documented in the limiter's comment).
- Order inside a reservation is unchanged: household limit first, then the global cap; when the global cap refuses, the household reservation is given back (also for unlimited households, which just decrement what they incremented).
- `ReleasePhotoAsync` is unchanged.
- `PhotoRemainingTodayAsync` is replaced by `PhotoQuotaAsync` returning `PhotoQuota(bool Unlimited, int? DailyLimit, int? Remaining)`: `Unlimited` true gives null limit and null remaining; otherwise `Remaining = max(0, limit - used today)`. Lowering a limit below what a household already used today leaves `Remaining` 0 (no reset, no refund); raising it takes effect on the next request; counters are never reset by a plan change.
- The new architecture rule `AiUsageBulkWriteScopeTests` still holds: the limiter's bulk updates touch only `AiHouseholdUsages` and `AiGlobalUsages`; `HouseholdPlans` is only read there, never bulk written.

### What members see (existing endpoint, extended)
`GET /api/households/{h}/imports/photo-usage` (Owner and Adult) returns `{ enabled, unlimited, dailyLimit, remainingToday }`:
- feature off: `enabled: false`, `unlimited: false`, limits as today (0 remaining), unchanged;
- limited plan or default: `unlimited: false`, `dailyLimit` and `remainingToday` are the plan's numbers;
- unlimited plan: `unlimited: true`, `dailyLimit: null`, `remainingToday: null`.
`dailyLimit` and `remainingToday` become nullable in the contract; `RecipeImportResponse.photoUsage` and the `429 photo-import-limit-reached` / error ProblemDetails extensions (`remainingToday`, `dailyLimit`) use the same shape (for unlimited: `unlimited: true` and no numbers; the 429 can never occur for an unlimited household). The TS client is regenerated; the photo upload screen (T-119) must treat `unlimited` as "no counter, send button never disabled by the count". If T-119 is built before this task, this task adjusts it minimally; if after, T-119 consumes the new shape.

### Support endpoints (T-096 pattern)
Under `/api/support/households/{targetHouseholdId}` (the existing group: `SupportSessionRead`, session household from the session row, never the route):
- `GET .../plan` (read or write session; audit action `view.plan`, kind View): `200` `{ source, name, photoImportsPerDay, lineHelpPerDay, lineHelpPerMonth, defaults }`. `source` is `Default` or `Custom`. Each limit is `{ unlimited: bool, limit: int? }`. For a household without a row the three values are the effective defaults. `defaults` carries the configured defaults, so the admin page can show "default is 2".
- `PUT .../plan` (`SupportSessionWrite`; audit action `plan.set`, kind Write): body `{ name?, photoImportsPerDay, lineHelpPerDay, lineHelpPerMonth }`, all three limits required and explicit (each `{ unlimited, limit }`; PUT replaces the whole plan, an omitted limit is `400`, not "unchanged"). Validation (`400` ValidationProblem on the field): `limit` required and 0 to 10000 when `unlimited` is false; `limit` must be absent when `unlimited` is true; `name` trimmed, at most 40 characters, no control characters (empty becomes null); a monthly line-help limit below the daily one is allowed (the tighter one simply wins). Returns `200` with the same body as `GET`. Idempotent: the same body twice leaves one row and the same values.
- `DELETE .../plan` (`SupportSessionWrite`; audit action `plan.reset`): removes the row, `204`, idempotent (also when there is no row; still audited).
- `plan.set` and `plan.reset` are added to `SupportActions` and the audit constants; `view.plan` likewise. The audit `Detail` holds numbers only: the previous and the new value of each limit (`unlimited` or the number) and the label length. The label text itself is not stored in `Detail` (it keeps the "ids and counts" rule from T-096).
- Because they are `Write` kind, the existing T-096 behaviour applies without new code: one audit row in the same transaction as the change (fail closed), the structured log mirror, an email to every Owner of the household after commit ("Foodplanner support accessed your household ...: plan.set"), and the household's own `GET /api/households/{h}/support-access` shows the row (mode, reason category, action, outcome; no note, no detail, no admin identity). The action text in the household-facing list is the action string; translating it is the job of the household settings "Support access" section (not built).
- The overview (`GET .../{h}`) `plan` field: the plan's `name` when a custom plan has one, otherwise null (the page shows "Default" for no row and "Custom" for a row without a name from the plan panel). No new counts.
- Support still cannot change anything else: no new generic edit appears.

### Admin UI (web/app/support, desktop first, usable at 390px, touch targets at least 44px)
- A new panel tab "Abonnement" / "Subscription" (panel id `subscription`, not `plan`, which is the meal plan panel) in the active-session view, next to Overview, Members and so on. The overview's existing "Plan" row shows the plan name or "Default".
- Read session: shows source (Default or Custom), the label, and three rows (photo imports per day, line help per day, line help per month), each "Unlimited" or the number, with "default: N" beside them when the plan is custom. Today's usage is not shown (out of scope).
- Write session: an "Edit plan" form: label field, and for each limit an "Unlimited" switch and a number field (disabled while unlimited), a Save button, and "Reset to default" (only when `source` is `Custom`). Each of Save and Reset opens the confirmation dialog the other write actions use, naming the household and the effect ("Set Hansen to: photo imports unlimited, ..."). In a read session the form and both buttons are absent.
- Strings through `t()` in nb and en. Server errors (`400` on a field) are shown beside the field.

### Data model and migration (Migration: yes; assumes Q-038 option 1)
- Table `HouseholdPlans`: `Id`, `HouseholdId` (FK to `Households`, cascade delete like other tenant tables, unique index so there is at most one row), `Name` (nvarchar(40) null), `PhotoImportsPerDay` int null, `LineHelpPerDay` int null, `LineHelpPerMonth` int null (null = unlimited; a missing row = default), `CreatedAt`/`UpdatedAt` from the existing base. Check constraints: each of the three is null or between 0 and 10000.
- `HouseholdPlan : Entity, ITenantEntity` in `Domain/Tenancy` (or `Domain/Households`) with a factory and a `Replace(...)` mutator that validates; `HouseholdId` assigned by Infrastructure like other tenant entities. Added to `TenantIsolationRegistry` (isolation suite), `TenantCoverage`, and deleted with the household by cascade (T-044 deletion test extended). Not included in the household export (it is not household content).
- One migration `AddHouseholdPlans`, no data backfill: existing households have no row and so follow the defaults (behaviour does not change on deploy). Only this task adds a migration at that time (rule 9).

### Local seeding (the architect's household)
- `LocalAdminSeeder` (Development only) ensures that household "Home" has a custom plan with every limit unlimited and the label "Unlimited". It is idempotent and runs on every `seed-admin`; it never touches the demo household "Håvard og Rikke" (T-122), which keeps the default so the limit can be tested locally. Home's plan is applied through ONE new named `ITenantResolution` member (for example `EnsureHouseholdPlan(householdId, plan)`), because only the resolution implementation may write outside a request; the seeder is its only caller, and the architecture allow-list in `TenantSourceRules` names that one more caller.
- Production: nothing is seeded. The architect's real household is made unlimited by a super admin through the admin page in a write session (this is the real, audited path). There is no configuration list of "unlimited household ids" (it would be a second way to change plans without an audit row).

### Errors
All ProblemDetails (RFC 9457), `urn:foodplanner:problem:` types: `400` validation on plan fields; the usual `403` support problems from T-096 (`support-session-required`, `support-session-mismatch`, `support-write-required`, `forbidden`); `404 household-not-found` is not needed (the session household is verified at session start; if it was deleted since, the existing overview behaviour applies, `404 household-not-found`).

## Out of scope
- The household's own Anthropic key (T-120), billing, payment, self-service upgrades, a public plans page, a plan catalogue or named tiers with shared definitions.
- Limits for anything other than the three counters above (other AI features, Oda, voice, recipes count, members count).
- Changing the global monthly caps (configuration only) or making the defaults editable in the app.
- Per-user or per-profile limits; limits that reset on other periods (week, rolling 24 hours).
- Showing the household's own plan name or line-help quota to members; a usage dashboard or history for the admin (T-121 metrics).
- Resetting or editing the usage counters from the admin page.
- Changing how many photos one import may contain (`MaxPhotos`) or the rate limiter policy `PhotoImport`.
- Any new support action besides view/set/reset of the plan; any change to sessions, the audit table structure or the allow-list.
- The household settings "Support access" section (not built in T-096).

## Acceptance criteria (each must be testable)
Test names are the Domain.Tests, Api.Tests and Architecture.Tests names to create.

Domain (pure)
- [ ] `HouseholdPlanLimits_Resolve_uses_defaults_without_a_plan`: `Resolve(null, defaults)` returns the defaults; with a plan it returns the plan's three values (unlimited or number) and ignores the defaults.
- [ ] `AiLimit_validation`: numbers below 0 or above 10000 are rejected, 0 is allowed, unlimited carries no number; `HouseholdPlan.Replace` rejects a name over 40 characters or with control characters and trims an empty name to null.

Limiter (Api.Tests with Testcontainers SQL Server, fake `TimeProvider`)
- [ ] `Photo_default_plan_allows_two_a_day`: a household with no plan row gets 2 reservations in a UTC day and `HouseholdLimit` on the third (today's behaviour unchanged); `photo-usage` shows `unlimited: false`, `dailyLimit: 2`.
- [ ] `Photo_custom_limit_is_honoured`: a plan with `photoImportsPerDay = 5` allows 5 and refuses the 6th (429 `photo-import-limit-reached` with `dailyLimit: 5`); a plan with 0 refuses the first without any counter write.
- [ ] `Photo_unlimited_skips_household_check`: a household with an unlimited photo plan makes 50 reservations in one day, all `Reserved`, the counter shows 50, `photo-usage` returns `unlimited: true` with null `dailyLimit` and `remainingToday`.
- [ ] `Unlimited_does_not_skip_global_cap`: with `AiPhotoImport:GlobalMonthly = 3` and an unlimited household, the 4th photo reservation returns `GlobalLimit` (503 `photo-import-capacity`) and the household counter is back at 3; the same for line help with `GlobalMonthlyCalls`; a global cap of 0 refuses an unlimited household.
- [ ] `Line_help_plan_limits`: a plan with `lineHelpPerDay = 1` allows one call and refuses the second the same day; with `lineHelpPerMonth = 3` the 4th call in the month is refused although the day limit is not reached; `unlimited` for both lets 30 calls through; a new UTC day or month restarts the count (fake clock).
- [ ] `Plan_is_per_household`: household A unlimited and household B with the default: B is refused at its own limit while A is not; A's plan row is never returned by a query run as B (the tenant filter), and B's `photo-usage` shows B's numbers.
- [ ] `Plan_change_applies_next_request_without_reset`: a household that used 2 of 2 today, plan raised to 5: `remainingToday` is 3; plan lowered to 1 after 2 used: `remainingToday` 0 and the next import is refused; deleting the row returns to the configured default; no counter changed by any of these.
- [ ] `Concurrent_reservations_respect_a_limited_plan`: 10 parallel photo reservations against a plan of 4 give exactly 4 `Reserved` and 6 `HouseholdLimit`; with an unlimited plan all 10 are `Reserved` and the counter is 10 (atomic update unchanged).
- [ ] `Configuration_default_is_live`: changing `AiPhotoImport:DailyPerHousehold` from 2 to 3 (options change in the test host) gives a household without a row 3 imports at the next request, while a household with a custom row keeps its own value.
- [ ] `Disabled_feature_beats_the_plan`: with `AiPhotoImport:Enabled` false or no key, an unlimited household still gets `photo-usage` `enabled: false` and POST `404 photo-import-disabled`; the fake reader is not called.

Support endpoints
- [ ] `Plan_endpoints_require_admin_and_session`: as Owner, Adult, Child, kid-mode session and anonymous, `GET`, `PUT` and `DELETE /api/support/households/{h}/plan` return 403 (401 anonymous) ProblemDetails and write nothing; an admin without a session gets `403 support-session-required` with an `access.denied` audit row; a read session gets `403 support-write-required` on `PUT` and `DELETE` and the table is unchanged (raw SQL); a session for A calling with household B gets `403 support-session-mismatch` and B's plan is unchanged.
- [ ] `Admin_sets_plan_in_write_session`: write session for H; `PUT` with `{ name: "Unlimited", photoImportsPerDay: { unlimited: true }, lineHelpPerDay: { unlimited: false, limit: 40 }, lineHelpPerMonth: { unlimited: true } }` returns 200 with that plan, one `HouseholdPlans` row exists for H and none for any other household (raw SQL), a second identical `PUT` leaves one row, and `GET` returns `source: Custom`. A household without a row returns `source: Default` with the configured defaults.
- [ ] `Plan_set_is_validated`: missing limit object, `unlimited: false` without `limit`, `unlimited: true` with a `limit`, `limit` of -1 or 10001, a 41-character name, a name with a control character: each is a 400 ValidationProblem naming the field and changes nothing.
- [ ] `Plan_reset`: `DELETE` removes the row (`204`), the household is back on the defaults (`photo-usage` shows the configured values), a second `DELETE` is `204`; both are audited.
- [ ] `Plan_changes_are_audited`: `GET` writes one `view.plan` row, `PUT` one `plan.set`, `DELETE` one `plan.reset`, each with the right admin, session, target household, mode, reason and outcome; `Detail` of `plan.set` holds the old and new limit values; the audit row and the change are in one transaction (with the audit insert faulted, `PUT` returns 500 and no `HouseholdPlans` row exists); no audit `Detail` or log line contains a password or any other household's id.
- [ ] `Plan_write_emails_owners`: `PUT` and `DELETE` each send one email to every Owner of H (fake `IEmailSender`) and none to Adults or Children; a failing sender does not fail or roll back the change and the audit row exists.
- [ ] `Household_sees_plan_change_in_support_access`: after `PUT`, an Owner and an Adult of H get a `plan.set` row from `GET /api/households/{H}/support-access` (mode, reasonCategory, action, outcome, time) with no `reasonNote`, no `Detail`, no plan values, no admin identity; B's members do not see it.
- [ ] `Overview_plan_field`: the overview returns `plan: "Unlimited"` for a custom plan with that label, null for a household without a row or with an unnamed plan.
- [ ] `Plan_write_cannot_touch_another_household`: with a write session for A, no request can create, change or delete B's plan row (checked by raw SQL before and after for every plan endpoint, with B's id in the route); the write guard throws `TenancyViolationException` if a handler were to attach another household id (unit test of the guard with a `HouseholdPlan` row for B).

Tenancy and architecture
- [ ] `HouseholdPlan_is_in_tenant_isolation_suite`: `TenantIsolationCoverageTests` is green with `HouseholdPlan` registered; the isolation test shows household A's plan row is invisible and untouchable as household B.
- [ ] `Delete_household_removes_plan`: deleting a household (T-044) leaves no `HouseholdPlans` row for it (extends the existing deletion test); the plan is not in the household export.
- [ ] `Plan_has_no_member_endpoint`: route enumeration finds no endpoint under `/api/households/{householdId}` that writes a plan, and exactly the three plan endpoints (`GET`, `PUT`, `DELETE`) under `/api/support/households/{targetHouseholdId}/plan`; each carries `SupportSessionRead` or `SupportSessionWrite` and `SupportAuditedMetadata` (the existing T-096 architecture tests stay green).
- [ ] `Limiter_bulk_writes_still_scoped`: `AiUsageBulkWriteScopeTests` stays green (the limiter bulk-writes only the two counter sets; `HouseholdPlans` is read only there); `TenantSourceRules` allow-list names the one new `ITenantResolution` caller (the seeder) and `Support_adds_no_filter_bypass` still passes.
- [ ] `Migration_AddHouseholdPlans`: applies on a fresh database and on one with data (existing households get no row and behave as before); the unique index on `HouseholdId` and the check constraints exist; a second row for one household fails; `No_pending_model_changes` passes (the `MigrationTests` pattern).

Seeding
- [ ] `Seed_makes_home_unlimited`: after `seed-admin` against a test database, household "Home" has a plan with all three limits unlimited and label "Unlimited", running it twice leaves one row, the demo household has no plan row, and the seeder refuses outside Development (existing test extended). A photo import by Home's Owner is never refused by the household limit.

Web
- [ ] `Subscription_panel_read_session`: Vitest: in a read session the panel shows source, label and the three limits ("Unlimited" or the number, with "default: N" for a custom plan) and has no form or buttons; the overview's plan row shows the label or "Default".
- [ ] `Subscription_panel_write_session`: Vitest: the form disables the number field while "Unlimited" is on, "Reset to default" shows only for a custom plan, Save and Reset each open a confirmation naming the household and the effect and send the call only after confirming, a `400` is shown beside its field; every element is at least 44px high; strings exist in nb and en.
- [ ] `Admin_sets_home_unlimited_e2e`: Playwright (T-027 harness, `./scripts/e2e.sh --workers=1`) as the seeded admin: start a write session for the demo household, set photo imports to unlimited, see the confirmation, the saved plan in the panel, and a `plan.set` row in the access log; end the session.

Contract and platform
- [ ] `Plan_in_openapi_and_client`: the OpenAPI document contains the three plan endpoints and the extended `PhotoUsageStatusResponse` (`unlimited`, nullable `dailyLimit` and `remainingToday`); the TS client is regenerated, not hand-edited; existing web code compiles under strict TypeScript.
- [ ] `Domain_doc_updated`: `docs/domain.md` gets a "Household plan" section (the three limits, no row = defaults from configuration, unlimited still under the global cap, the support-only write path, the seed rule) and the "Recipe from a photo" section points to it.
- [ ] `./scripts/verify.sh` is green.

## Open questions
- [Q-038](../questions/Q-038.md): plan storage, a separate tenant table `HouseholdPlans` (recommended, assumed above) or columns on `Household`. Only the Data model, Migration, Seeding and tenancy criteria depend on the answer.
