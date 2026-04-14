# Expense Tracker

Aplicație full-stack pentru urmărirea abonamentelor și cheltuielilor recurente.
Backend ASP.NET Core 8 + EF Core + PostgreSQL. Frontend Vue 3 + Vite + Tailwind CSS.
Deployabil cu un singur `docker compose up -d` pe orice VPS Linux cu Docker.

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
- Adaugă, editează și șterge abonamente
- Vizualizează totaluri lunare și anuale **per valută** (RON, EUR, USD etc.)
- Filtrează abonamente active / inactive
- Protejat prin Bearer token — sigur de expus pe internet

---

## Quickstart local

### Cerințe

- .NET 8 SDK
- PostgreSQL 16 (local sau Docker)
- Node.js 20+

### Backend

```bash
cd ExpenseTracker.Api

# Copiază și editează variabilele de dev (opțional, appsettings.Development.json e deja configurat)
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

La primul acces vei fi întâmpinat de ecranul de login.
Token-ul implicit pentru dev este `dev-token-change-me` (setat în `appsettings.Development.json`).

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
git clone https://github.com/<user>/expense-tracker.git && cd expense-tracker

# 2. Completează variabilele de mediu
cp .env.example .env && nano .env

# 3. Configurează domeniul în Caddyfile
cp Caddyfile.example Caddyfile && nano Caddyfile   # înlocuiește your-domain.com

# 4. Rulează migrațiile (prima dată și după fiecare update cu migrații noi)
docker compose run --rm migrate

# 5. Pornește totul
docker compose up -d
```

Caddy obține automat certificat TLS de la Let's Encrypt.

---

## Variabile de mediu

Copiază `.env.example` în `.env` și completează valorile. **Nu comite niciodată `.env`.**

| Variabilă | Descriere | Exemplu |
|---|---|---|
| `ConnectionStrings__DefaultConnection` | PostgreSQL connection string | `Host=db;Port=5432;Database=expense_tracker;Username=expense_user;Password=...` |
| `API_TOKEN` | Token secret pentru autentificare API | `openssl rand -hex 32` |
| `Cors__AllowedOrigins` | Origini CORS permise (virgulă, gol în prod) | `http://localhost:5173` |
| `ASPNETCORE_ENVIRONMENT` | Mediu .NET | `Production` |
| `POSTGRES_DB` | Numele bazei de date | `expense_tracker` |
| `POSTGRES_USER` | Utilizatorul PostgreSQL | `expense_user` |
| `POSTGRES_PASSWORD` | Parola PostgreSQL | *(secret)* |

---

## API Endpoints

Toate endpoint-urile `/api/*` necesită header `Authorization: Bearer <API_TOKEN>`.
`/health` este public.

| Method | Endpoint | Descriere |
|---|---|---|
| `GET` | `/health` | Status aplicație + baza de date |
| `GET` | `/api/subscriptions?skip=0&take=50` | Lista paginată (max 200) |
| `GET` | `/api/subscriptions/{id}` | Un abonament după ID |
| `POST` | `/api/subscriptions` | Adaugă abonament |
| `PUT` | `/api/subscriptions/{id}` | Actualizează abonament |
| `DELETE` | `/api/subscriptions/{id}` | Șterge abonament |
| `GET` | `/api/subscriptions/summary` | Totaluri per valută |

### Format răspuns summary

```json
{
  "byCurrency": [
    { "currency": "RON", "monthlyTotal": 120.50, "yearlyTotal": 1446.00, "activeCount": 3 },
    { "currency": "EUR", "monthlyTotal": 45.00,  "yearlyTotal": 540.00,  "activeCount": 2 }
  ],
  "activeSubscriptions": 5,
  "totalSubscriptions": 7
}
```

---

## Backup & Restore

### Backup manual

```bash
./scripts/backup-db.sh
# Salvează în ./backups/expense_tracker_YYYY-MM-DD_HH-MM-SS.sql.gz
```

### Backup automat zilnic (cron la 3:00)

```bash
crontab -e
# Adaugă (înlocuiește calea):
0 3 * * * /opt/expense-tracker/scripts/backup-db.sh >> /var/log/et-backup.log 2>&1
```

Backup-urile mai vechi de 30 de zile sunt șterse automat.

### Restore

```bash
./scripts/restore-db.sh backups/expense_tracker_2026-04-14_03-00-00.sql.gz
```

---

## Tech Stack

| Layer | Tehnologie |
|---|---|
| Backend | ASP.NET Core 8 Web API |
| ORM | Entity Framework Core 8 (Code-First) |
| Baza de date | PostgreSQL 16 |
| Frontend | Vue 3 + Vite + Tailwind CSS |
| Reverse proxy | Caddy 2 (HTTPS automat) |
| Containerizare | Docker + Docker Compose |
| Logging | Serilog (stdout JSON) |
| CI | GitHub Actions |

## Licență

[MIT](LICENSE)

