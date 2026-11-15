# CHANGES.md

> Arhivă istorică a primului refactor. Descrierile de mai jos despre API_TOKEN,
> localStorage, Caddy și .NET 8 nu descriu versiunea curentă. Pentru starea actuală
> vezi README și [auditul din noiembrie 2026](docs/AUDIT-AND-ROADMAP.md).

Rezumatul complet al modificărilor efectuate în cadrul refactor-ului production-ready.

---

## Task 1 — Security

### 1.1 Autentificare Bearer Token
- **Nou:** `ExpenseTracker.Api/Auth/ApiKeyAuthenticationHandler.cs` — handler ASP.NET Core custom care validează `Authorization: Bearer <token>` față de env var `API_TOKEN`.
- La startup în `Production`, dacă `API_TOKEN` sau connection string-ul lipsesc, aplicația refuză să pornească cu mesaj `Fatal` în log.
- Toate endpoint-urile `/api/*` au `[Authorize]`; `/health` și Swagger (dev only) rămân publice.
- **Frontend:** Token stocat în `localStorage` (cheie `api_token`). Dacă lipsește, se afișează ecranul de login (`LoginView.vue`). La 401, token-ul se șterge și revinem la login. Request interceptor axios atașează header-ul `Authorization`.

### 1.2 Configurare prin variabile de mediu
- `appsettings.json` — eliminat orice secret (connection string și token).
- `appsettings.Development.json` — default-uri pentru dev (`localhost:5432`, `dev-token-change-me`, CORS origins locale).
- **Nou:** `.env.example` — documentează toate variabilele necesare: `ConnectionStrings__DefaultConnection`, `API_TOKEN`, `Cors__AllowedOrigins`, `ASPNETCORE_ENVIRONMENT`, `POSTGRES_DB`, `POSTGRES_USER`, `POSTGRES_PASSWORD`.

### 1.3 CORS dinamic
- Înlocuit lista hardcodată `localhost:5173/5174/5175` cu `Cors:AllowedOrigins` (virgulă-separated, citit din config).
- Politica se numește `"Frontend"`. În producție (same-origin prin Caddy), lista poate fi goală.

### 1.4 Rate Limiting
- Folosit `AddRateLimiter` built-in .NET 8.
- Politică globală (fixed window): **100 req/min per IP**.
- Politică `"writes"` pe `POST`, `PUT`, `DELETE`: **20 req/min per IP**.
- Răspuns 429 la depășire.

---

## Task 2 — Deployment Infrastructure

### 2.1 Dockerfile API (`ExpenseTracker.Api/Dockerfile`)
- Multi-stage: `sdk:8.0` → build/publish + bundle EF; `aspnet:8.0-alpine` → runtime.
- Stage dedicat pentru generarea EF migration bundle (`--self-contained -r linux-musl-x64`).
- Utilizator non-root (`appuser:appgroup`).
- Expune portul 8080.

### 2.2 Dockerfile Frontend (`expense-tracker-ui/Dockerfile`)
- Multi-stage: `node:20-alpine` build → `alpine:3.19` distribuție.
- Imaginea finală funcționează ca **init container**: copiază `dist/` într-un volum Docker montat (`DEST` env var, default `/srv`).

### 2.3 `docker-compose.yml`
- **`db`:** `postgres:16-alpine`, volum named `postgres_data`, healthcheck `pg_isready`, fără port expus extern.
- **`api`:** build din `ExpenseTracker.Api/`, `env_file: .env`, `restart: unless-stopped`, healthcheck pe `/health`.
- **`ui-builder`:** init container (build frontend → populează volum `frontend_dist`), `restart: "no"`.
- **`caddy`:** `caddy:2-alpine`, porturi 80/443, monta `Caddyfile`, `frontend_dist:/srv:ro`, `caddy_data`, `caddy_config`; depinde de `ui-builder` (completed_successfully) și `api` (healthy).
- **`migrate`:** același build ca `api`, `entrypoint: ["/app/efbundle"]`, profile `migrate`. Rulat manual cu `docker compose run --rm migrate`.

### 2.4 `Caddyfile.example`
- Template cu `your-domain.com`, HTTPS automat Let's Encrypt, `reverse_proxy /api/* api:8080`, `file_server` + SPA fallback, `encode gzip`.

### 2.5 `dev.sh` (înlocuiește `start.sh`)
- Același comportament, clar etichetat ca script de dev local.
- `start.sh` eliminat.

### 2.6 `deploy.md`
- Ghid complet de deployment producție: cerințe VPS, configurare `.env`, Caddyfile, migrații, pornire, update, backup/restore, logs.

### 2.7 `.dockerignore`
- `ExpenseTracker.Api/.dockerignore`: exclude `bin/`, `obj/`.
- `expense-tracker-ui/.dockerignore`: exclude `node_modules/`, `dist/`, `.vite/`.

### 2.8 Migrații — gate Development only
- `db.Database.Migrate()` mutat în blocul `if (app.Environment.IsDevelopment())`.
- În producție: `docker compose run --rm migrate` rulează efbundle-ul.

---

## Task 3 — Correctness Bugs

### 3.1 Summary multi-valută corectat
- Eliminat `TotalMonthlyEquivalent` (suma amesteca valute diferite — greșit).
- `GetSummary` returnează acum `byCurrency[].{currency, monthlyTotal, yearlyTotal, activeCount}`.
- **Frontend:** card per valută în loc de un singur card cu prima valută.

### 3.2 DTOs + Validare (`ExpenseTracker.Api/Dtos/SubscriptionDtos.cs`)
- `CreateSubscriptionDto`: fără `Id`, fără `IsActive` (totdeauna `true` la creare).
- `UpdateSubscriptionDto`: include `IsActive`.
- `SubscriptionResponseDto`: câmpuri de audit incluse.
- `PagedResult<T>`: wrapper paginare `{ items, total }`.
- `SummaryResponseDto` / `CurrencySummary`: shape fix pentru summary.
- Atribute: `[Required]`, `[StringLength]`, `[Range(0, 1_000_000)]`.
- Controller mapare manuală via `ToDto()` static.

### 3.3 Paginare pe `GetAll`
- `GET /api/subscriptions?skip=0&take=50` — max 200, default 50.
- Returnează `{ items: [...], total: N }`.
- Frontend solicită `take=200` (nicio UI pagination, dar contractul e viitor-proof).

---

## Task 4 — Robustness & Observability

### 4.1 Global Error Handler
- `app.UseExceptionHandler()` cu `ProblemDetails` RFC 7807.
- În Development: include stack trace în `detail`. În Production: fără detalii interne.

### 4.2 Serilog Structured Logging
- Pachet: `Serilog.AspNetCore` 8.0.3.
- Bootstrap logger captează erori la startup.
- `UseSerilogRequestLogging()` pe fiecare request.
- Output pe stdout (Docker compose logs captează).
- Bootstrap logger + `try/catch` principal în `Program.cs` cu exit code corect.

### 4.3 Health Checks
- Pachet: `AspNetCore.HealthChecks.NpgSql` 8.0.1.
- `AddHealthChecks().AddNpgSql(connectionString)`.
- `MapHealthChecks("/health").AllowAnonymous()` — public, folosit de Docker healthcheck.

### 4.4 Câmpuri de Audit
- `SubscriptionItem`: adăugat `CreatedAt` și `UpdatedAt` (`DateTime` UTC).
- `AppDbContext.SaveChangesAsync()` override setează automat `CreatedAt` la ADD și `UpdatedAt` la ADD/MODIFY.
- **Migrație:** `20260414120011_AuditFieldsAndDateOnly.cs`.

### 4.5 Frontend Error Handling
- `handleSubmit`, `handleDelete` wrapped în try/catch.
- Erori afișate ca toast notifications (4 secunde, `TransitionGroup`).
- `confirm()` nativ înlocuit cu `ConfirmModal.vue` (Teleport'd overlay, animat).

---

## Task 5 — Polish

### 5.1 Frontend `.env`
- `expense-tracker-ui/.env.development`: `VITE_API_URL=/api`
- `expense-tracker-ui/.env.production`: `VITE_API_URL=/api`
- `api.js`: folosește `import.meta.env.VITE_API_URL` (explicit, chiar dacă valorile coincid).

### 5.2 README Refăcut
- Un singur document în română care acoperă: ce face aplicația, quickstart local, deployment producție, tabel env vars, API endpoints, backup/restore, tech stack.

### 5.3 Backup & Restore Scripts
- `scripts/backup-db.sh`: `pg_dump` via `docker compose exec`, `.sql.gz` timestamped în `./backups/`, rotație 30 zile.
- `scripts/restore-db.sh`: `gunzip | psql` cu confirmare interactivă.
- Instrucțiuni cron în README și deploy.md.

### 5.4 GitHub Actions CI (`.github/workflows/ci.yml`)
- **On push/PR:** `dotnet restore + build + test` + `npm ci + build`.
- **On tag `v*`:** build și push imagini Docker la GHCR (API + UI).

### 5.5 MIT License
- `LICENSE` adăugat la root.

### 5.6 Diverse Curățenie
- `expense_tracker_c#.sln` redenumit în `ExpenseTracker.sln` (caracterul `#` în denumiri de fișiere creează probleme în shells).
- `DateTime NextBillingDate` → `DateOnly NextBillingDate` (mapare `date` PostgreSQL, Npgsql 8 suportă nativ). Migrație inclusă.
- `.editorconfig`: C#=4-spații, JS/Vue/YAML=2-spații, `lf`, `utf-8`.
- `.dockerignore` pentru ambele proiecte.
- `launchSettings.json`: port actualizat la 5000 (consistent cu proxy Vite).
- `LoginView.vue`: ecran de autentificare minimal, dark mode.
- `ConfirmModal.vue`: înlocuiește `window.confirm()`.

---

## Stare Finală

| Criteriu | Status |
|---|---|
| `docker compose up -d` ridică aplicația HTTPS | ✅ |
| Fără token valid → 401 pe `/api/*` | ✅ |
| Summary returnează totaluri per valută corecte | ✅ |
| `/health` returnează 200 cu status DB | ✅ |
| Loguri structurate JSON pe stdout | ✅ |
| `CHANGES.md` la root cu toate modificările | ✅ |
| README duce un cititor de la zero la running în <10 min | ✅ |
