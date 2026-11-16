# Izolarea mediului PostgreSQL

Constatare verificată la 16.11.2026: containerul DB moștenea 14 variabile ale
aplicației, inclusiv Jwt__Secret și Smtp__Password, prin env_file comun.
S-au inspectat numai numele, fără afișarea valorilor. Compromiterea containerului
DB putea astfel expune și secretele aplicației/SMTP; este o lărgire reală a
impactului, chiar dacă DB nu publică port extern.

Compose declară acum pentru DB numai POSTGRES_DB, POSTGRES_USER și
POSTGRES_PASSWORD. Imaginea PostgreSQL este fixată prin digest la exact imaginea
deja activă, fără upgrade implicit. API-ul continuă să primească configurația
necesară. Eliminarea variabilelor din sursă nu le elimină dintr-un container
existent: este necesară recrearea controlată a serviciului DB.

Scriptul scripts/isolate-database-environment.py:

1. Cere sursă curată/committed și lock-ul privat comun cu deploymentul.
2. Validează imaginea exactă, credențialele DB neschimbate, PGDATA/defaulturile
   imaginii, volumul existent, rețeaua și lipsa unui port publicat.
3. Refuză eliminarea unor variabile necunoscute care ar putea afecta PostgreSQL.
4. Salvează dump și configurația veche în director root-only; restaurează dump-ul
   într-un container fără rețea, înaintea operației active.
5. Reverifică sursa/configurația/containerele pentru schimbări concurente.
6. Recreează numai DB, fără pull/build, păstrând imaginea și volumul cu date.
   Verifică eliminarea efectivă a variabilelor și health-ul real al API-ului.
7. Dacă este necesar, repornește numai API-ul pentru reconectare. La eșec revine
   la configurația privată anterioară a containerului; nu restaurează dump-ul peste
   datele active și nu șterge volumul.

Operația poate produce o scurtă întrerupere a conexiunilor. Verificările sunt
executate înaintea recreării, iar rezultatul și backupul se consemnează în audit.
Backupul vechii configurații conține secrete și trebuie să rămână privat.
Revenirea la configurația veche reintroduce expunerea; este numai recuperare
funcțională și nu se declară remediere.

Șase teste sintetice verifică eliminarea secretelor, idempotency, credențiale/
PGDATA neschimbate și refuzul imaginilor mutable, volumelor/porturilor/configurației
neprevăzute. După remediere, un dry-run Compose general trebuie evaluat fără
recreări neașteptate; nu se rulează down -v și nu se modifică serviciile altor repo-uri.

Activare verificată la 16.11.2026: backup restaurat înainte de recreare,
14 variabile de aplicație eliminate efectiv. Imaginea, numele volumului și volumul
creat anterior containerului original sunt păstrate. API healthy după reconectare.
Backup/rollback: /var/backups/expense-tracker/db-env-20261116T052835Z.
DB a fost recreat intenționat pentru acest fix; nu se declară fără restart pe
întreaga etapă. Deploymenturile ulterioare ale API-ului nu au recreat DB.
