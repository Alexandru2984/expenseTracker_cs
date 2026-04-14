# Deploy Guide — Expense Tracker

Ghid pentru deployment pe un VPS Linux cu Docker.

## Cerințe preliminare

- VPS Linux cu Docker Engine ≥ 24 și Docker Compose CLI (plugin `compose`)
- Un domeniu cu DNS-ul `A` setat pe IP-ul VPS-ului
- Porturile 80 și 443 deschise în firewall

---

## 1. Pregătire server

```bash
# Instalează Docker (Ubuntu/Debian)
curl -fsSL https://get.docker.com | sh
sudo usermod -aG docker $USER
# Logout și login pentru a aplica grupul
```

---

## 2. Clonează repo-ul

```bash
git clone https://github.com/<user>/expense-tracker.git
cd expense-tracker
```

---

## 3. Configurează variabilele de mediu

```bash
cp .env.example .env
nano .env   # sau vim .env
```

Completează toate valorile din `.env`:

| Variabilă | Descriere |
|---|---|
| `ConnectionStrings__DefaultConnection` | String de conexiune PostgreSQL (host=db) |
| `API_TOKEN` | Token secret lung (`openssl rand -hex 32`) |
| `Cors__AllowedOrigins` | Lasă gol — frontend e same-origin prin Caddy |
| `ASPNETCORE_ENVIRONMENT` | `Production` |
| `POSTGRES_DB` | Numele bazei de date |
| `POSTGRES_USER` | Utilizatorul PostgreSQL |
| `POSTGRES_PASSWORD` | Parola PostgreSQL (trebuie să coincidă cu cea din connection string) |

---

## 4. Configurează Caddy

```bash
cp Caddyfile.example Caddyfile
nano Caddyfile   # Înlocuiește `your-domain.com` cu domeniul tău
```

---

## 5. Prima rulare — migrații baza de date

Rulează migrația EF Core **înainte** de prima pornire a API-ului:

```bash
docker compose run --rm migrate
```

> Această comandă pornește un container efemer care aplică toate migrațiile
> în baza de date, apoi se oprește. Rulează-l și la fiecare update care
> include migrații noi.

**Generarea manuală a bundle-ului EF (pentru CI/CD):**

```bash
cd ExpenseTracker.Api
dotnet tool install --global dotnet-ef
dotnet ef migrations bundle --self-contained -r linux-musl-x64 -o efbundle
```

---

## 6. Pornire producție

```bash
docker compose up -d
```

Aceasta va:
1. Construi imaginile (API + UI)
2. Copia fișierele statice ale frontend-ului în volumul Caddy
3. Porni db, api, caddy în fundal

Verifică statusul:

```bash
docker compose ps
docker compose logs -f api
```

---

## 7. Verificare

- `https://your-domain.com` → frontend Vue
- `https://your-domain.com/health` → `{"status":"Healthy",...}`
- `curl -H "Authorization: Bearer YOUR_TOKEN" https://your-domain.com/api/subscriptions` → `[...]`

---

## 8. Update aplicație

```bash
git pull
docker compose build
docker compose run --rm migrate   # dacă există migrații noi
docker compose up -d
```

---

## 9. Backup & Restore

### Backup manual

```bash
./scripts/backup-db.sh
```

Fișierele `.sql.gz` se salvează în `./backups/`.

### Backup automat (cron zilnic la 3:00)

```bash
crontab -e
# Adaugă:
0 3 * * * /path/to/expense-tracker/scripts/backup-db.sh >> /var/log/expense-tracker-backup.log 2>&1
```

### Restore

```bash
./scripts/restore-db.sh backups/expense_tracker_2026-04-14_03-00-00.sql.gz
```

---

## 10. Logs

```bash
docker compose logs api        # API logs
docker compose logs caddy      # Caddy access logs
docker compose logs db         # PostgreSQL logs
```
