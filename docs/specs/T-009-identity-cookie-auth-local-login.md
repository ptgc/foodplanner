# Spec: Identity, cookie auth, local login (T-009)
## Goal
A person can create a local account with email and password, sign in, stay signed in on their device through a secure first-party cookie, and sign out. Feature code learns who is calling only through `ICurrentUser`.

## Users & roles
- Anyone (anonymous) can call antiforgery, register and login. Every other endpoint requires a signed-in user.
- Household roles (Owner, Adult, Child) do not exist yet (T-011), and nothing in this task checks them. A user who will later hold a Child membership signs in exactly like anyone else. Credential-less kid profiles (T-026) cannot sign in.
- Tenancy: a User is a global identity, not an `ITenantEntity`, and has no `HouseholdId`. No household data is read or written. `GET /api/auth/me` only ever returns the caller's own user.

## Behaviour
**Contracts owned here:**
- `Foodplanner.Domain.Users.ICurrentUser` with `bool IsAuthenticated`, `Guid? UserId` and `string? Email`.
- `AppUser : IdentityUser<Guid>` (Infrastructure). User ids are `Guid` everywhere.
- The cookie and antiforgery contract.
- The fallback authorization policy: authenticated user required.
- The 401 and 403 response shape.
- The ProblemDetails `type` convention: application errors use `type` = `urn:foodplanner:problem:<code>`. Validation errors use the standard `ValidationProblem` shape.

**Contracts used from elsewhere:** `AppDbContext` (T-008), and the layout and `MapXxxEndpoints` convention (T-002). The 500 exception mapping and trace id come from T-013.

- **Identity.** `AppDbContext` derives from `IdentityUserContext<AppUser, Guid>`. Identity's global role tables are not used, because roles are per membership (T-011). The migration `AddIdentity` adds the user tables. Password rules: minimum length 10, no character-class rules. Lockout is on for all users: 5 failed attempts lock the account for 15 minutes. Both values are configurable.
- **Endpoints** (`Features/Auth/AuthEndpoints.cs`, `MapAuthEndpoints`, under `/api/auth`):
  - `GET antiforgery` (anonymous): returns 200 `{ "token": "..." }` and sets the antiforgery cookie.
  - `POST register` (anonymous): body `{ email, password }`. An invalid email or password returns 400 `ValidationProblem`. Register never signs the user in. It always returns 202 with an identical status and body for a new and an existing email, so it does not reveal who has an account. A new address gets a confirmation email. An existing address gets an "you already have an account" email with no token.
  - `POST confirm-email` (anonymous): body `{ userId, token }`. A valid token returns 204 and confirms the email. An invalid, expired (default 24 hours, configurable) or already-used token returns 400 ProblemDetails, `type` `urn:foodplanner:problem:confirmation-invalid`.
  - `POST login` (anonymous): body `{ email, password }`. Success returns 204 and sets the auth cookie. Failure returns 401 ProblemDetails, `type` `urn:foodplanner:problem:login-failed`, with the same `title` and `detail` for an unknown email, a wrong password, a locked-out account and an unconfirmed email. For an unknown email the server still runs one password-hash verification against a fixed dummy hash, to reduce timing differences.
  - `POST logout` (authenticated): returns 204 and expires the auth cookie. Cookies are stateless, so other devices stay signed in until their cookies expire.
  - `GET me` (authenticated): returns 200 `{ userId, email }`.
- **Auth cookie.** Name `__Host-foodplanner`, `HttpOnly`, `Secure` (always), `SameSite=Lax`, `Path=/`, no `Domain`. It is persistent with 14-day sliding expiry (configurable). The API never redirects for auth. A missing or invalid cookie returns 401 ProblemDetails (`type` `urn:foodplanner:problem:unauthenticated`). A forbidden request returns 403 ProblemDetails (`type` `urn:foodplanner:problem:forbidden`). `AddProblemDetails()` is registered here.
- **Antiforgery.** The cookie `__Host-foodplanner-xsrf` is `HttpOnly`, `Secure` and `SameSite=Strict`. The request token is sent in header `X-XSRF-TOKEN`. All endpoints hang off one root group `/api`. An endpoint filter on that group validates every POST, PUT, PATCH and DELETE, including login and register (login CSRF). A missing or invalid token returns 400 ProblemDetails (`type` `urn:foodplanner:problem:antiforgery-invalid`). Tokens are bound to the signed-in identity, so the client fetches a new token after login and after logout.
- **Email (Q-002).** This task owns the `IEmailSender.SendAsync(to, subject, textBody, ct)` port in `Foodplanner.Infrastructure.Email` and a `RecordingEmailSender` that keeps messages in memory for tests and Development. Confirmation links are never written to logs. The production provider is T-017; until it lands, deployed environments must set `Email:Provider=Recording` explicitly, otherwise startup fails with a message naming the setting.
- **Fallback policy.** Every endpoint requires an authenticated user unless explicitly `AllowAnonymous`. The anonymous allow-list is: health, antiforgery, register, login, and confirm-email.
- **`ICurrentUser`.** The Api implementation reads the authenticated principal. Feature handlers outside `Features/Auth` never touch `ClaimsPrincipal`, `HttpContext.User`, `UserManager<>` or `SignInManager<>`.
- **Tests** use `WebApplicationFactory` with base address `https://localhost`, so `Secure` cookies flow.

## Out of scope
External providers (T-010), households, roles and policies beyond "authenticated" (T-011), passkeys (T-058), password reset and change, Data Protection key persistence for deployed environments (not assigned, see the task report), and UI. Rate limiting is pending Q-003. The production email provider is T-017 (provider choice: Q-007); this task adds only the port and the recording sender.

## Acceptance criteria (each must be testable)
- [ ] `Login_logout_round_trip`: for an existing usable user, login returns 204 with a `Set-Cookie` for `__Host-foodplanner`. `GET /api/auth/me` then returns 200 with that user's id and email. Logout returns 204, and `GET /api/auth/me` then returns 401 ProblemDetails.
- [ ] `Auth_cookie_flags`: the login `Set-Cookie` header contains `HttpOnly`, `Secure`, `SameSite=Lax` and `Path=/`, and has no `Domain` attribute.
- [ ] `Antiforgery_cookie_flags`: the antiforgery `Set-Cookie` contains `HttpOnly`, `Secure` and `SameSite=Strict`.
- [ ] `Login_failures_are_indistinguishable`: unknown email, wrong password and locked-out account all return 401 with identical `type`, `title` and `detail`.
- [ ] `Unknown_email_still_hashes`: a spy `IPasswordHasher<AppUser>` records one verification call for a login with an unknown email.
- [ ] `Lockout_after_5_failures`: after 5 wrong passwords, the correct password returns 401 `login-failed`. After advancing a fake `TimeProvider` 15 minutes, it succeeds.
- [ ] `Unsafe_requests_need_antiforgery`: login, register and logout without `X-XSRF-TOKEN`, or with a token from another session, return 400 ProblemDetails `antiforgery-invalid`.
- [ ] `All_unsafe_endpoints_validate_antiforgery`: a test enumerating `EndpointDataSource` finds no POST, PUT, PATCH or DELETE endpoint outside the antiforgery-validating `/api` root group.
- [ ] `Anonymous_endpoints_match_allow_list`: enumerating endpoints, the set allowing anonymous access equals the documented allow-list exactly.
- [ ] `Protected_endpoint_returns_401_problem_not_redirect`: `GET /api/auth/me` without a cookie returns 401 with `Content-Type: application/problem+json` and no `Location` header.
- [ ] `Register_validation`: an invalid email, or a password shorter than 10 characters, returns 400 `ValidationProblem` naming the field.
- [ ] `Register_is_uniform`: register for a new email and for an existing email returns 202 with identical status and body, and sets no auth cookie.
- [ ] `Register_sends_confirmation_or_notice`: the recording sender holds one confirmation mail with a token for a new email, and one "already registered" mail with no token for an existing confirmed email.
- [ ] `Confirm_email_flow`: a valid token returns 204 and a following login succeeds. An invalid token, an expired token (fake `TimeProvider`) and a reused token each return 400 `confirmation-invalid`.
- [ ] `Unconfirmed_login_refused`: a correct password with an unconfirmed email returns 401 `login-failed`, with a body identical to a wrong password.
- [ ] `Missing_email_provider_fails_startup`: a non-Development host without `Email:Provider` throws at startup with a message naming the setting.
- [ ] Architecture test `Handlers_use_ICurrentUser_only`: types under `Foodplanner.Api.Features` (except `Features.Auth`) have no dependency on `ClaimsPrincipal`, `UserManager<>` or `SignInManager<>`.
- [ ] The `AddIdentity` migration applies on a fresh database, and `No_pending_model_changes` (T-008) still passes.

## Open questions
- Answered: [Q-002](../questions/Q-002.md), option 1 (confirm email in this task). Provider choice for production is [Q-007](../questions/Q-007.md), which blocks T-017 only.
- [Q-003](../questions/Q-003.md): app-level rate limiting on auth endpoints now or in T-047.
