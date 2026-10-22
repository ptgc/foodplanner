# Spec: Google external login with invite round trip (T-010)
## Goal
A person can sign in with Google. If they arrived through a household invite link, they land in that household after the Google round trip. An existing account is linked only when the provider vouches for the email, and further OIDC providers are added by configuration alone.

## Users & roles
- Anyone (anonymous) can list providers and start an external sign-in.
- No role check applies to signing in. A new user with no invite has no household and can create one (T-011). A user who completes an invite gets exactly the role stored on the invite (Owner, Adult or Child, as limited by Q-005). Credential-less kid profiles (T-026) never use external login.
- Tenancy: users and external-login links are global, not `ITenantEntity`. The only household data written is the membership from invite acceptance, and only in the invite's household, through `IInviteRedemption` (T-011).

## Behaviour
**Contracts owned here:** the external provider configuration shape, the external sign-in endpoints, and the linking rules.
**Contracts used from elsewhere:** `AppUser`, the auth cookie, antiforgery, `ICurrentUser` and the problem `type` convention (T-009), and `IInviteRedemption` and `HouseholdRole` (T-011).

- **Configuration.** Section `Authentication:External:Providers:<Scheme>` has these keys:
  - `Enabled`, `DisplayName`, `Authority`, `ClientId`, `ClientSecret`
  - `Scopes` (default `openid email profile`)
  - `EmailVerifiedClaim` (default `email_verified`)

  Each enabled entry is registered by one generic loop as an OpenID Connect scheme: authorization code flow with PKCE and callback path `/api/auth/signin-<scheme>`, so nginx proxies it. Google is just the entry `Google` with `Authority` `https://accounts.google.com`. If an entry is `Enabled` but `Authority`, `ClientId` or `ClientSecret` is missing, startup fails with a message naming the provider. Secrets come from user-secrets locally and from Key Vault or environment in Azure, never from committed files.
- **Endpoints** (`Features/Auth/ExternalAuthEndpoints.cs`):
  - `GET /api/auth/providers` (anonymous): returns 200 `[{ scheme, displayName }]` for enabled providers.
  - `POST /api/auth/external/{scheme}` (anonymous): a form post carrying the antiforgery token, plus optional `returnUrl` and `invite`. It returns 302 to the provider. An unknown or disabled scheme returns 404 ProblemDetails.
    - The invite token is kept only in the protected `AuthenticationProperties.Items`. It is never put in `returnUrl`, query strings or logs.
    - `returnUrl` must be a local path: it starts with `/` and does not start with `//` or `/\`. Anything else becomes `/`.
  - The OIDC callback signs into a short-lived (10 min) external cookie, `__Host-foodplanner-ext`, then continues to `GET /api/auth/external/complete`.
  - `complete` applies the linking rules, accepts the invite if present, issues the normal auth cookie (T-009), deletes the external cookie, and redirects with 302 to `returnUrl`.
  - These are browser navigations, so failures are reported as a redirect to `/login?error=<code>`, not as ProblemDetails JSON. Codes: `email-not-verified`, `external-failed`, `account-conflict`.
- **Linking rules**, applied in order. The email is treated as verified only if the configured claim is `true` (boolean or string).
  1. The (scheme, subject) pair is already linked: sign in that user.
  2. The email is missing or not verified: do not link, do not create, and do not sign in. Redirect with `email-not-verified`.
  3. A user with the same normalised email exists and that email is confirmed: add the link and sign in.
  4. A user with the same email exists but is unconfirmed: behaviour per Q-002. Until then, redirect with `account-conflict` and change nothing.
  5. No such user: create the user with `EmailConfirmed = true` and no password, link, and sign in.
- **Invite after sign-in.** `IInviteRedemption.AcceptAsync`: success adds the membership. Either way the redirect URL gets `invite=accepted`, `invite=invalid` or `invite=already-member`, and the user stays signed in. Whether the invite is bound to the email is decided by Q-005.
- **Public origin.** `redirect_uri` uses the public HTTPS origin. Forwarded headers are honoured only from the configured proxy networks.
- **Tests** drive the real OIDC handler against an in-process fake identity provider: a static OIDC configuration, plus a stubbed back-channel that issues id_tokens signed with a test key. There are no network calls.

## Out of scope
Microsoft, Apple and Vipps (T-057), passkeys (T-058), unlinking providers, account settings, login UI, Keycloak, and Data Protection key persistence. Rate limiting is pending Q-003.

## Acceptance criteria (each must be testable)
- [ ] `Providers_listed_from_config`: with `Google` and a second scheme `TestIdp` enabled only in test configuration, `GET /api/auth/providers` lists both. With `TestIdp.Enabled=false` it lists only Google. No code differs between the runs.
- [ ] `Second_provider_full_sign_in_by_config`: the full sign-in round trip succeeds through `TestIdp`, which exists only in configuration.
- [ ] `Challenge_is_code_flow_with_pkce`: `POST /api/auth/external/Google` returns 302 to the Google authorization endpoint. The query has `response_type=code`, `code_challenge`, `state`, and `redirect_uri=https://<public-host>/api/auth/signin-Google`. The public host comes from trusted `X-Forwarded-*` headers.
- [ ] `Challenge_requires_antiforgery`: the challenge POST without a valid token returns 400 and no redirect.
- [ ] `Invite_round_trip_new_user`: an Owner creates an invite with role Adult for H. A new identity (verified email) completes the challenge with that invite. The final redirect has `invite=accepted`, and `GET /api/households` for the new user lists H with role `Adult`.
- [ ] `Invite_not_exposed`: no `Location` header or query string in the round trip contains the plain invite token.
- [ ] `Invalid_invite_round_trip`: with an expired or used invite, the user is signed in, the redirect has `invite=invalid`, and no membership is created.
- [ ] `Unverified_email_never_links`: the fake provider returns `email_verified=false` with the email of an existing confirmed user. Afterwards there are no new rows in `AspNetUserLogins`, no auth cookie is issued, and the redirect is to `/login?error=email-not-verified`. The same happens when the claim is missing.
- [ ] `Verified_email_links_existing_confirmed_user`: with `email_verified=true`, the existing confirmed user gets one new external login. `GET /api/auth/me` returns that user's id.
- [ ] `Existing_link_signs_in`: a second sign-in with the same subject signs in the same user, even if the provider's email changed.
- [ ] `Open_redirect_blocked`: `returnUrl` values `https://evil.example`, `//evil.example` and `/\evil.example` each end with a redirect to `/`.
- [ ] `Misconfigured_provider_fails_startup`: `Enabled=true` without `ClientSecret` makes host startup throw with a message naming the provider.
- [ ] `Unconfirmed_local_account_conflict`: the behaviour matches the Q-002 answer. **Pending Q-002.**

## Open questions
- [Q-002](../questions/Q-002.md): handling of an unconfirmed local account with the same email (linking rule 4).
- [Q-005](../questions/Q-005.md): whether the invite must match the signed-in email.
- [Q-003](../questions/Q-003.md): rate limiting on the external sign-in endpoints.
