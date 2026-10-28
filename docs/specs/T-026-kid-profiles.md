# Spec: Kid profiles and profile switching (T-026)
## Goal
A parent signed in on a shared tablet or phone can let a child use the app as their own named profile (no password, no email). The child looks at big pictures of dishes and taps the ones they like; the parents see those likes when they plan the week. While a kid profile is active the device cannot do anything an adult is needed for. The parent gets back to their own session with a PIN, and a child cannot guess it by trying repeatedly.

**Architect decisions (2026-10-28), recorded here and not open:**
- Kid profiles are for children of about 4 to 8 (kindergarten and early elementary school); many cannot read yet. The kid UI is image-first: big recipe photos, large touch targets, icons instead of words, at most a very short label. Cooking and prep time are removed, and so are most other texts and metadata (tags, servings, ingredient lists, steps, source). Spoken text (TTS) is a possible later idea and is out of scope.
- "They should pick food they like": the kid's core action is marking dishes they like, a personal list of likes per kid profile. It is not voting on the week's plan; adults plan the week and see and use the likes (a "liked by" badge on recipes and a filter for recipes a given kid likes). A like is one tap with clear feedback (big heart); tapping again removes it.

Parts that depend on the open question [Q-020](../questions/Q-020.md) are marked "(Q-020 n)". The spec assumes the recommendation of each point; if the architect answers differently those parts change, the rest stands.

## Users & roles
A "kid profile" is not a user and not a membership. It is a named, credential-less person in a household that an already signed-in account can act as. "Kid mode" is the state of one device's session while a profile is active.

| Action | Anonymous | Not a member | Child member | Adult | Owner | Any role, in kid mode |
|---|---|---|---|---|---|---|
| List profiles of H (picker) | 401 | 403 | yes | yes | yes | yes |
| Create, rename, delete profile (Q-020 5) | 401 | 403 | 403 | yes | yes | 403 |
| Activate a profile (switch to a kid, or kid to kid) | 401 | 403 | 409 `pin-not-set` (a Child member has no PIN) | yes (PIN must be set) | yes (PIN must be set) | yes, no PIN (Q-020 5) |
| Set or change own PIN | 401 | n/a | yes, but see activate | yes | yes | 403 `kid-mode` |
| Unlock with PIN (kid mode to full session) | 401 | n/a | n/a | yes | yes | yes, with PIN |
| Everything an Adult can do (recipes, imports, own ingredients, tags, images, household export, ...) | 401 | 403 | 403 | yes | yes | 403 |
| Everything only an Owner can do (members, invites, delete household) | 401 | 403 | 403 | 403 | yes | 403 |
| Read recipes, meal plan, shopping list, pantry, export-free reads | 401 | 403 | yes | yes | yes | yes |
| Add a meal plan entry, check items off, add or remove manual list items, confirm pantry staples | 401 | 403 | yes | yes | yes | yes (as the profile) |
| Like or unlike a recipe (as the active profile) | 401 | 403 | 409 `no-active-profile` | 409 `no-active-profile` | 409 `no-active-profile` | yes, own likes only |
| See who liked a recipe, filter recipes by a kid's likes | 401 | 403 | yes | yes | yes | yes (the kid UI does not show it) |
| Change or delete a meal plan entry | 401 | 403 | own picks | any | any | only entries picked by the ACTIVE profile |
| Leave household, create household, accept invite | 401 | create/accept: yes | per role | per role | per role | 403 `kid-mode` |
| Sign out | 401 | yes | yes | yes | yes | yes (signing in again needs the password) |

In kid mode the effective role is Child in EVERY household of the account, not only the household of the profile (a child must not reach a household where the account is Owner by switching the household in the client). Kid mode is per device: the account's other devices are unaffected.

Tenancy: `MemberProfile` is a household-owned entity. A caller sees and changes only profiles of the household in the route, found through the tenant filter. Another household's profile id (in a route or in a body) never resolves. `UserPin` is a per-account secret and is global like the identity tables (no `HouseholdId`).

## Behaviour

### Data model (migration `AddMemberProfiles`; only migration in flight, rule 9)
- `MemberProfile` (`Domain/Households`, `ITenantEntity`, own Guid key, no navigations, `CreatedAt`/`UpdatedAt`): `DisplayName` (1 to 30 characters after trimming, unique per household, case-insensitive, `409 profile-exists`), `Avatar` (string key from a fixed set in `Domain`: `fox`, `bear`, `cat`, `dog`, `owl`, `frog`, `panda`, `rabbit`, `lion`, `penguin`, `koala`, `whale`; anything else is `400` on `avatar`). Kid profiles only: an adult acts as themselves, so there is no adult profile row. At most 10 profiles per household (`409 profile-limit`). Cascade only Household -> MemberProfile. No credentials, no email, no link to `AspNetUsers`.
- `RecipeLike` (`Domain/Households`, `ITenantEntity`, own Guid key, no navigations, `CreatedAt`/`UpdatedAt`): `MemberProfileId`, `RecipeId`; unique `(MemberProfileId, RecipeId)`. A like belongs to the household (`HouseholdId`), the profile and the recipe, and only ever links rows of that household. FKs to `MemberProfiles` and `Recipes` are NO ACTION and the household FK too; the only cascade is Household -> RecipeLike, because Household -> Recipe and Household -> MemberProfile already exist. Deleting a profile first deletes its likes (tracked removes) and deleting a recipe first deletes its likes, each in the same transaction as the parent delete (`recipe-in-use` 409 for plan entries is unchanged). Both foreign keys are covered by the save-time reference check (T-073). Only profiles have likes: an adult account cannot like (`409 no-active-profile`).
- `MealPlanEntry.PickedByProfileId` (`Guid?`, FK to `MemberProfiles`, NO ACTION, because Household -> MealPlan -> Entry already cascades and SQL Server rejects a second path). `PickedByUserId` stays required and is always the signed-in account. Deleting a profile first sets `PickedByProfileId` to null on its entries by tracked updates (no `ExecuteUpdate`), then deletes the profile, in one transaction; the history then shows only the account holder. The save-time reference check (T-073) covers the new foreign key from the EF model.
- `UserPin` (global, table `UserPins`, on `TenantCoverage.Exempt` with the reason "per-account secret, like the identity tables"): `UserId` (key, FK to `AspNetUsers`, cascade), `Hash` (the ASP.NET Identity password hasher, PBKDF2 with per-PIN salt and the hasher's format version; the plain PIN is never stored or logged), `FailedCount`, `LockedUntil`, `RowVersion`, `CreatedAt`, `UpdatedAt`. One PIN per account (Q-020 1).
- Registered in the isolation harness: `TenantIsolationCase.For<MemberProfile>(...)` and `For<RecipeLike>(...)` in `TenantIsolationRegistry.cs`; `TenantIsolationCoverageTests` must pass. The household delete and export tests are model-derived, so profiles are deleted with the household and exported (profile id, name, avatar) with no extra code.

### PIN (Q-020 1, 2)
- Format: 4 to 8 digits, nothing else (`400` on `pin`). Refused as too weak (`400` on `pin`): all digits equal ("0000") and straight ascending or descending runs ("1234", "9876").
- `PUT /api/auth/pin` `{ pin, currentPin?, password? }` (policy `FullSession`, antiforgery like all unsafe endpoints; `204`). Setting a first PIN needs nothing more than the full session. Replacing an existing PIN needs `currentPin`, or `password` for an account that has one; a wrong value is handled exactly like a wrong unlock attempt (counted, can lock). Setting a PIN resets the counters.
- `POST /api/auth/pin/unlock` `{ pin }` (authenticated, allowed in kid mode, rate-limit policy `pin-unlock`; `204`):
  1. The session is not in kid mode: `409 not-in-kid-mode`; nothing is hashed or counted (no PIN oracle).
  2. The account has no PIN: `409 pin-not-set`.
  3. `LockedUntil` is in the future: `429 pin-locked` with `Retry-After`; the PIN is not even checked, even if it is right.
  4. Wrong PIN: `FailedCount` + 1 and `403 pin-incorrect` with an extension member `attemptsRemaining`. The 5th consecutive failure sets `LockedUntil = now + 15 minutes` and resets `FailedCount` (the response is `429 pin-locked`).
  5. Right PIN: `FailedCount = 0`, `LockedUntil = null`, the auth cookie is re-issued WITHOUT the kid claims, and the account's live hub connections are closed (below).
- The counter is server-side and per account, not per device or cookie, so clearing cookies, opening a second tab or using another device does not reset it. Updates use the row version with a bounded retry, so parallel guesses cannot all be evaluated against a stale counter.
- Two layers of rate limiting (T-003/T-047 style, `RateLimitingSetup`): (a) policy `pin-unlock` on the unlock and PIN-change endpoints, partitioned by client IP and user, default 10 per 60 seconds (`RateLimiting:PinUnlock`), `429 rate-limited` with `Retry-After`; (b) the account lockout above. Limits are configuration (`Auth:PinMaxFailedAttempts` = 5, `Auth:PinLockoutMinutes` = 15).
- The only way out of a locked state is the lockout expiring, or a password sign-in (which issues a fresh cookie with no kid claims). Reset of a forgotten PIN for an account without a password is not provided (see Out of scope).

### How a switch works on a shared device (Q-020 3)
- Source of truth is the auth cookie `__Host-foodplanner` itself (same cookie, same flags). Kid mode adds two claims to the principal: `fp:profile` (profile id) and `fp:profile-household` (household id). No extra cookie, no server session table. Deleting the cookie signs the device out; it cannot return the device to the adult session.
- `POST /api/households/{h}/profiles/{profileId}/activate` (`HouseholdMember`, `204`): resolves the profile through the household's filtered set (`404 profile-not-found` for an unknown or other household's id); an account NOT in kid mode needs a PIN (`409 pin-not-set`); then re-issues the auth cookie (`SignInWithClaimsAsync` style, same security stamp, same expiry window) with the claims for that profile. In kid mode the call is allowed without a PIN and only replaces the profile claims (kid to kid). `fp:profile-household` is always the route household.
- The claims are written by the server only. A client cannot set them; the cookie is protected by Data Protection like today.
- Identity refresh must keep the lock. `SecurityStampValidator` rebuilds the principal from the claims factory on its validation interval, which would silently drop custom claims and unlock the device. The cookie `OnValidatePrincipal` therefore copies the two kid claims from the old principal into the replacement. A stale claim whose profile no longer exists (deleted on another device) keeps the device locked as Child with no active profile (the client shows the picker); it never unlocks it.
- Role narrowing (Q-004, T-011 "so T-026 can narrow it"): `HouseholdContextMiddleware` and `HouseholdMembershipGuard` (SignalR) both go through one `EffectiveRole` function in the household resolution code: `min(membership role, Child)` when the principal has the kid claims, otherwise the membership role. Policies (`HouseholdMember/Adult/Owner`) are unchanged and remain the only readers of `Role`, so every existing and future Adult or Owner endpoint is closed to kid mode with no per-endpoint code (rule 2). `ICurrentUser` gains `ActiveProfileId` and `IsKidMode` (feature code still never touches `ClaimsPrincipal`).
- Policy `FullSession` (`Policies.FullSession`, a requirement satisfied only when the principal has no kid claims) is attached to the account-level actions that are not under a role policy or that are destructive for the account: `POST /api/households`, `POST /api/invites/{token}/accept`, `POST /api/households/{h}/leave`, `PUT /api/auth/pin`. Failure is `403` ProblemDetails `urn:foodplanner:problem:kid-mode`. Sign out, `GET /api/households`, `GET /api/auth/me` and the unlock endpoint stay open. `GET /api/households` and the household listing report the EFFECTIVE role, and `GET /api/auth/me` gains `kidMode: { profileId, householdId } | null`, so the client can hide adult actions; the server remains the only authority.
- Architecture test: every endpoint is either under `/api/households/{householdId}` (so it has a household policy), or has `FullSession`, or is on a short explicit allow-list (auth, health, hub negotiate, unlock, `GET /households`).
- SignalR identity: the hub reads the same cookie, but a connection keeps the principal it had at connect. Activate and unlock therefore call `ILiveConnections.CloseUserAsync` (T-044) after the cookie is re-issued; the client's automatic reconnect and its `JoinHousehold`/`GetState` (T-034 contract) then run with the new identity. All hub methods are `HouseholdMember`-level and role-independent, so even before the reconnect nothing is exposed; this makes the identity consistent rather than being the security boundary. Side effect, as with logout: the account's connections on other devices also reconnect.

### What a kid profile can do
- Everything under `HouseholdMember` stays available: browse recipes (list, detail, scaled, images), read and add meal plan entries, read the shopping list and sync it, check items off, add and remove manual items, generate the list, read and change pantry confirmations, read tags and catalog, `GET .../profiles`. No new rule is needed for these; they keep the T-030, T-033, T-037 permissions.
- Recipe or ingredient edits, imports, image uploads, tag creation, household export, member and invite management, leaving or deleting a household are closed (policy `HouseholdAdult`/`HouseholdOwner`/`FullSession`).
- Likes (the kid's core action, see Likes below). Server-side, kid mode can still add meal plan entries (T-030, the Child role keeps "pick meals"), but the kid UI does not offer it; planning is the adults' job. There is no voting feature.
- The kid UI hides cooking and prep time, tags, servings, ingredient lists, steps and source. This is presentation only: the API responses are unchanged and a kid-mode session can still call the member-level reads (nothing in them is secret from a household member).

### Likes (`/api/households/{h}/recipes/{r}/like` and reads)
- `PUT .../recipes/{r}/like` (`HouseholdMember`; needs an active profile whose household is the route household, else `409 no-active-profile`): idempotent like for the ACTIVE profile (taken from the claim, never from the body), `204`. `DELETE .../recipes/{r}/like`: removes it, `204`, also when there is none (a second tap is "unlike"; the client sends PUT or DELETE from the heart's state). The recipe is resolved through the filtered set: unknown or other household's id `404 recipe-not-found`. Concurrent double PUT leaves one row (unique index; the loser's conflict is treated as success).
- Reads for adults and the picker: recipe list items and detail gain `likedBy: [{ id, displayName, avatar }]` (profiles of this household, ordered by name) and `liked: bool` (is it liked by the active profile; false outside kid mode). `GET .../recipes` accepts `likedByProfileId` (repeat, at most 10: liked by ALL of them, ANDed with the other filters); an unknown or foreign profile id matches nothing, a malformed one is `400` on `likedByProfileId` (the T-023 filter rule). `GET .../profiles` stays as is.
- Which recipes the kid sees (Q-020 6): all of them, the UI lists the ones tagged Barnevennlig first (existing `tagIds` filter plus ordering in the client). No server rule.

### Attribution and the meal plan entry rule (Q-020 4; likes always belong to the profile)
- `POST .../meal-plans/{w}/entries` in kid mode sets `PickedByProfileId` from the claim, never from the body, and only when `fp:profile-household` equals the route household and the profile still exists in it; otherwise it stays null. `PickedByUserId` is the account.
- Meal plan responses keep `pickedBy { userId, email }` and gain `pickedByProfile { id, displayName, avatar } | null`.
- Policy `MealPlanEntryOwnerOrAdult` is extended (policy code, the only reader of the role/claims): in kid mode it succeeds only when `entry.PickedByProfileId` equals the active profile (so siblings sharing one account cannot edit each other's picks, and a kid cannot touch an entry the parent made); outside kid mode it is unchanged (Adult/Owner, or the user who picked it).
- Check-offs and pantry confirmations are not attributed to the profile; they keep the account (Q-020 4).

### Profile management endpoints (`/api/households/{h}/profiles`, `MapProfileEndpoints`)
- `GET` (`HouseholdMember`): `[{ id, displayName, avatar }]` ordered by display name (nb collation), possibly empty.
- `POST` (`HouseholdAdult`) `{ displayName, avatar }`: `201` with Location. `PUT /{id}` (`HouseholdAdult`): rename or change avatar, same rules, `200`. `DELETE /{id}` (`HouseholdAdult`): `204`, also when it is the active profile on some device (see stale claim above). Unknown or other household's id `404 profile-not-found`. Concurrency: `DbUpdateConcurrencyException` is `409 profile-conflict`. Validation errors are ValidationProblem on `displayName`/`avatar`.

### UI sketch (informative; built by a frontend task, not by T-026; 390px wide, touch targets at least 44px, all text through `t()` nb + en)
```
Shell, adult session                 Profile picker (opened from the shell)
+--------------------------+         +--------------------------+
| Hjem            [ avatar ]|         |  Hvem er du?             |
|  ...                      |         |  +------+ +------+       |
| [ Bytt profil ]           |  --->   |  | fox  | | owl  |  big  |
+--------------------------+         |  | Mia  | | Ola  | 96px   |
                                      |  +------+ +------+ tiles |
Kid mode shell (image first)          |  [ + Ny profil ] (adults)|
+--------------------------+         +--------------------------+
| [fox]               [ key]|         PIN sheet (after the key icon)
| +----------------------+ |         |  o o o o                 |
| |                      | |         |  [1][2][3] [4][5][6] ... |
| |   big dish photo     | |         |  "Feil PIN, 3 forsok igjen"|
| |                 [<3] | |         |  "Last i 14 min"          |
| +----------------------+ |         +--------------------------+
| |   next dish photo    | |
+--------------------------+
```
Kid mode: a vertical feed of large dish photos (full width, no time, tags, servings, ingredients, steps or source), Barnevennlig dishes first. Each photo has a heart of at least 64px in the corner: one tap fills it (liked, with a short pop animation), tapping again empties it (unlike). No text except an optional one-word dish title under the photo; no menu for recipes, plan or settings, only the profile avatar (opens the picker) and a key icon (opens the PIN sheet). The shopping list is reachable by an icon, with product pictures where the catalog has them; checking off is a big tap target. Adult view of the same data: a "liked by" row of small avatars on the recipe card and a "liked by" filter chip per kid in the recipe list.

The picker works with one tap per tile. The PIN sheet is a numeric keypad with 48px keys; on `pin-locked` it shows the remaining time from `Retry-After` and a link to "Logg inn med passord". An adult with no PIN is asked to set one before the first "Bytt profil". Adult-only menu entries are hidden from `kidMode` / effective role, never relied on.

## Out of scope
Anything not in the T-026 goal: voting on the week's plan and kids suggesting a dish for a specific day (Q-020 7, not in base scope); spoken text (TTS) or any audio in the kid UI; reading level or age-based content rules; attributing check-offs and pantry confirmations to a profile; per-profile preferences, allergies or age limits; profile photos (avatars are a fixed set); the web screens (picker, PIN sheet, kid shell) and the frontend task that builds them; removing a PIN; a recovery flow for a forgotten PIN on accounts without a password (a password sign-in still resets the session, and a replacement PIN needs the old PIN or the account password); idle auto-lock of kid mode back to the picker; per-store or device-bound lockout; changing the T-044/T-048 member management.

## Acceptance criteria (each must be testable)
- [ ] `Kid_mode_blocks_every_adult_and_owner_endpoint`: a test enumerates `EndpointDataSource`, takes every endpoint carrying `HouseholdAdult` or `HouseholdOwner` (or `FullSession`) and calls it as an Owner of H in kid mode (urls built from the route template with H and random ids). Each returns `403` ProblemDetails (`forbidden`, or `kid-mode` for `FullSession`) and nothing is written. The same list called by the same Owner without kid mode does not return `403` for the policy reason (not tied to a body being valid).
- [ ] `Child_profile_cannot_call_adult_only_endpoints` (T-026 criterion 1): explicit calls in kid mode of `POST /recipes`, `PUT /recipes/{id}/image`, `POST /imports`, `PUT /ingredients/{id}`, `POST /tags`, `GET /export`, `POST /invites`, `PUT /members/{u}/role`, `DELETE /households/{h}`, `POST /households/{h}/leave`, `POST /api/households` all return `403`.
- [ ] `Kid_mode_can_use_member_endpoints`: in kid mode `GET /recipes`, `GET /meal-plans/{w}`, `POST /meal-plans/{w}/entries`, `GET /shopping-lists/{w}`, `PUT /shopping-lists/{w}/items/{id}/check`, `POST /shopping-lists/{w}/items`, `GET /pantry`, `GET /profiles` return success.
- [ ] `Effective_role_is_child_in_every_household`: an account that is Owner of A and Adult of B, in kid mode with a profile of A, gets `403` on a `HouseholdAdult` endpoint under B and under A, and `GET /api/households` lists both with `role` = `Child`; `GET /api/auth/me` returns `kidMode { profileId, householdId }`.
- [ ] `Kid_mode_survives_cookie_refresh`: after switching to a profile, advancing the fake `TimeProvider` past the security stamp validation interval and making a request (the principal is rebuilt), `GET /api/auth/me` still shows kid mode and an Adult endpoint is still `403`.
- [ ] `Kid_claims_cannot_be_forged`: a request with a tampered or foreign-signed cookie is `401`; a request carrying a kid-claim-free cookie but a forged `X-Profile`-like header or body field cannot enter or leave kid mode (no such input exists: activate and unlock are the only transitions).
- [ ] `Activate_requires_pin_set`: an Adult with no PIN calling activate gets `409 pin-not-set` and the cookie is unchanged; after `PUT /api/auth/pin` the same call returns `204` and `Set-Cookie` for `__Host-foodplanner`.
- [ ] `Kid_to_kid_switch_needs_no_pin`: in kid mode as profile 1, activating profile 2 returns `204`, `/me` shows profile 2, and the device is still locked.
- [ ] `Unknown_or_foreign_profile_is_404`: activate, `PUT` and `DELETE` with a profile id of household B under household A return `404 profile-not-found`; `CrossHouseholdLinks.AssertNoneAsync` passes.
- [ ] `Unlock_with_correct_pin`: unlock returns `204`, `Set-Cookie` without kid claims, `/me` shows `kidMode` null, and an Adult endpoint returns non-403; `UserPin.FailedCount` is 0.
- [ ] `Unlock_wrong_pin_counts_and_locks`: with `Auth:PinMaxFailedAttempts` = 5, four wrong PINs return `403 pin-incorrect` with `attemptsRemaining` 4, 3, 2, 1; the fifth returns `429 pin-locked` with `Retry-After`; a sixth attempt with the CORRECT PIN is still `429 pin-locked`, the device stays in kid mode, and the hasher was not called for it (spy hasher).
- [ ] `Pin_lockout_expires`: after advancing the fake clock 15 minutes the correct PIN unlocks (`204`); a wrong one earlier would have been counted from 0.
- [ ] `Pin_attempts_rate_limited` (T-026 criterion 2): with `RateLimiting:PinUnlock` set to 3 per minute and a high lockout threshold, the 4th unlock attempt within the window from one client returns `429 rate-limited` with `Retry-After`, and attempts are counted per client IP and user (a different user is not limited).
- [ ] `Pin_lockout_is_per_account_not_per_cookie`: failed attempts spread over two sessions (two cookies) of one account add up to one lockout; deleting and re-creating the cookie of a locked device (new password sign-in) does not reset `FailedCount`.
- [ ] `Parallel_wrong_pins_cannot_beat_the_lockout`: 30 parallel wrong unlock requests with threshold 5 never yield more than 5 evaluated hashes before `pin-locked`, and `UserPin.LockedUntil` is set.
- [ ] `Unlock_when_not_in_kid_mode_is_409`: a full session calling unlock gets `409 not-in-kid-mode`, and `FailedCount` is unchanged.
- [ ] `Pin_validation`: `pin` of 3 or 9 digits, non-digits, "0000", "1234" and "9876" return `400` ValidationProblem on `pin`; "4827" returns `204`.
- [ ] `Pin_change_requires_current_pin`: replacing an existing PIN without `currentPin`/`password` is `400`; with a wrong `currentPin` it is `403 pin-incorrect` and counts toward the lockout; with the right one it is `204` and the old PIN no longer unlocks.
- [ ] `Pin_stored_hashed`: the `UserPins.Hash` value does not contain the plain PIN, two accounts with the same PIN have different hashes, and no log entry captured during the tests contains the PIN.
- [ ] `Pin_endpoints_blocked_in_kid_mode`: `PUT /api/auth/pin` in kid mode returns `403 kid-mode`.
- [ ] `Profile_crud_roles`: Owner and Adult create (`201`), rename (`200`) and delete (`204`) profiles; a Child member, a kid-mode session and a non-member get `403`, and the list endpoint is `200` for Child members and kid mode.
- [ ] `Profile_validation`: empty or 31-character name, unknown avatar, duplicate name differing only by case (`409 profile-exists`) and an 11th profile (`409 profile-limit`) are rejected with ProblemDetails; nothing is written.
- [ ] `Kid_can_like_and_unlike`: in kid mode as profile 1, `PUT .../recipes/{r}/like` returns `204` and creates one `RecipeLike` (household, profile 1, recipe); a second `PUT` still leaves one row; the recipe detail has `liked` true and `likedBy` containing profile 1; `DELETE` removes it (`204`), and a second `DELETE` is also `204`. A body or query `profileId` is ignored; two parallel `PUT`s give one row and no `500`.
- [ ] `Like_needs_active_profile`: an Adult or Owner outside kid mode, and a real Child member outside kid mode, calling `PUT .../like` get `409 no-active-profile` and nothing is written; a stale claim (profile deleted) also gets `409`.
- [ ] `Likes_are_per_profile`: profile 1 and profile 2 on one account like different recipes; each sees only own `liked`; `likedBy` lists both for a shared recipe; unliking as profile 1 does not remove profile 2's like.
- [ ] `Likes_visible_to_adults_and_filterable`: an Adult sees `likedBy` on list items and detail; `GET .../recipes?likedByProfileId=P1` returns exactly P1's liked recipes, `likedByProfileId=P1&likedByProfileId=P2` only the intersection, an unknown or other household's profile id returns an empty page, and a malformed id is `400` on `likedByProfileId`.
- [ ] `Likes_cleaned_up_with_parents`: deleting a profile removes its likes and keeps other profiles' likes; deleting a recipe (not on a plan) removes its likes; both `204`. A recipe on a plan is still `409 recipe-in-use`.
- [ ] `Likes_isolated_between_households`: tenant isolation suite passes for `RecipeLike` (registered with `TenantIsolationCase.For<RecipeLike>`); liking a recipe of household B under household A returns `404 recipe-not-found` and writes nothing; a like row whose profile or recipe belongs to another household is refused by the save-time check (a `SaveTimeReferenceCheckTests` row each for `MemberProfileId` and `RecipeId`); `CrossHouseholdLinks.AssertNoneAsync` passes; household delete removes the household's likes and export lists them.
- [ ] `Kid_pick_is_attributed_to_profile`: a kid-mode `POST .../entries` creates an entry with `PickedByProfileId` = active profile and `PickedByUserId` = account; the GET response has `pickedByProfile`. A body field `pickedByProfileId` is ignored.
- [ ] `Kid_can_change_only_own_profile_picks`: with profile 1 active, `PUT`/`DELETE` of an entry picked by profile 1 succeeds; of an entry picked by profile 2, or by the parent outside kid mode, returns `403`. Outside kid mode a real Child member still changes only the entries they picked, and an Adult any entry.
- [ ] `Deleting_profile_keeps_history`: deleting a profile that has picked entries returns `204`, the entries remain with `pickedByProfile` null and the account as picker, and no `ExecuteUpdate`/`ExecuteDelete` is used (existing architecture rule passes).
- [ ] `Stale_profile_claim_stays_locked`: a device in kid mode whose profile is deleted from another session still gets `403` on Adult endpoints, `/me` shows kid mode with the profile gone, and its new entries have `PickedByProfileId` null.
- [ ] `Activate_and_unlock_close_live_connections`: with a fake `ILiveConnections`, activate and unlock each call `CloseUserAsync` for the account once, after the cookie is re-issued; a hub connection opened with the kid cookie gets `forbidden` from `JoinHousehold` of a household the account is not a member of and works for its own.
- [ ] `Profiles_isolated_between_households`: tenant isolation suite for `MemberProfile` passes (reads, insert for another household, update, delete, `HouseholdId` change, `ExecuteUpdate`/`ExecuteDelete`); `TenantIsolationCoverageTests` passes with `UserPin` on `Exempt` with a reason; a household's profile list never contains another household's profile.
- [ ] `Household_delete_and_export_include_profiles`: the existing model-derived household delete test deletes the profiles of A and leaves B's; the export lists the household's profiles.
- [ ] `Account_endpoints_require_full_session_or_are_allow_listed`: the architecture test over `EndpointDataSource` described above is green and fails when an unprotected account-level endpoint is added.
- [ ] Policies are the only mapping: architecture test `No_role_checks_outside_policies` still passes (no feature code reads `ICurrentHousehold.Role` or the kid claims); the kid claims are read only in `EffectiveRole`, the `FullSession` handler and the meal plan entry handler.
- [ ] The `AddMemberProfiles` migration applies on a fresh database and on one with meal plan entries (existing entries get `PickedByProfileId` null); `No_pending_model_changes` passes; `./scripts/verify.sh` is green; the OpenAPI document contains the new endpoints and the generated TS client is regenerated, not hand-edited.
- [ ] `docs/domain.md` is updated (profiles, kid mode, PIN, `FullSession`, effective role) in the same branch.

## Open questions
- [Q-020](../questions/Q-020.md): PIN ownership (1), PIN format and lockout numbers (2), where the kid-mode lock lives (3), what else is attributed to a profile besides likes (4), who manages profiles and which switches need the PIN (5), which recipes a kid sees (6, Barnevennlig first vs only Barnevennlig), whether kids may suggest a dish for a day (7, optional, not in base scope). Open; spec assumes the recommendations.
