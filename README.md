# foodplanner

## 1. Fresh machine setup

Do this once per machine. Windows first, then macOS and Linux. Every command is given for bash
(macOS, Linux, WSL, Git Bash) and for PowerShell.

### Windows (PowerShell)

```powershell
winget install --id Git.Git -e
winget install --id Microsoft.DotNet.SDK.10 -e
winget install --id OpenJS.NodeJS.LTS -e
winget install --id jqlang.jq -e
winget install --id Microsoft.VisualStudioCode -e
winget install --id Anthropic.ClaudeCode -e
winget install --id SUSE.RancherDesktop -e
```

Close and reopen PowerShell (new PATH), then install the VS Code extensions:

```powershell
code --install-extension ms-dotnettools.csharp
code --install-extension ms-azuretools.vscode-docker
code --install-extension dbaeumer.vscode-eslint
code --install-extension vitest.explorer
code --install-extension editorconfig.editorconfig
code --install-extension anthropic.claude-code
```

Why each one:
- **Git** also provides Git Bash, which Claude Code and `verify.sh` use.
- **.NET 10 SDK** and **Node LTS (22+)** build the API and the web app. **jq** is used by the safety hook.
- **VS Code** is the editor and debugger. **Claude Code** is the `claude` command that runs the orchestrator.
- **Rancher Desktop** is the container engine (SQL Server, API, web). It is free and not Docker Desktop.
  Start it once. In Preferences, Container Engine, choose **dockerd (moby)**, or from a terminal:
  `rdctl start --container-engine.name=moby`. Wait until it says it is running, then check with `docker version`.
  Alternatives (Docker Engine in WSL2, Podman, Docker Desktop): section 9.

Clone and check the machine:

```powershell
mkdir C:\projects -Force
cd C:\projects
git clone https://github.com/ptgc/foodplanner
cd foodplanner
git switch dev
```

Open a **new** terminal, then:

```powershell
.\scripts\doctor.ps1
```

If PowerShell blocks the script run `Set-ExecutionPolicy -Scope CurrentUser RemoteSigned` once, or run
scripts as `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\doctor.ps1`.
Windows PowerShell 5.1 and PowerShell 7 (`pwsh`, `winget install --id Microsoft.PowerShell -e`) both work.

### macOS (bash or zsh, Homebrew)

```bash
brew install git jq node@24 colima docker docker-compose
brew install --cask dotnet-sdk visual-studio-code claude-code
brew link --overwrite node@24      # puts node on PATH
mkdir -p ~/.docker && echo '{"cliPluginsExtraDirs":["'"$(brew --prefix)"'/lib/docker/cli-plugins"]}' > ~/.docker/config.json
colima start --cpu 4 --memory 6    # the container engine; Apple Silicon: see section 9 and 10
code --install-extension ms-dotnettools.csharp ms-azuretools.vscode-docker dbaeumer.vscode-eslint vitest.explorer editorconfig.editorconfig anthropic.claude-code
mkdir -p ~/projects && cd ~/projects
git clone https://github.com/ptgc/foodplanner && cd foodplanner && git switch dev
./scripts/doctor.sh
```

(Only run the `~/.docker/config.json` line if you have no existing Docker config; otherwise add the
`cliPluginsExtraDirs` entry by hand.)

### Linux (Ubuntu or Debian, bash)

```bash
sudo apt update && sudo apt install -y git jq curl
sudo add-apt-repository -y ppa:dotnet/backports      # Ubuntu only; Debian: use Microsoft's packages feed
sudo apt install -y dotnet-sdk-10.0
curl -fsSL https://deb.nodesource.com/setup_24.x | sudo -E bash - && sudo apt install -y nodejs
curl -fsSL https://get.docker.com | sudo sh          # Docker Engine and the compose plugin
sudo usermod -aG docker "$USER"                      # then log out and in again
sudo snap install code --classic
curl -fsSL https://claude.ai/install.sh | bash
code --install-extension ms-dotnettools.csharp ms-azuretools.vscode-docker dbaeumer.vscode-eslint vitest.explorer editorconfig.editorconfig anthropic.claude-code
mkdir -p ~/projects && cd ~/projects
git clone https://github.com/ptgc/foodplanner && cd foodplanner && git switch dev
./scripts/doctor.sh
```

`doctor` prints `[ok]` or `[FAIL]` with a fix for each tool. Do not go on until it says `Ready.`.
The Linux and macOS commands in this section have not been run on those systems by the author.

## 2. What this is

Mobile-first web app for families: pick the week's dinners, get a shopping list, cook from recipes.
This repository is iteration L1: it runs on your machine only, with nothing in Azure.
Rules and workflow: `CLAUDE.md`. Decisions, specs and backlog: `docs/`.

| Part | Where | Runs as |
| --- | --- | --- |
| SQL Server 2022 | `docker-compose.yml` | container, `127.0.0.1:1433` |
| API (.NET 10 minimal API) | `src/Api` | container, or on the host in the dev loop |
| Web (React Router SPA) | `web/` | container (nginx), or Vite dev server |

Prerequisites: what section 1 installs (Git, .NET 10 SDK, Node 22+, jq, a container engine, VS Code).

| URL | What |
| --- | --- |
| http://localhost:3000 | web in containers (nginx; proxies `/api` to the api container) |
| http://localhost:5173 | web dev server (Vite; proxies `/api` to http://localhost:5000) |
| http://localhost:5000 | API on the host (dev loop); `/openapi/v1.json` in Development |
| `127.0.0.1,1433` | SQL Server (login `sa`, password in user secrets), loopback only |

Always use `localhost` in the browser, not `127.0.0.1` or your machine name, so cookies stay on one origin.

## 3. Run everything in containers

```bash
./scripts/local-up.sh
```

```powershell
.\scripts\local-up.ps1
```

The first run generates the SQL password (section 8), builds the api and web images (a few minutes)
and starts SQL, creates the `foodplanner` database, then starts api and web. Open
http://localhost:3000: the page shows **API is Healthy.** and **Database: Up**.

Check from a terminal:

```bash
curl http://localhost:3000/api/health/ready
```

```powershell
Invoke-RestMethod http://localhost:3000/api/health/ready
```

Expected: `status` Healthy, `database` Up.

## 4. Stop and reset

| Action | bash | PowerShell |
| --- | --- | --- |
| Stop, keep the data | `./scripts/local-down.sh` | `.\scripts\local-down.ps1` |
| Stop and **delete the SQL volume** (all local data) | `./scripts/local-reset.sh` | `.\scripts\local-reset.ps1` |

Reset keeps your user secrets, so the password stays the same. `local-up` afterwards starts with an empty database.

## 5. VS Code dev loop (SQL in Docker, API on the host with the debugger, web with Vite)

Open the folder in VS Code (`code .`) and accept the recommended extensions.

1. **F5** (Run and Debug, "API (debug, host) against compose SQL"). It first runs the task `api: prepare`:
   `sql: up` (starts only SQL and the database init through `local-up`, password from user secrets) and a build.
   The API then runs under the debugger on http://localhost:5000 with `ASPNETCORE_ENVIRONMENT=Development`,
   which is what makes it load the user secrets.
2. **Terminal, Run Task, `web: dev`**. Vite serves http://localhost:5173 and proxies `/api` to the API.
3. Open http://localhost:5173.

Without VS Code, the same three steps in a terminal (the API and web each need their own terminal):

```bash
./scripts/local-up.sh sql
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5000 dotnet run --project src/Api
```

```bash
npm --prefix web ci
npm --prefix web run dev
```

```powershell
.\scripts\local-up.ps1 sql
$env:ASPNETCORE_ENVIRONMENT = 'Development'; $env:ASPNETCORE_URLS = 'http://localhost:5000'
dotnet run --project src/Api
```

```powershell
npm --prefix web ci
npm --prefix web run dev
```

Without `ASPNETCORE_ENVIRONMENT=Development`, `dotnet run` starts as Production and ignores user secrets, and
`/api/health/ready` returns 503 "Database not configured".

## 6. Tests and verify

```bash
./scripts/verify.sh
```

```powershell
bash ./scripts/verify.sh     # uses Git Bash, which comes with Git
```

`verify.sh` checks that no SQL password is in a tracked file, then builds, formats, tests the .NET solution
(integration tests need the container engine running) and lints, typechecks, tests and builds `web`.
Single parts: `dotnet test --solution Foodplanner.slnx` and `npm --prefix web test` (same in both shells).

`Api.Tests` start one SQL Server Testcontainer (`mcr.microsoft.com/mssql/server`, pinned 2022 CU tag in `SqlServerFixture.cs`) per run, with a
password generated at runtime, and give each test class a fresh database with the migrations applied.
Testcontainers uses `DOCKER_HOST` if set, otherwise the default local endpoint (`npipe://./pipe/docker_engine` on
Windows, which Rancher Desktop in dockerd (moby) mode provides; `/var/run/docker.sock` elsewhere). For another
endpoint (Colima, rootless Podman) set `DOCKER_HOST` to the endpoint `docker context inspect` shows.
`verify.sh` checks the engine with `docker info` before the tests and stops there if it is not running.

New migration (the Api is the startup project; migrations are never applied at app startup):
`dotnet tool restore` once, then
`dotnet ef migrations add <Name> --project src/Infrastructure --startup-project src/Api --output-dir Persistence/Migrations`.

### End-to-end tests (Playwright)

With the stack up (`local-up`), run the suite (mobile 390px and desktop projects, headless chromium, traces kept on failure):

```bash
./scripts/local-up.sh
./scripts/e2e.sh                    # E2E=1 ./scripts/verify.sh runs it as part of verify
```

```powershell
.\scripts\local-up.ps1
.\scripts\e2e.ps1
```

Details and CI wiring: `tests/e2e/README.md`.

### Regenerating the API contract

After changing API endpoints, regenerate the OpenAPI document and then the TypeScript types, and commit both:

```bash
./scripts/openapi.sh
npm --prefix web run api:generate
```

```powershell
bash ./scripts/openapi.sh
npm --prefix web run api:generate
```

(`openapi.ps1` also exists: `.\scripts\openapi.ps1`.) `verify.sh` fails if either is stale.

## 7. Script list

| Script (`.sh` / `.ps1`) | What it does |
| --- | --- |
| `doctor` | checks the machine: tools, container engine, branch |
| `local-secrets` | generates the SQL password into user secrets, once (run by `local-up` for you) |
| `local-up` | starts the stack; `local-up sql` starts only SQL and the database init |
| `local-down` | stops, keeps the SQL volume |
| `local-reset` | stops and deletes the SQL volume |
| `verify` (`.sh` only) | build, lint and test everything |
| `e2e` | runs the Playwright e2e suite against the running stack |
| `openapi` | regenerates `openapi/openapi.v1.json` |
| `start-orchestrator` | starts Claude Code as the orchestrator (section 11) |

## 8. Secrets

No password is stored in the repo, in compose files, in `.env` files or in scripts. `local-secrets` generates a
random SQL Server `sa` password (24 letters and digits plus one `-`; meets the SQL Server policy) and stores two
**.NET user secrets** for `src/Api` on your machine (Windows: `%APPDATA%\Microsoft\UserSecrets\`, others: `~/.microsoft/usersecrets/`):

- `Sql:SaPassword`: read by `local-up`/`down`/`reset`, passed to compose as `MSSQL_SA_PASSWORD` for that one process only.
- `ConnectionStrings:Default`: used by the API when it runs on the host (`127.0.0.1,1433`; see troubleshooting).
  Inside containers compose builds the connection string itself.

List the key names (this prints the values, do not paste the output anywhere):

```bash
dotnet user-secrets list --project src/Api
```

```powershell
dotnet user-secrets list --project src/Api
```

**Rotate the password.** The SQL data volume stores the password it was created with, so reset first, then drop the secrets and start again:

```bash
./scripts/local-reset.sh
dotnet user-secrets remove Sql:SaPassword --project src/Api
dotnet user-secrets remove ConnectionStrings:Default --project src/Api
./scripts/local-up.sh
```

```powershell
.\scripts\local-reset.ps1
dotnet user-secrets remove Sql:SaPassword --project src/Api
dotnet user-secrets remove ConnectionStrings:Default --project src/Api
.\scripts\local-up.ps1
```

To choose your own password, run `dotnet user-secrets set Sql:SaPassword "<password>" --project src/Api` and the same for
`ConnectionStrings:Default` instead of the last step, after the reset. Policy: at least 8 characters with three of upper, lower, digit, symbol; avoid `; ' " $ \` and spaces.

## 9. Container engine without Docker Desktop

The scripts only need a `docker` command with `docker compose`. Check any option with `docker version` and `docker compose version`.

- **Windows, Rancher Desktop** (default, section 1): dockerd (moby) mode. This is what the author tested.
- **Windows, Docker Engine inside WSL2.** Work entirely inside WSL (clone into the Linux file system, use the bash commands, install .NET, Node, jq there as in the Linux part of section 1, and open VS Code with `code .` from WSL, which uses the WSL extension `ms-vscode-remote.remote-wsl`):
  ```powershell
  wsl --install -d Ubuntu
  wsl -d Ubuntu
  ```
  ```bash
  curl -fsSL https://get.docker.com | sudo sh
  sudo usermod -aG docker "$USER"
  sudo service docker start       # needed after every Windows restart unless systemd is enabled
  ```
- **macOS, Colima:** `brew install colima docker docker-compose` then `colima start --cpu 4 --memory 6`. Stop with `colima stop`.
- **Linux, Docker Engine:** `curl -fsSL https://get.docker.com | sudo sh`, `sudo usermod -aG docker "$USER"`, log out and in, `sudo systemctl enable --now docker`.
- **Podman** (not tested here): the scripts call `docker`, so make Podman answer to it.
  Windows: `winget install --id RedHat.Podman -e`, `podman machine init`, `podman machine start`, and enable Docker compatibility in Podman Desktop.
  macOS: `brew install podman docker docker-compose`, `podman machine init`, `podman machine start`.
  Linux: `sudo apt install podman podman-docker podman-compose` (rootless Podman cannot publish ports below 1024; ours are above).
- **Docker Desktop** also works: `winget install --id Docker.DockerDesktop -e`, `brew install --cask docker`.

## 10. Troubleshooting

- **A port is in use** (3000, 5000, 5173, 1433): find the process and stop it.
  ```bash
  lsof -i :3000          # macOS, Linux
  ```
  ```powershell
  Get-NetTCPConnection -LocalPort 3000 | Select-Object LocalPort, OwningProcess
  Get-Process -Id <OwningProcess>
  ```
  A local SQL Server on 1433 conflicts with the container: stop that service.
- **Container runtime not running** (`Cannot connect to the Docker daemon`, `docker version` shows only Client):
  start Rancher Desktop / Docker Desktop and wait until it is ready; Colima: `colima start`;
  WSL2 engine: `sudo service docker start`; Linux: `sudo systemctl start docker`.
- **SQL password policy / `Login failed for user 'sa'`.** If you set your own password it must meet the policy (section 8). If the
  secret and the existing volume disagree (you rotated the secret without a reset), run the rotation steps in section 8: reset first.
  The `sql` container shows `unhealthy` for the same reasons: `docker logs foodplanner-sql-1`.
- **`/api/health/ready` returns 503 from the host API.** Check that SQL is up (`docker ps`), that the API runs with
  `ASPNETCORE_ENVIRONMENT=Development` (section 5), and that the connection string uses `127.0.0.1,1433`, not `localhost,1433`:
  on Windows `localhost` resolves to `::1` first, and Rancher Desktop only forwards the container port on IPv4 loopback, so `localhost,1433` times out.
- **Apple Silicon.** The SQL Server image is amd64 only. Colima: `colima start --arch aarch64 --vm-type vz --vz-rosetta --cpu 4 --memory 6` (Rosetta); Rancher Desktop: enable Rosetta in Preferences, Virtual Machine, Emulation (VZ). Without Rosetta it falls back to slow QEMU emulation and SQL Server may crash. Azure SQL Edge (arm64) is retired and is not a supported substitute; the image is not changed here.
- **PowerShell says scripts are disabled:** `Set-ExecutionPolicy -Scope CurrentUser RemoteSigned`.
- **Docker build cannot reach the registry** behind a proxy or VPN: set the proxy in the engine's settings.

## 11. Orchestrator (building the app with Claude Code)

Open `claude` once in this folder and accept the trust prompt, then quit. Then:

```bash
./scripts/start-orchestrator.sh
```

```powershell
.\scripts\start-orchestrator.ps1
```

This runs `claude --agent orchestrator` with the prompt in `docs/prompts/local-iteration-1.md`.
Tasks are built one at a time on `task/T-xxx-*` branches and merged into `dev`. Never work on `test` or `prod`; they are promoted by PR.

## 12. Sign-in and cookies (T-009)

Local accounts: `POST /api/auth/register` then the emailed link (`confirm-email`), then `login`. Until a production email
provider exists (T-017) the only sender is the in-memory `Recording` one, so no mail leaves the machine and confirmation
links are not logged. Outside Development the API refuses to start unless `Email:Provider=Recording` is set (compose sets
`Email__Provider`); a seeded, already confirmed local admin is T-029.

- Auth cookie `__Host-foodplanner` (HttpOnly, Secure, SameSite=Lax, Path=/, no Domain, 14 days sliding) and antiforgery cookie
  `__Host-foodplanner-xsrf` (HttpOnly, Secure, SameSite=Strict). Every POST/PUT/PATCH/DELETE under `/api` needs the header
  `X-XSRF-TOKEN` from `GET /api/auth/antiforgery`; fetch a new token after login and logout.
- Cookies are Secure on `http://localhost` too: Chrome, Edge and Firefox accept Secure cookies from localhost, Safari does not.
  Nginx (`:3000`) and the Vite proxy (`:5173`) pass `Set-Cookie` through unchanged. The API treats every request as HTTPS
  (`Auth:AssumeHttps`, default true) because TLS ends before it.
- Settings (all optional, env var form `Auth__PublicBaseUrl`): `Auth:PublicBaseUrl` (origin used in email links, default
  `http://localhost:3000`; set it when deployed), `Auth:MinPasswordLength` (10), `Auth:LockoutMaxFailedAttempts` (5),
  `Auth:LockoutMinutes` (15), `Auth:CookieLifetimeDays` (14), `Auth:ConfirmationTokenLifetimeHours` (24),
  `RateLimiting:{Auth,LoginByEmail,InviteAccept,ExternalSignin}:{PermitLimit,WindowSeconds}`, and
  `ForwardedHeaders:TrustedNetworks:0..n` (CIDR of the reverse proxy; without it `X-Forwarded-For` is ignored and every
  client behind nginx shares one rate-limit bucket).
- Data Protection keys: for Azure see [docs/backlog/T-016.md](docs/backlog/T-016.md). Local compose keys are not persisted,
  so restarting the API container signs everyone out and invalidates pending confirmation links.
- Behind a trusted proxy (`ForwardedHeaders:TrustedNetworks` set) the API relies on `X-Forwarded-Proto` (the proxy must send
  `https`) and skips the `AssumeHttps` rewrite; without trusted networks outside Development it logs a warning at startup.
