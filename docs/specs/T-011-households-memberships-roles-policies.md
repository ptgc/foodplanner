# Spec: Households, memberships, roles, policies (T-011)
## Goal
A signed-in user can create a household and becomes its Owner. The Owner can invite others with a single-use, expiring link. A user can belong to several households and act in one at a time, and what they may do there is decided by their role through named policies.

## Users & roles
| Action | Anonymous | Signed in, not a member | Child | Adult | Owner |
|---|---|---|---|---|---|
| Create household | 401 | yes, becomes Owner | yes, becomes Owner of the new one | yes | yes |
| List my households | 401 | yes, empty list | own memberships only | same | same |
| Create invite for household H | 401 | 403 | 403 | 403 | yes |
| Accept invite | 401 | yes | yes, joins another household | yes | yes |
| Act in household H (any policy) | 401 | 403 | per policy | per policy | per policy |

Roles are hierarchical. Owner can do everything Adult can, and Adult can do everything Child can. Tenancy: a user never sees households, memberships or invites of households they are not a member of.

## Behaviour
**Contracts owned here:**
- `Foodplanner.Domain.Tenancy.HouseholdRole { Owner, Adult, Child }`.
- The `HouseholdRole? Role` member added to `ICurrentHousehold` (T-008 owns `HouseholdId`), and the real `ICurrentHousehold` implementation.
- Policies in `Foodplanner.Api.Authorization.Policies`:
  - `HouseholdMember`: any role.
  - `HouseholdAdult`: Owner or Adult.
  - `HouseholdOwner`: Owner.
- `IInviteRedemption.AcceptAsync(token, userId)`, used by this task's endpoint and by T-010.

**Contracts used from elsewhere:** `ICurrentUser`, the 401/403 shape and the problem `type` convention (T-009), and the tenancy filter and write guard (T-008).

- **Entities.**
  - `Household` (Name, 1-100 characters after trimming) is the tenant root.
  - `Membership` (HouseholdId, UserId, Role), unique on (HouseholdId, UserId).
  - `Invite` (HouseholdId, TokenHash, Role, ExpiresAt, CreatedByUserId, AcceptedAt, AcceptedByUserId).
  - `Membership` and `Invite` implement `ITenantEntity` (Q-006). `Household` is the tenant root with no `HouseholdId`; it is on T-012's explicit exemption list. This task's migration is `AddHouseholds`.
  - **`ITenantResolution`** (`Foodplanner.Infrastructure.Tenancy`, owned here) is the only code that may call `IgnoreQueryFilters` or open an `ITenantWriteScope` (T-008). It offers `GetMembershipsForUser(userId)`, `FindInviteByTokenHash(hash)`, `CreateHouseholdWithOwner(...)` and `AddMembershipFromInvite(...)`. Every filter bypass has an explicit `UserId` or token-hash predicate, and every write scope names its household explicitly. `ICurrentHousehold` and invite redemption use it.
- **Household context (Q-004).** Every household-scoped endpoint in the whole API lives under `/api/households/{householdId}/...`. An endpoint filter on that route group resolves the caller's membership once per request and sets `ICurrentHousehold` (`HouseholdId` and `Role`; both `null` if the caller is not a member of that household). Endpoints outside the group have no household context. "Active household" is purely a client choice; the server holds no selection. Role resolution lives only in `ICurrentHousehold`, so T-026 can narrow it to Child when a kid profile is active. This route convention is a contract owned by this task; later tasks follow it.
- **Policies.** One requirement type, `HouseholdRoleRequirement`, and one handler read `ICurrentHousehold.Role`. Endpoints attach policies with `RequireAuthorization(Policies.X)` on their group. No endpoint compares role strings or enums itself. Anonymous callers get 401. Callers without a membership in the household, or with too low a role, get 403 `urn:foodplanner:problem:forbidden`.
- **Endpoints** (`Features/Households/HouseholdEndpoints.cs`, `MapHouseholdEndpoints`; all unsafe methods under the antiforgery group from T-009):
  - `POST /api/households` with `{ name }`: returns 201 `{ id, name, role: "Owner" }` with a `Location` header. The household and the Owner membership are created in one transaction. An invalid name returns 400 `ValidationProblem`.
  - `GET /api/households`: returns 200 `[{ id, name, role }]` for the caller's memberships only. The list is empty if there are none.
  - `POST /api/households/{householdId}/invites` with `{ role }` (`HouseholdOwner`): returns 201 `{ token, expiresAt, role }`. The plain token appears only in this response. `role` must be `Adult` or `Child`; `Owner` or any other value returns 400 `ValidationProblem` and writes nothing (Q-005).
  - `POST /api/invites/{token}/accept` (authenticated): returns 200 `{ householdId, role }` and creates the membership.
- **Invite tokens.** A token is 32 cryptographically random bytes, base64url-encoded. Only its SHA-256 hash is stored. Expiry is configurable, default 7 days. Acceptance is atomic: it consumes the invite only if it is unaccepted and unexpired, and creates the membership in the same transaction.
  - An unknown, expired or already-used token returns 410 ProblemDetails `urn:foodplanner:problem:invite-invalid`, with the same body in every case.
  - A caller who is already a member returns 409 `urn:foodplanner:problem:already-member`, and the invite is not consumed.
- **Who may accept (Q-005).** Any signed-in user holding the token. The invite is not bound to an email. The accept endpoint uses the `invite-accept` rate-limit policy from T-009.

## Out of scope
Changing a member's role, listing or revoking invites, invite previews, removing members, leaving, deleting a household, ownership transfer (T-044), email delivery of invites, kid profiles and PIN (T-026), external login (T-010), UI (T-045), and the generic isolation harness (T-012). This task still adds its own isolation tests for its entities.

## Acceptance criteria (each must be testable)
- [ ] `Create_household_makes_caller_owner`: `POST /api/households` returns 201. `GET /api/households` then lists it with `role` = `Owner`.
- [ ] `Create_household_validation`: an empty or 101-character name returns 400 `ValidationProblem`. Anonymous returns 401.
- [ ] `Policy_matrix`: test-only endpoints protected by each policy, called in household H by Owner, Adult, Child, a non-member and anonymous, return:
  - `HouseholdMember`: 200/200/200/403/401
  - `HouseholdAdult`: 200/200/403/403/401
  - `HouseholdOwner`: 200/403/403/403/401

  All failures are ProblemDetails.
- [ ] `Only_owner_creates_invites`: an Adult or Child of H calling create-invite gets 403, and no invite row is written. The Owner of household A calling it for household B gets 403.
- [ ] `Invite_accept_joins_household`: a second user accepts with the token and gets 200. Their `GET /api/households` lists H with the invited role.
- [ ] `Used_invite_rejected`: accepting the same token again (as any user) returns 410 `invite-invalid`.
- [ ] `Expired_invite_rejected`: after advancing a fake `TimeProvider` past `expiresAt`, accept returns 410 `invite-invalid`, and no membership is created.
- [ ] `Unknown_invite_rejected`: a random token returns 410 with a body identical to the expired case.
- [ ] `Concurrent_accept_single_use`: two parallel accepts of one token by different users produce exactly one 200 and one 410.
- [ ] `Existing_member_accept_conflict`: a member accepting an invite to their own household gets 409, and the invite can still be used by someone else.
- [ ] `Token_stored_hashed`: the `Invite` row does not contain the plain token returned to the Owner.
- [ ] `Two_households_and_switch`: a user who is Owner of A and Adult of B sees both in `GET /api/households`. A `HouseholdOwner` test endpoint under `/api/households/A/...` returns 200, and the same endpoint under `/api/households/B/...` returns 403. A GUID that is no household returns 403, never 200.
- [ ] `Owner_role_invite_rejected`: creating an invite with `role` = `Owner` returns 400 `ValidationProblem`, and no invite row exists.
- [ ] `Household_endpoints_under_route`: a test enumerating `EndpointDataSource` finds every endpoint that requires a household policy under `/api/households/{householdId}`.
- [ ] `Invite_accept_rate_limited`: with a low configured limit, repeated accept calls from one client return 429 `rate-limited`.
- [ ] `Households_isolated`: a user in A only never sees B in `GET /api/households`, and with context A no query returns B's memberships or invites. 
- [ ] `Tenant_resolution_is_the_only_bypass`: a test lists every `IgnoreQueryFilters` call and every `ITenantWriteScope.ForHousehold` call in the solution and finds them only inside `ITenantResolution`'s implementation (the enforcing architecture test is delivered by T-012).
- [ ] Architecture test `No_role_checks_outside_policies`: no type under `Foodplanner.Api.Features` reads `ICurrentHousehold.Role` or calls `IsInRole`. Only the authorization handler and `ICurrentHousehold` do.
- [ ] The `AddHouseholds` migration applies on a fresh database, and `No_pending_model_changes` passes.

## Open questions
- Answered: [Q-004](../questions/Q-004.md) (route segment), [Q-005](../questions/Q-005.md) (bearer link, Adult or Child only), [Q-003](../questions/Q-003.md) (invite accept is rate limited).
- Answered: [Q-006](../questions/Q-006.md), option 1 (tenant entities, one sanctioned bypass through `ITenantResolution`).
