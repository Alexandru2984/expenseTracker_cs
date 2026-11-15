# Audit și plan de produs — 15 noiembrie 2026

## Domeniu și metodă

Baseline: `34c92ce`, ASP.NET Core 10 / EF Core / PostgreSQL și Vue 3 / Vite.
Analiză statică, teste pe date sintetice, audituri NuGet/npm și verificări HTTP
nedistructive. Niciun test de preluare a conturilor, brute force sau import pe
datele utilizatorilor reali. Configurațiile cu secrete nu sunt incluse în raport.
Acesta este un audit al aplicației și al traseului ei de deployment; nu certifică
securitatea tuturor serviciilor VPS-ului, Cloudflare sau a aplicației F#.

## Constatări inițiale

| ID | Prioritate | Problemă și impact | Remediere / verificare |
| --- | --- | --- | --- |
| AUTH-01 | Critică | `verify-email` emite o sesiune pentru un username deja verificat, fără validarea codului. Posibilă preluare a contului cunoscând username-ul. | Respinge conturile deja verificate; cod valid, neexpirat, consumat o singură dată. Reprodus cu 3 teste care eșuează înaintea fixului, exclusiv pe conturi sintetice. |
| AUTH-02 | Ridicată | JWT-urile existente rămân valide după logout global / resetare parolă, până la expirare. | Leagă access token de sesiunea revocabilă din DB și starea credentialelor; testează invalidarea imediată. |
| AUTH-03 | Ridicată | Schimbarea emailului nu cere parola; codurile vechi de resetare/verificare nu sunt invalidate la schimbarea emailului/parolei. | Reautentificare, invalidarea codurilor și sesiunilor, teste pentru coduri vechi. |
| AUTH-04 | Ridicată | Refresh și consumarea codurilor folosesc read-modify-write fără protecție la cereri concurente. | Concurență optimistă pe câmpurile existente; verificare și pe PostgreSQL izolat. |
| CSRF-01 | Medie | Toate rutele auth sunt exceptate de la CSRF; SameSite nu izolează subdomeniile aceluiași domeniu. | Verifică Origin și Fetch Metadata pentru cereri mutante, inclusiv auth; păstrează double-submit pentru operațiile autentificate. |
| DATA-01 | Medie | Importul CSV acceptă enum-uri numerice nedefinite, limitează artificial costuri greșite și trunchiază câmpuri fără avertizare. DTO-urile permit enum invalid / dată omisă. | Validare comună explicită, limite și erori inteligibile, fără alterarea tăcută a sumelor. |
| DATA-02 | Corectitudine | Graficele pe categorii folosesc doar pagina curentă; scadențele includ toate datele din trecut; valuta necunoscută se convertește ca RON. | Agregări pe întregul cont, separare restanțe/scadențe, conversie numai cu rată cunoscută. |
| UI-01 | Produs | Filtrele pot suprascrie rezultate noi cu răspunsuri întârziate; resetarea sesiunii nu golește toate datele UI; formulare fără protecție la dublu submit. | Anulare cereri / versiuni de request, reset complet, stări busy și feedback. |
| OPS-01 | Operațional | SDK-ul implicit al hostului este .NET 8, proiectul cere .NET 10; documentația istorică descrie mecanisme eliminate. | Folosește SDK 10 disponibil separat; actualizează instrucțiunile fără modificarea runtime-urilor altor servicii. |
| OPS-02 | Operațional | Fallback SMTP scrie codurile în log dacă SMTP lipsește. | Restricționează explicit fallback-ul la Development/Testing; producția trebuie să eșueze închis. |

Protecții deja prezente: cookie-uri HttpOnly/SameSite, parole BCrypt, refresh hashuit,
CSRF pentru API-ul autentificat, ownership la CRUD/export, tokenuri F# cu namespace
distinct, limitare per IP normalizat, port API loopback, Swagger numai development,
headere no-store, backup verificat prin restaurare înainte de deployment și rollback.
Prezența acestor controale nu compensează ocolirea autentificării din AUTH-01.

## Etape și criterii de acceptare

1. **Închidere acces neautorizat:** AUTH-01, teste de regresie, commit separat și
   lansare prin backup/restore drill/health check. Fără migrații și fără restart DB.
2. **Sesiuni și recuperare cont:** AUTH-02–04, CSRF-01, OPS-02; autentificare,
   refresh, logout, schimbare email/parolă și F# verificate împreună.
3. **Date financiare corecte:** DATA-01–02, import cu raport explicit și validare;
   contractul Finance `schemaVersion: 1` și tipurile Monthly/Yearly rămân compatibile.
4. **Dashboard util pe mobil:** calendar de plăți, restanțe distincte, previziune pe
   luni și categorii pentru întregul cont, suspendare/reactivare și avansarea
   scadenței la confirmarea reînnoirii. Stări goale, erori utile, accesibilitate,
   tastatură, dark/light, fără overflow la 320/390/1440 px.
5. **Pregătire de produs:** bugete per valută/categorie persistente, obiective de
   economisire, detectare duplicate, istoric modificări de preț, notificări email
   cu opt-in și fus orar, perioade de probă, note/link furnizor, căutare avansată,
   export calendar ICS și portabilitate/ștergere cont. Fiecare are propriul contract,
   migrație/rollback unde este nevoie, teste și commit. Nu se declară implementată
   o funcție doar pentru că apare în acest plan.
6. **Extindere F#:** preview înainte de sincronizare, diagnostic de conectare,
   indicator ultima sincronizare, alerte variații de preț și prognoze pe categorii.
   Orice contract nou se versionează; tokenurile rămân read-only, revocabile și
   separate de sesiunile utilizatorului. Modificările aplicației F# se validează
   într-un mediu izolat înainte de lansare.
7. **La final: CI și SEO:** CI cu PostgreSQL real, teste browser desktop/mobil,
   audit accesibilitate și dependențe, verificare migrații, release reproductibil;
   pagină publică de prezentare, metadata/social cards, sitemap numai pentru pagini
   publice, robots/noindex pentru date personale. Fără indexarea dashboardului.

Un produs finit mai necesită decizii privind retenția datelor, recuperarea conturilor,
livrarea emailurilor, monitorizare și obiective de disponibilitate. Se păstrează o
listă explicită a punctelor neterminate în locul unei promisiuni de „zero vulnerabilități”.

## Referințe

- [OWASP Authentication](https://cheatsheetseries.owasp.org/cheatsheets/Authentication_Cheat_Sheet.html)
- [OWASP Session Management](https://cheatsheetseries.owasp.org/cheatsheets/Session_Management_Cheat_Sheet.html)
- [OWASP CSRF](https://cheatsheetseries.owasp.org/cheatsheets/Cross-Site_Request_Forgery_Prevention_Cheat_Sheet.html)
- [EF Core: concurrency](https://learn.microsoft.com/en-us/ef/core/saving/concurrency)

## Dovezi și starea lansărilor

Rezultatele finale și commiturile fiecărei etape se adaugă aici după verificare.

- Etapa 1, `8d6a9d8`: 50 teste backend; 3 regresii reproduse înainte de fix.
  Lansată imaginea `expensetracker_cs-api:auth-fix-stage1`; backup restaurat în
  izolare, health public 200, export Finance anonim 401/no-store, zero restarturi.
- Etapa 2: access token legat de un RefreshToken activ și de un HMAC al stării
  credentialelor; logout individual/global, rotație și schimbarea parolei invalidează
  imediat JWT-ul vechi. Schimbarea emailului cere parola, invalidează coduri/sesiuni
  și oferă o sesiune curentă pentru confirmare. Codurile vechi sunt invalidate și
  la schimbare/resetare parolă. Concurență optimistă pe coloane existente, fără DDL;
  snapshot EF actualizat pentru metadatele de concurență, fără migrație necesară.
  Origin/Fetch Metadata verificat inclusiv pe auth; CORS cu credențiale doar pentru
  originile configurate. SMTP obligatoriu în Production; codurile se loghează numai
  în Development. Limită corp HTTP 1,1 MB și eliminarea antetului Kestrel Server.
  59 teste trecute în memorie și pe PostgreSQL izolat, build UI și 24 verificări ale
  conectorului la 1440/390/320 px. Fără schimbarea contractului Finance.

La prima folosire după etapa 2, JWT-urile vechi fără identificator de sesiune sunt
respinse; un refresh legitim activ poate reface sesiunea. Nu există dovadă de
exploatare din acest audit. Acest lucru nu dovedește absența exploatării istorice;
o investigație a jurnalelor și revocarea sesiunilor existente rămân acțiuni distincte.
Pe baza de producție verificată la prima lansare erau zero conturi și abonamente;
testele au folosit exclusiv conturi sintetice în baze temporare.

- Etapa 3: DTO-uri comune pentru creare/editare/import, normalizare valută și spații,
  cost cu maximum două zecimale, enum-uri definite, dată obligatorie, caractere de
  control respinse. CSV cu ghilimele valide, limite explicite de rânduri/coloane,
  antete duplicate respinse, BOM acceptat și raport `imported/skipped/rejected` cu
  primele 10 erori. Valorile invalide nu sunt ajustate automat. Crearea respectă
  IsActive; DueThisWeek nu mai include restanțele. Protecția la formule CSV acoperă
  și newline sau whitespace înaintea formulei.
