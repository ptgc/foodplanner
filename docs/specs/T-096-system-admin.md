# Spec: System admin and household support access (T-096)
## Goal
The person running Foodplanner (a system admin, above the per-household roles) can help a household that is stuck: find the household, see its members and invites, and apply a small set of fixes (a lost Owner, a stuck invite, a locked PIN, a locked week), without being a member of every household. Every look and every fix is tied to a reason, limited to one household at a time, written to a log nobody can edit, and visible to the household afterwards.

**Planning only.** This task is not scheduled; the architect decides when it is built. The spec assumes the recommendation of every point in [Q-023](../questions/Q-023.md): config allow-list, read plus four named fixes, no prior consent, household notice, append-only audit.

**Design in one paragraph.** Support lives on its own route tree `/api/support/...`, never under `/api/households/{householdId}`. A system admin first opens a support session for ONE household (with a reason). While the session is active, `/api/support/households/{h}/...` requests run with the ordinary tenant filter set to that household and `Role = null` (no membership, so no member policy passes). There is no new `IgnoreQueryFilters`, no new write scope, and no new role in `HouseholdRole`. Data access therefore has exactly the reach of one household's filter; the audit row and the session are what make it narrow and accountable.

## Users & roles
"System admin" is not a membership role. It is a property of one account, decided by configuration, checked on every request. An admin keeps their normal household memberships and roles as a regular user.

| Action | Anonymous | Any member (Owner/Adult/Child), not admin | Admin, kid mode | Admin, no session | Admin, read session for H | Admin, write session for H |
|---|---|---|---|---|---|---|
| `GET /api/auth/me` shows `isSystemAdmin` | 401 | false | false | true | true | true |
| Search households, search users | 401 | 403 | 403 | yes | yes | yes |
| Start, read or end own support session | 401 | 403 | 403 | yes | yes | yes |
| Read H's overview, members, invites, profiles, plan, list, recipe titles, H's access log | 401 | 403 | 403 | 403 `support-session-required` | yes | yes |
| The same for a household other than H | 401 | 403 | 403 | 403 | 403 `support-session-mismatch` | 403 `support-session-mismatch` |
| Cancel invite, promote member to Owner, clear PIN lockout, unlock week (in H) | 401 | 403 | 403 | 403 `support-session-required` | 403 `support-write-required` | yes |
| Any `/api/households/{h}/...` endpoint as a member of H (the admin's own memberships) | 401 | per role | per role (Child) | per role | per role | per role |
| Any `/api/households/{H}/...` endpoint where the admin is NOT a member of H, session or not | 401 | 403 | 403 | 403 | 403 | 403 |
| See that support accessed H (`GET /api/households/{h}/support-access`) | 401 | Owner, Adult: yes; Child: 403; non-member: 403 | 403 | n/a | n/a | n/a |

Rules that follow:
- A session never gives membership. An admin with a session for H calling a member endpoint of H gets 403 exactly like any non-member; support has no "act as Owner".
- Admin status is never cached in the auth cookie and never read from a claim a client could influence. It is evaluated from configuration on every request (below), so revocation takes effect on the next request.
- Kid mode wins: a device in kid mode (T-026) has no admin powers, even if the account is an admin (the `SystemAdmin` policy includes `FullSession`).
- Tenancy: `SupportSession` and `SupportAuditEntry` are global tables about support, not household data, and are not `ITenantEntity` (see Data model). Everything an admin reads or changes inside a household goes through that household's tenant filter and write guard, the same as a member's request.

## Behaviour

### How a user becomes system admin
- Source of truth: configuration `Support:SystemAdminEmails` (list, at most 5 entries; more fails startup). In test/prod it is a Key Vault-backed app setting (rule 7: no secrets or addresses in the repo/workflows; the Bicep module references the secret). `appsettings.json` has an empty list; `appsettings.Development.json` lists the seeded local admin `admin@foodplanner.local` (T-029) so local manual testing works (that account is also Owner of "Home"). Production-like environments never read `appsettings.Development.json`.
- `ISystemAdminDirectory.IsAdmin(userId)` (Domain interface, Infrastructure/Api implementation) is true only when the account's email is on the list (case-insensitive) AND `EmailConfirmed` is true AND the account is not locked out. It is evaluated per request; no cache beyond the request.
- Never self-assignable: no endpoint, claim, header, query or body field, invite, or registration can make an account an admin. Registering the allow-listed address as a new, unconfirmed account does not make it admin (confirmation requires the mailbox, T-072).
- Grant = add the address to the Key Vault setting and restart/redeploy the api. Revoke = remove it. The next request of that account answers 403 on every support endpoint; an open session of the revoked admin is unusable (the policy fails first) and is closed by the next cleanup/start (`ended: revoked`).
- Changing an account's email (if that is ever built) drops admin status automatically because matching is on the current confirmed email.
- Out of band, granting and revoking is itself an infra change reviewed through the normal PR/approval path; the app records no grant event. The app logs (Information, once at startup) the NUMBER of configured admins, not the addresses.

### Support session (narrow, explicit, reasoned)
- Table `SupportSessions` (global, `AdminUserId`, `TargetHouseholdId`, `Mode` Read|Write, `ReasonCategory`, `ReasonNote`, `StartedAt`, `ExpiresAt`, `EndedAt`, `EndedReason`). No foreign key to `Households` or `AspNetUsers` (so deleting either never deletes or alters support records).
- `POST /api/support/sessions` `{ householdId, mode, reasonCategory, reasonNote, password? }` (`SystemAdmin`, antiforgery like all unsafe endpoints, rate limit policy `support-session`, default 20 per hour per admin): `201` with the session `{ id, householdId, householdName, mode, reasonCategory, startedAt, expiresAt }`.
  - `reasonCategory`: `LostOwner | StuckInvite | PinLockout | LockedWeek | DataProblem | Other`. `reasonNote`: 10 to 500 characters after trimming, required (`400` ValidationProblem on `reasonNote`, `reasonCategory`).
  - Unknown household id: `404 household-not-found`.
  - `mode = Write` needs `password` (the admin's own password, verified with the Identity password hasher; a wrong one is `403 password-incorrect` and counts as a failed sign-in for lockout and rate limiting, and the account has to have a password or write mode is refused with `409 password-required`). Read mode needs no password.
  - Lifetime: `Support:ReadSessionMinutes` = 30, `Support:WriteSessionMinutes` = 15, not extendable (start a new one with a new reason). At most one active session per admin: starting a new one ends the previous one (`EndedReason = replaced`), enforced by a unique filtered index `(AdminUserId) WHERE EndedAt IS NULL` so two parallel starts leave exactly one.
- `GET /api/support/sessions/current` (`SystemAdmin`): the active, unexpired session or `404 support-session-required`. `DELETE /api/support/sessions/current`: ends it (`204`, idempotent; `EndedReason = ended`).
- `SupportContextMiddleware` (after authentication and routing, before authorization, like `HouseholdContextMiddleware` and for the same reason: policies run before endpoint filters). For endpoints carrying `SupportScopedMetadata` (the group `/api/support/households/{householdId}`) it: (1) requires `ISystemAdminDirectory.IsAdmin(userId)`; (2) loads the admin's active, unexpired session; (3) requires the route `householdId` to equal the SESSION's `TargetHouseholdId` (the session row, not the route value, is what sets the filter); (4) only then calls `RequestCurrentHousehold.SetSupport(sessionHouseholdId, sessionId)`: `HouseholdId` set, `Role` stays `null`. Any failed step leaves the context empty (no household, no rows) and the policy answers the ProblemDetails below. `HouseholdContextMiddleware` is unchanged and ignores support routes.
- ProblemDetails (all `403`, types `urn:foodplanner:problem:...`): `support-session-required` (no active session), `support-session-mismatch` (session is for another household; the body names no household), `support-write-required` (read session on a write action), `forbidden` (not an admin: the same answer as any other missing policy, so the support routes do not announce themselves).

### Policy mapping (rule 2)
Policies are the only readers of admin status and session state; feature code never touches `ClaimsPrincipal`, `ICurrentHousehold.Role`, or the config list.
- `SystemAdmin` (`Policies.SystemAdmin`): authenticated, `FullSession` (T-026) and `ISystemAdminDirectory.IsAdmin`. Attached to the group `/api/support` (sessions, search).
- `SupportSessionRead` (`Policies.SupportSessionRead`): `SystemAdmin` + an active session whose household equals the route household (the context middleware's result). Attached to the sub-group `/api/support/households/{householdId}` (secure by default, like `HouseholdMember`).
- `SupportSessionWrite` (`Policies.SupportSessionWrite`): `SupportSessionRead` + session `Mode = Write`. Attached per write action.
- Handlers: `SystemAdminHandler`, `SupportSessionHandler` (the only code that reads the support context). `HouseholdMember/Adult/Owner` are unchanged and fail for an admin without a membership because `Role` is null.
- Architecture tests extend the existing ones: every endpoint under `/api/support` has `SystemAdmin` (and every one under `/api/support/households/{householdId}` has `SupportSessionRead` or `SupportSessionWrite`); `HouseholdRouteRulesTests` still holds for `/api/households/{householdId}`; `No_role_checks_outside_policies` also forbids reading `ISystemAdminDirectory`, `ISupportContext` or the session table from feature code.

### How it crosses tenants without weakening rule 1
- No new `IgnoreQueryFilters`, no new `ITenantWriteScope` use, no new `ITenantResolution` method. `Architecture.Tests` `TenantSourceRules` are unchanged and must still pass; the test `Support_adds_no_filter_bypass` asserts the allow-list of files and the call count of `IgnoreQueryFilters` are identical before and after this task.
- Inside a session the EF filter is the one for the session household: reads of `Membership`, `Invite`, `MemberProfile`, `MealPlan`, `ShoppingList`, `Recipe` return that household only; the save guard refuses any write for another household (`TenancyViolationException`), and the save-time reference check (T-073) and the Q-012 rule (resolve ids through the filtered sets; another household's id is 404) apply to every support action.
- Two things are read across households and neither is tenant data: `Households` (the tenant root, unfiltered) for household search (id, name, createdAt), and `AspNetUsers` (global identity) for user search (id, email, confirmed). To show which households a found user belongs to, user search calls the EXISTING `ITenantResolution.GetMembershipsForUser` (id, name, role only); the architecture rule that limits its callers (today `ListHouseholds`) gets exactly one more named caller, `SupportUserSearch`. No other new cross-household read exists.
- Admin status is not a tenancy concept: nothing in `Domain.Tenancy` learns about it.

### Support actions
Reads (read or write session; each writes one audit row, see below):
- Search households: `GET /api/support/households?search=` (name contains, at least 2 characters, at most 20 results, ordered by name then id; `{ id, name, createdAt }`) and by exact household id. Search users: `GET /api/support/users?email=` (prefix match on email, at least 3 characters, at most 20: `{ userId, email, emailConfirmed, households: [{ id, name, role }] }`). Both `SystemAdmin` only, rate limited like search (`support-search`, default 60 per minute per admin). The audit row records the query text and the result count.
- `GET /api/support/households/{h}` overview: id, name, createdAt, counts (members, owners, open invites, profiles, recipes, meal plans).
- `.../members`: `[{ userId, email, role, joinedAt, pinLocked, pinLockedUntil }]` (lockout state read from `UserPin`, no hash, no failed count; for members only).
- `.../invites`: `[{ id, role, status (Open|Accepted|Expired|Revoked), createdAt, expiresAt, acceptedAt, createdByUserId }]`, last 50, never a token or hash.
- `.../profiles`: kid profiles (id, name, avatar). `.../meal-plans/{weekStart}`: the plan as the household API returns it (entries, locked, lockedBy). `.../shopping-lists/{weekStart}`: the list as returned to members. `.../recipes`: titles list (id, title, createdAt, paged); no recipe detail, no images, no household export. `.../access-log`: this household's audit rows (the same data the household sees, plus the admin's address and note).
- Read-only means read-only: none of these endpoints saves anything except the audit row; the support DbContext write guard also refuses any other write in a read session (policy plus `ISupportContext.Mode` check in the audit writer; see tests).

Writes (write session only; each is a named endpoint that calls the same domain code as the Owner endpoint it mirrors, never a copy of the rules):
- `DELETE /api/support/households/{h}/invites/{inviteId}` cancel an open invite (`Invite.Revoke`, T-048 rules; not open or other household: `404 invite-not-found`). Support cannot create or re-send an invite (invites are links; the plain token exists only for the creator).
- `PUT /api/support/households/{h}/members/{userId}/role` `{ "role": "Owner" }` promote an EXISTING member to Owner (other values: `400`; the member must belong to H: `404 member-not-found`, including for a user who is a member only of another household). Support never demotes, removes, or adds a member. Same household row lock and re-check as T-048, so concurrent Owner changes serialise. This is both "transfer" and "restore": the new Owner then manages the rest (demote the old Owner, etc.).
- `POST /api/support/households/{h}/members/{userId}/pin-unlock` clears `FailedCount` and `LockedUntil` of that member's `UserPin` (T-026), only when the user is a member of H (`404 member-not-found` otherwise); it never reads, sets, resets or removes a PIN. Idempotent, `204`.
- `DELETE /api/support/households/{h}/meal-plans/{weekStart}/lock` unlock a week (the T-026 unlock domain method; `weekStart` must be a Monday; `204`, idempotent). Support does not lock weeks.
- Optimistic concurrency and conflicts follow the corresponding household endpoint (`409 ...-conflict`).
- Not provided at all (see Out of scope): impersonation, editing recipes or plans, deleting a household or account, password/email changes, reading or changing secrets.

### Audit log (who, what, which household, when, why; never editable)
- Table `SupportAuditEntries` (global, migration `AddSupportAccess`): `Id`, `At` (server clock `TimeProvider`), `AdminUserId`, `AdminEmail` (snapshot), `SessionId` (nullable for search and session endpoints before a session exists), `TargetHouseholdId` (nullable, plain column; named so it is not a `HouseholdId` property, which the model rules reserve for tenant/catalog entities), `TargetHouseholdName` (snapshot), `Action` (closed set of constants, e.g. `session.start`, `session.end`, `search.households`, `search.users`, `view.overview`, `view.members`, `view.invites`, `view.profiles`, `view.meal-plan`, `view.shopping-list`, `view.recipes`, `view.access-log`, `invite.cancel`, `member.promote-owner`, `member.pin-unlock`, `week.unlock`, `access.denied`), `Mode`, `ReasonCategory`, `ReasonNote` (copied from the session so a row stands alone), `Detail` (small JSON of ids and counts, never recipe content, tokens, PINs, hashes or passwords), `Outcome` (`Success | Denied | Failed`). No foreign keys, so deleting a household (T-044), an account (T-095) or a session never removes or alters rows; `Detail`/snapshots keep them readable afterwards.
- Every endpoint under `/api/support` is registered with `SupportAuditedMetadata(action)`; a shared endpoint filter writes the row in the same database transaction as the action, before the response is sent. Fail closed: if the audit row cannot be written, the whole request fails with a 500 ProblemDetails and neither the data nor the change is returned or saved. Denied attempts by an admin (`support-session-mismatch`, `support-write-required`) write an `access.denied` row with the attempted household id; a non-admin never gets audit rows (nothing to attribute).
- Append-only: (1) the save interceptor refuses any Modified or Deleted `SupportAuditEntry` (`InvalidOperationException`, no ProblemDetails path because no code attempts it); (2) the migration adds a SQL trigger `TR_SupportAuditEntries_AppendOnly` that raises on UPDATE and DELETE for every login; (3) no endpoint, admin or not, edits or deletes audit rows; (4) `ExecuteUpdate`/`ExecuteDelete` and raw SQL are already forbidden in src (architecture rules). Each row is also written to the structured log (`SupportAudit` category) for a second copy. Rows are kept indefinitely (Q-023.5).
- Sessions are also append-style: `EndedAt`/`EndedReason` are set once; an expired session is simply read as ended from `ExpiresAt`, no job writes it.

### What the household sees
- `GET /api/households/{h}/support-access` (Owner or Adult, i.e. `HouseholdAdult`; household-scoped like every other household route, found through the membership of the caller): the last 90 days of support activity for H from `SupportAuditEntries` (rows where `TargetHouseholdId = ` the policy-verified route household, never a client value): `[{ at, mode, reasonCategory, action, outcome }]`, newest first, paged. The admin appears only as "Foodplanner support" (no name, no address); `reasonNote` and `Detail` are not returned. A Child, a kid-mode session and a non-member get 403; another household's rows are never returned.
- Web: household settings has a "Support access" section (Owner and Adult): the list above, and a "New since your last visit" badge (client-side, last-seen timestamp in local storage). Strings through `t()` (nb + en).
- Email: when a WRITE session starts and for each write action, all Owners of H get an email through the existing email sender (`IEmailSender`): "Foodplanner support accessed your household on <time> for <reason category>: <action>". A failing email is logged and never undoes or blocks the action (the audit row is the record). Read-only sessions send no email.

### Admin UI sketch (informative; built by a frontend task; desktop first, usable at 390px, touch targets at least 44px)
Route `/support`, no menu link; shown only when `GET /api/auth/me` has `isSystemAdmin` (the server is the only authority, a direct visit by a non-admin gets the API's 403 and a "not found" page).
```
/support (desktop, 2 columns)
+------------------------------------------------------------+
| Support                          admin@...   [ Sign out ]  |
+-----------------------+------------------------------------+
| Search                | Household "Hansen" (read, 27 min)  |
| [households v] [ ... ]|  Reason: Lost owner - "owner left" |
|  Hansen   2024-03     |  [Overview][Members][Invites]      |
|  Hansen-Berg 2025-01  |  [Profiles][Plan][List][Recipes]   |
|                       |  [Access log]                      |
| Users (email prefix)  |  Members                           |
|  ola@... Hansen:Adult |   ola@..   Adult  PIN locked 2 min |
|                       |            [Clear PIN lock]  (W)   |
| [ Start session ]     |   mia@..   Child                   |
|  household, read/write|            [Make Owner]       (W)  |
|  reason + note, (pwd) |  Invites: Open Adult  [Cancel] (W) |
+-----------------------+------------------------------------+
(W) = visible only in a write session, with a confirm dialog naming household and action.
```
A banner shows household, mode, remaining time and an "End session" button on every support screen. Actions in the UI always show a confirmation naming the household, the user and the effect. Narrow screens stack the columns.

## Out of scope
- Impersonation ("log in as" a member), acting as a member, or any session that gives membership.
- Editing recipes, plans, lists, pantry or any household content; deleting or creating households; export of a household by support; creating or re-sending invites; adding or removing members; demoting an Owner.
- Account-level support: password reset, email change, deleting a user (T-095), clearing or setting a PIN, unconfirming accounts.
- A UI or API to grant or revoke admin status (it is configuration), more than one admin tier, admin MFA, multi-person approval, consent flow for the household, alerting on support activity, external immutable audit storage, audit retention or purge, audit export.
- Cross-household analytics, usage dashboards, billing.
- Support chat or ticketing integration.
- The Azure/Key Vault provisioning itself beyond referencing the `Support:SystemAdminEmails` secret in the Bicep parameters.

## Acceptance criteria (each must be testable)
Test names are the Api.Tests / Architecture.Tests / Domain.Tests names to create.

Becoming admin
- [ ] `Allow_listed_confirmed_email_is_admin`: with `Support:SystemAdminEmails` = [a@x], the confirmed account a@x (case-insensitive) gets `GET /api/auth/me` `isSystemAdmin` true; an account b@x gets false.
- [ ] `Unconfirmed_allow_listed_account_is_not_admin`: register a@x without confirming; `/api/support/households` is 403 and `isSystemAdmin` false.
- [ ] `Admin_status_cannot_be_self_assigned`: a normal user sending `isSystemAdmin`, `role=SystemAdmin`, an `X-System-Admin` header, a crafted claim in a request body, registering or accepting an invite with the allow-listed address does not become admin; there is no endpoint that writes the list (architecture test `No_endpoint_writes_admin_configuration`: the list is read only by `ISystemAdminDirectory`).
- [ ] `Revoking_admin_takes_effect_on_next_request`: remove the address from configuration (options reload in the test host); the same cookie gets 403 on `/api/support/...`, an open session cannot be used, and `/me` shows false. Adding it back (a new session is needed after a revoke only if the old one expired) works without re-login.
- [ ] `Admin_in_kid_mode_has_no_admin_powers`: an admin account with an active kid profile (T-026) gets 403 on every `/api/support` endpoint and `isSystemAdmin` false.
- [ ] `Admin_config_is_not_in_repo_and_capped`: a test over `appsettings.json` and `infra/**/*.bicepparam` finds `Support:SystemAdminEmails` empty or a Key Vault reference only; more than 5 addresses fails host startup; `appsettings.Development.json` is the only file naming `admin@foodplanner.local` and is not loaded when `ASPNETCORE_ENVIRONMENT=Production`.

Normal users can never reach support
- [ ] `Non_admin_gets_403_on_every_support_endpoint`: a test enumerates `EndpointDataSource`, takes every endpoint under `/api/support`, and calls it as an Owner of H, an Adult, a Child member, a kid-mode session and a user with no household: all `403` ProblemDetails (`forbidden`); anonymous gets `401`. Nothing is written (no audit rows, no sessions).
- [ ] `Every_support_endpoint_is_policy_protected`: architecture test: every endpoint under `/api/support` carries `SystemAdmin`; every one under `/api/support/households/{householdId}` carries `SupportSessionRead` or `SupportSessionWrite`; none is `AllowAnonymous`; none lies outside the two route trees.
- [ ] `Admin_without_membership_cannot_use_household_routes`: an admin who is not a member of H, with and without a read or write session for H, gets 403 on `/api/households/{H}/recipes`, `/meal-plans/{w}`, `/members` and `DELETE /api/households/{H}`; `GET /api/households` does not list H.
- [ ] `Support_session_does_not_grant_membership_policies`: with a write session for H, `HouseholdMember`, `HouseholdAdult` and `HouseholdOwner` policies all fail for the admin on H's household routes (`ICurrentHousehold.Role` is null).

Admin reaches only the session household
- [ ] `Admin_without_session_gets_support_session_required`: admin calls `GET /api/support/households/{H}/members` with no session: `403 support-session-required`, one `access.denied` audit row, no data.
- [ ] `Admin_session_for_A_cannot_read_B`: two households with data in every entity of the isolation registry; a session for A; every read endpoint called with householdId = B returns `403 support-session-mismatch`, the body contains no id or name of B, and an `access.denied` row is written with the attempted household B.
- [ ] `Admin_session_for_A_sees_only_A_data`: with a session for A, every read endpoint returns A's rows only; the set of ids in the responses is a subset of A's ids read by raw SQL, and none of B's ids appears.
- [ ] `Support_write_actions_cannot_touch_B_through_A_session`: with a write session for A, promoting a user who is a member only of B (`404 member-not-found`), cancelling B's invite id under A (`404 invite-not-found`), unlocking B's week under A (no change to B), clearing the PIN lock of a user who is a member only of B (`404 member-not-found`, B's user's `UserPin` unchanged); `CrossHouseholdLinks.AssertNoneAsync` passes and B's rows are unchanged by raw SQL.
- [ ] `Session_household_comes_from_the_session_not_the_route`: a request whose route household differs from the session's never executes a query with the route value (the test uses a command interceptor on the support request and finds no query filtered to the route household).
- [ ] `Support_adds_no_filter_bypass`: architecture test: the files that call `IgnoreQueryFilters` or `ITenantWriteScope.ForHousehold` are exactly the pre-existing allow-list (`TenantResolution.cs` plus the documented test-support entries), the number of `IgnoreQueryFilters` call sites in `src` is unchanged, `GetMembershipsForUser` is called only from `ListHouseholds` and `SupportUserSearch`, and `TenantIsolationCoverageTests` still passes.
- [ ] `Search_exposes_only_listed_fields`: household search returns only `{ id, name, createdAt }`, user search only `{ userId, email, emailConfirmed, households[{id,name,role}] }`; no password hash, security stamp, PIN data or token appears in any support response (checked by serialising every response and scanning for the known column names and the seeded hash values).
- [ ] `Search_limits`: a 1-character household search or 2-character user search is `400`; results are capped at 20; the 61st search within a minute is `429 rate-limited` with `Retry-After`.

Sessions
- [ ] `Start_session_requires_reason`: missing or short `reasonNote`, unknown `reasonCategory` or unknown `mode` returns `400` ValidationProblem on the field; a valid start returns `201` and writes `session.start` with the reason; an unknown household is `404 household-not-found`.
- [ ] `Write_session_requires_password`: `mode = Write` without `password` is `400`; a wrong password is `403 password-incorrect` and no session exists; the right one gives `201`. Read mode needs no password.
- [ ] `Session_expires`: with the fake clock advanced past 30 minutes (read) or 15 minutes (write) the session is unusable (`403 support-session-required`), `GET .../sessions/current` is `404`, and a new session can be started.
- [ ] `One_active_session_per_admin`: starting a second session ends the first (`EndedReason = replaced`), and the first no longer authorises; two parallel starts leave exactly one active row (unique index).
- [ ] `End_session`: `DELETE /api/support/sessions/current` returns `204` (also when none), the audit has `session.end`, and the former session id no longer authorises.
- [ ] `Session_endpoints_are_rate_limited`: the 21st session start within an hour returns `429`.

Read-only versus write
- [ ] `Read_session_cannot_write`: with a read session every write action (cancel invite, promote, pin-unlock, week unlock) returns `403 support-write-required`, an `access.denied` audit row is written, and the database is unchanged (raw SQL).
- [ ] `Read_endpoints_write_only_the_audit_row`: for each read endpoint, a change tracker/SQL command interceptor sees exactly one insert, into `SupportAuditEntries`, and no other write.
- [ ] `Cancel_invite`: write session; an open invite is revoked (`204`, the link answers `410 invite-invalid`); an accepted, expired or unknown invite is `404 invite-not-found`; audit `invite.cancel` has the invite id in `Detail`.
- [ ] `Promote_member_to_owner`: write session; an Adult member becomes Owner (`200`), the household has two Owners, the previous Owner is unchanged; a non-member is `404 member-not-found`; `role` other than `Owner` is `400`; promoting a member who already is an Owner is a `200` no-op; the household row lock is taken (the T-048 lock test holds the lock in SQL, the call waits); support has no endpoint that demotes or removes a member (route enumeration test).
- [ ] `Restore_owner_when_owner_left`: a household whose only Owner left (Owner count 0 by raw SQL set-up, the state the API would refuse) with an Adult member: promote succeeds, the new Owner then passes `HouseholdOwner` on that household's normal endpoints.
- [ ] `Clear_pin_lockout`: a member with `UserPin` `LockedUntil` in the future and `FailedCount` 3 is cleared to 0 and null (`204`), the correct PIN then unlocks, the PIN hash is unchanged (read before and after), and the lockout state is visible in `.../members` before and not after. A user who is a member only of another household is `404`.
- [ ] `Unlock_week`: a locked week is unlocked (`204`, `lockedAt` null in the plan read, kid-mode write now succeeds), unlocking an unlocked or missing week is `204` and writes no plan row, a non-Monday is `400` on `weekStart`.
- [ ] `Support_actions_reuse_domain_code`: architecture test: the support endpoints call the same application methods as `HouseholdMemberEndpoints`/`MealPlanLockEndpoints` (a shared service type), and contain no `Role` comparison or `HouseholdRole` literals except the constant `Owner` argument.

Audit
- [ ] `Every_support_endpoint_writes_audit`: for every endpoint under `/api/support` the metadata `SupportAuditedMetadata` is present (architecture test) and a call writes exactly one `SupportAuditEntries` row with the right `AdminUserId`, `AdminEmail`, `TargetHouseholdId`, `TargetHouseholdName`, `SessionId`, `Action`, `Mode`, `ReasonCategory`, `ReasonNote`, `Outcome` and `At` from the fake clock.
- [ ] `Audit_failure_fails_the_request`: with the audit insert made to fail (fault-injecting interceptor), a read returns 500 ProblemDetails with no household data in the body, and a write leaves the database unchanged (same transaction).
- [ ] `Audit_rows_contain_no_secrets`: after running every support action, no audit `Detail` or log line contains a password, PIN, token, hash or recipe content (scan against the seeded values).
- [ ] `Audit_is_append_only`: a SaveChanges that modifies or deletes a `SupportAuditEntry` throws; raw SQL `UPDATE` and `DELETE` on `SupportAuditEntries` with the application's database login fail with the trigger's error; `TRUNCATE`-style access is not available to that login; there is no route in the OpenAPI document that modifies or deletes audit rows.
- [ ] `Audit_survives_deleting_household_and_user`: delete household A through `DELETE /api/households/{A}` (T-044) and delete the admin's account row; the audit and session rows remain unchanged, readable, and their snapshots still name the household and admin. Model test: `SupportSessions` and `SupportAuditEntries` have no foreign keys.
- [ ] `Support_tables_are_exempt_with_reasons`: `TenantCoverage.Exempt` contains `SupportSession` and `SupportAuditEntry` each with a reason ("global support record, not household data; keyed by TargetHouseholdId without FK"); `TenantIsolationCoverageTests` is green; the model rule about `HouseholdId` properties passes because the column is `TargetHouseholdId`.
- [ ] `Audit_is_mirrored_to_log`: each audit row also yields one structured log event in category `SupportAudit` (captured by the test logger) without the note's content beyond the category.

What the household sees
- [ ] `Household_can_see_support_access`: after a support session with a promote action on H, an Owner and an Adult of H get `GET /api/households/{H}/support-access` with rows for `session.start` and `member.promote-owner` (mode, reasonCategory, action, outcome, time), the admin shown only as "Foodplanner support", no `reasonNote`, no `Detail`, no admin id or email in the JSON.
- [ ] `Support_access_visible_only_for_own_household`: A's members never receive B's support rows; a Child member, a kid-mode session and a non-member get 403; the endpoint is under `HouseholdAdult` (policy test).
- [ ] `Owners_emailed_on_write`: a write session start and each write action send one email to every Owner of H (fake `IEmailSender`) and none to Adults or Children; a read session sends none; a failing sender does not fail or roll back the action and the audit row exists.
- [ ] `Household_log_excludes_denied_and_search`: the household-facing `support-access` list contains only rows with `Outcome` Success or Failed for actions that touched the household; an `access.denied` row (the attempted household B of an admin whose session is for A) and search rows never appear in B's list, because no B data was accessed. The `access.denied` row exists only in the table and the log mirror.

Platform
- [ ] `Migration_AddSupportAccess`: applies on a fresh database and on one with data; `SupportSessions`, `SupportAuditEntries`, the unique filtered index on active sessions and the trigger exist; `No_pending_model_changes` passes; deleting a household still removes everything (the `MigrationTests` pattern).
- [ ] `Support_endpoints_in_openapi_and_client`: the OpenAPI document contains the support endpoints; the TS client is regenerated, not hand-edited; `./scripts/verify.sh` is green.
- [ ] `Domain_doc_and_adr_updated`: `docs/domain.md` has a "System admin and support access" section (the design paragraph, the policies, the audit table, the one extra `GetMembershipsForUser` caller); an ADR is added if the architect wants the decisions of Q-023 recorded as one.
- [ ] `Admin_ui_visible_only_to_admin`: Vitest: `/support` renders only when `me.isSystemAdmin`; the write-only buttons are absent in a read session; each action shows a confirmation naming household and effect; end-to-end Playwright (T-027 harness) for the seeded admin: start a read session, see members, end the session.

## Open questions
- [Q-023](../questions/Q-023.md): (1) how admin is granted and revoked (config allow-list), (2) read versus write scope (read plus four named fixes), (3) prior consent from the household (none in v1), (4) what the household sees (in-app log plus email on writes), (5) audit immutability and retention (append-only with trigger, kept forever). Open; the spec assumes the recommendations.
