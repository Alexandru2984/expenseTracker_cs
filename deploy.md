# Deploy Guide — Expense Tracker

Ghid pentru deployment pe un VPS Linux cu Docker și Nginx (host).

## Cerințe preliminare

- VPS Linux cu Docker Engine ≥ 24 și Docker Compose CLI (plugin `compose`)
- Un domeniu cu DNS-ul `A` setat pe IP-ul VPS-ului (ex: `expenses.micutu.com`)
- Nginx instalat pe sistemul gazdă (host)
- Porturile 80 și 443 deschise în firewall-ul host-ului

---

## 1. Pregătire server

```bash
# Instalează Docker (Ubuntu/Debian)
curl -fsSL https://get.docker.com | sh
sudo usermod -aG docker $USER

# Instalează Nginx (Ubuntu/Debian)
sudo apt update && sudo apt install nginx certbot python3-certbot-nginx -y
```

---

## 2. Clonează repo-ul

```bash
git clone https://github.com/Alexandru2984/expenseTracker_cs.git
cd expenseTracker_cs
```

---

## 3. Configurează variabilele de mediu

```bash
cp .env.example .env
nano .env
```

Completează toate valorile din `.env`:

| Variabilă | Descriere |
|---|---|
| `ConnectionStrings__DefaultConnection` | String de conexiune PostgreSQL (host=db) |
| `Jwt__Secret` | Cheie secretă JWT (min 32 caractere, `openssl rand -hex 32`) |
| `Cors__AllowedOrigins` | Lasă gol (frontend e same-origin prin Nginx) |
| `AllowedHosts` | Domeniul real (ex. `expenses.micutu.com`) |
| `Auth__AllowRegistration` | `true` cât îți creezi conturile, apoi `false` |
| `Smtp__Host` / `Smtp__Port` / `Smtp__User` / `Smtp__Password` / `Smtp__From` | SMTP mailcow pentru emailuri de verificare/reset |
| `ASPNETCORE_ENVIRONMENT` | `Production` |
| `POSTGRES_DB` / `POSTGRES_USER` / `POSTGRES_PASSWORD` | Credențiale PostgreSQL |

> Lista completă și comentată este în `.env.example`.

### Email (mailcow)

Creează mailbox-ul expeditor via API-ul mailcow, apoi pune credențialele în `.env`:

```bash
MAILCOW_HOST=https://mail.micutu.com MAILCOW_API_KEY=xxxxxxxx \
  ./scripts/provision-mailbox.sh noreply micutu.com 'ParolaPuternica!'
```

---

## 4. Pornire containere

### 4.1 Prima rulare — migrații baza de date

Rulează migrația EF Core:

```bash
docker compose run --rm migrate
```

### 4.2 Construire UI și pornire servicii

```bash
docker compose up -d ui-builder  # Construiește activele statice în ./frontend_dist
docker compose up -d
```

---

## 5. Configurează Nginx (Host)

Creează o configurație în `/etc/nginx/sites-available/expenses.micutu.com`:

```nginx
# ── Cloudflare real client IP ────────────────────────────────────────────────
# Restore the visitor IP from CF-Connecting-IP so logs + rate limiting are
# correct. Keep this list updated from https://www.cloudflare.com/ips/
# (or include /etc/nginx/cloudflare-ips.conf). IMPORTANT: also firewall the
# origin so it ONLY accepts traffic from Cloudflare — otherwise CF-Connecting-IP
# can be spoofed by hitting the origin directly.
set_real_ip_from 173.245.48.0/20;
set_real_ip_from 103.21.244.0/22;
# … (restul intervalelor Cloudflare)
real_ip_header CF-Connecting-IP;

server {
    listen 80;
    server_name expenses.micutu.com;

    # Note: the API port is 8098 in docker-compose (was 8080). Match yours.
    set $api http://127.0.0.1:8098;

    # Security headers for the static SPA (the API sets its own).
    add_header X-Frame-Options "DENY" always;
    add_header X-Content-Type-Options "nosniff" always;
    add_header Referrer-Policy "no-referrer" always;
    add_header Strict-Transport-Security "max-age=31536000; includeSubDomains" always;

    # Frontend statice
    location / {
        root /calea/catre/expenseTracker_cs/frontend_dist;
        index index.html;
        try_files $uri $uri/ /index.html;
    }

    # API Proxy
    location /api/ {
        proxy_pass $api/api/;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header CF-Connecting-IP $http_cf_connecting_ip;
    }

    # Health check
    location /health {
        proxy_pass $api/health;
    }
}
```

> CSP-ul SPA-ului e livrat printr-un `<meta http-equiv="Content-Security-Policy">`
> injectat la build (vezi `vite.config.js`), deci nu trebuie configurat în Nginx.

Activează site-ul și instalează SSL:

```bash
sudo ln -s /etc/nginx/sites-available/expenses.micutu.com /etc/nginx/sites-enabled/
sudo nginx -t && sudo systemctl reload nginx
sudo certbot --nginx -d expenses.micutu.com
```

---

## 6. Update aplicație

```bash
git pull
docker compose build
docker compose run --rm migrate   # dacă există migrații noi
docker compose up -d ui-builder
docker compose up -d api
```

---

## 7. Logs

```bash
docker compose logs -f api     # Log-uri API (Serilog)
docker compose logs -f db      # Log-uri PostgreSQL
sudo tail -f /var/log/nginx/access.log # Log-uri Nginx host
```
