# Expense Tracker

Aplicație full-stack pentru urmărirea abonamentelor și cheltuielilor recurente.
Backend ASP.NET Core 8 + EF Core + PostgreSQL. Frontend Vue 3 + Vite + Tailwind CSS.
Deployabil cu `docker compose` pe orice VPS Linux cu Docker, folosind Nginx ca reverse proxy.

---

## Cuprins

1. [Ce face aplicația](#ce-face-aplicația)
2. [Quickstart local](#quickstart-local)
3. [Deployment producție](#deployment-producție)
4. [Variabile de mediu](#variabile-de-mediu)
5. [API Endpoints](#api-endpoints)
6. [Backup & Restore](#backup--restore)

---

## Ce face aplicația

Expense Tracker îți permite să urmărești abonamentele lunare sau anuale (Netflix, hosting, SaaS etc.).
Funcționalități principale:
- Autentificare securizată pe cookie-uri httpOnly + refresh token (rotație) și protecție CSRF
- Verificare cont prin email (cod) și resetare parolă prin email (SMTP / mailcow)
- Gestionare cont: schimbare parolă/email, „logout everywhere", toggle înregistrări
- Adaugă, editează, șterge, caută, sortează și filtrează (activ/inactiv) abonamente
- Totaluri lunare și anuale **per valută** (RON, EUR, USD…), curs valutar live + estimare totală
- Grafice (categorii + cheltuieli pe valută), badge-uri „scadent curând", export **și** import CSV
- Temă luminoasă/întunecată, responsive mobile-first, instalabilă ca PWA (offline app shell)

---

## Quickstart local

### Cerințe

- .NET 8 SDK
- PostgreSQL 16 (local sau Docker)
- Node.js 20+

### Backend

```bash
cd ExpenseTracker.Api

# Copiază și editează variabilele de dev
# Actualizează connection string-ul dacă PostgreSQL-ul tău ascultă pe alt port/user

dotnet ef database update        # creează schema
dotnet run --urls http://localhost:5000
# → http://localhost:5000/swagger
```

### Frontend

```bash
cd expense-tracker-ui
npm install
npm run dev
# → http://localhost:5173
```

La primul acces va trebui să te înregistrezi pe pagina de Login.

### Script convenabil (rulează ambele)

```bash
./dev.sh
```

---

## Deployment producție

Urmărește ghidul detaliat în [deploy.md](deploy.md).

### Pași rapizi

```bash
# 1. Clonează repo-ul pe VPS
git clone https://github.com/Alexandru2984/expenseTracker_cs.git && cd expenseTracker_cs

# 2. Completează variabilele de mediu
cp .env.example .env && nano .env

# 3. Rulează migrațiile
docker compose run --rm migrate

# 4. Construiește UI-ul și pornește serviciile
docker compose up -d ui-builder
docker compose up -d
```

Configurarea Nginx de pe host trebuie să pointeze către portul `8080` pentru API și către folderul `frontend_dist` pentru fișierele statice.

---

## Variabile de mediu

Copiază `.env.example` în `.env` și completează valorile. **Nu comite niciodată `.env`.**

| Variabilă | Descriere | Exemplu |
|---|---|---|
| `ConnectionStrings__DefaultConnection` | PostgreSQL connection string | `Host=db;Port=5432;Database=expense_tracker;Username=expense_user;Password=...` |
| `Jwt__Secret` | Cheie secretă pentru semnarea token-urilor JWT | `openssl rand -hex 32` |
| `Jwt__AccessTokenMinutes` | Durata access token (opțional, default 15) | `15` |
| `Jwt__RefreshTokenDays` | Durata refresh token (opțional, default 14) | `14` |
| `Cors__AllowedOrigins` | Origini CORS permise (virgulă, gol în prod) | `http://localhost:5173` |
| `AllowedHosts` | Host-uri permise (domeniul real în prod) | `expenses.micutu.com` |
| `ForwardedHeaders__TrustedNetworks` | CIDR-uri proxy de încredere (opțional) | `172.16.0.0/12,127.0.0.0/8` |
| `Auth__AllowRegistration` | Permite înregistrări noi (`true`/`false`) | `true` |
| `Smtp__Host` / `Smtp__Port` | Server SMTP (mailcow) + port | `mail.micutu.com` / `587` |
| `Smtp__UseStartTls` | STARTTLS (`true`) sau SSL direct (`false`) | `true` |
| `Smtp__User` / `Smtp__Password` | Credențiale mailbox expeditor | `noreply@micutu.com` / *(secret)* |
| `Smtp__From` / `Smtp__FromName` | Adresa și numele expeditorului | `noreply@micutu.com` / `Expense Tracker` |
| `ASPNETCORE_ENVIRONMENT` | Mediu .NET | `Production` |
| `POSTGRES_DB` / `POSTGRES_USER` / `POSTGRES_PASSWORD` | Credențiale container PostgreSQL | `expense_tracker` / `expense_user` / *(secret)* |

> Notă: dacă `Smtp__Host` nu e setat, codurile de verificare sunt scrise în log (fallback de dezvoltare), nu trimise pe email.

---

## API Endpoints

Sesiunea folosește cookie-uri httpOnly (`access_token` + `refresh_token`). Request-urile mutante
necesită header `X-CSRF-Token` egal cu cookie-ul `csrf_token` (double-submit). `/api/auth/*` și
`/health` sunt publice; restul necesită sesiune validă.

| Method | Endpoint | Descriere |
|---|---|---|
| `GET` | `/health` | Status aplicație + baza de date |
| `POST` | `/api/auth/register` | Înregistrare (cont neverificat + cod pe email) |
| `POST` | `/api/auth/verify-email` | Confirmă codul și autentifică |
| `POST` | `/api/auth/resend-code` | Retrimite codul de verificare |
| `POST` | `/api/auth/login` | Autentificare (blocată dacă emailul nu e verificat) |
| `POST` | `/api/auth/forgot-password` / `reset-password` | Resetare parolă prin cod email |
| `POST` | `/api/auth/refresh` / `logout` | Rotație token / deconectare |
| `GET` | `/api/auth/me` | Identitatea curentă |
| `GET` | `/api/account` | Info cont (email, status verificare) |
| `POST` | `/api/account/change-password` / `change-email` / `logout-all` | Gestionare cont |
| `GET` | `/api/subscriptions` | Listă paginată (`search`, `sortBy`, `status`) |
| `POST` `PUT` `DELETE` | `/api/subscriptions[/{id}]` | CRUD abonamente |
| `GET` | `/api/subscriptions/summary` | Totaluri per valută + scadențe |
| `GET` | `/api/subscriptions/export` | Export CSV |
| `POST` | `/api/subscriptions/import` | Import CSV |
| `GET` | `/api/subscriptions/rates` | Curs valutar live |

---

## Backup & Restore

### Backup manual

```bash
./scripts/backup-db.sh
```

### Restore

```bash
./scripts/restore-db.sh backups/expense_tracker_YYYY-MM-DD.sql.gz
```

---

## Teste

```bash
dotnet test ExpenseTracker.sln
```

Suita (xUnit) acoperă escaping-ul CSV anti-formula-injection, parserul CSV, hashing-ul
token-urilor, rezolvarea IP-ului real, izolarea pe utilizator (ownership) și fluxul de auth
(înregistrare → blocare login până la verificare) pe o bază de date in-memory.

---

## Tech Stack

| Layer | Tehnologie |
|---|---|
| Backend | ASP.NET Core 8 Web API |
| ORM | Entity Framework Core 8 |
| Baza de date | PostgreSQL 16 |
| Email | MailKit (SMTP / mailcow) |
| Frontend | Vue 3 + Vite + Tailwind CSS, instalabil ca PWA |
| Teste | xUnit + WebApplicationFactory |
| Reverse proxy | Nginx (Host) + Cloudflare |
| Containerizare | Docker + Docker Compose |

## Licență

[MIT](LICENSE)
