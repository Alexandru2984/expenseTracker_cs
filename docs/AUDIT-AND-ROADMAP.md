# Audit și plan de produs — 15 noiembrie 2026

Continuarea privind emailul în așteptare, cooldown-ul persistent și revocarea
sesiunilor este documentată în [ACCOUNT-SECURITY.md](ACCOUNT-SECURITY.md).

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

- Verificare operațională: a doua lansare s-a oprit înainte de mutații deoarece
  `pg_isready` observa serverul temporar de initdb, apoi `pg_restore` întâlnea
  oprirea lui. Reprodus în container separat; readiness/restore folosesc acum TCP
  loopback, disponibil numai pe serverul final. Backupul și rollbackul rămân obligatorii.

- Etapa 4: dashboard cu indicatori activ/suspendat/scadent, agregări pe întregul
  cont, prognoză pe 6 luni cu plățile anuale în luna scadentă, următoarele 30 de
  zile și scadențe vechi separate. Calculul păstrează ziua de ancoră în proiecție
  și gestionează anii bisecți; nu înregistrează automat plăți sau schimbări de dată.
  Calendarul ICS exportă o singură scadență salvată per abonament activ, cu escaping
  și împărțirea liniilor după octeți UTF-8 conform RFC 5545. Simulatorul de economii
  păstrează valutele separate și nu modifică datele. Suspendare/reactivare
  idempotentă, duplicare prin formular cu confirmarea utilizatorului, listă/revocare
  a sesiunilor proprii fără secrete expuse. Contract Finance schemaVersion 1 păstrat;
  suspendarea este vizibilă în snapshot prin isActive.
  Cursurile afișează explicit sursa live/fallback, rezultatele sunt validate și
  cache-uite inclusiv la eroare; valutele fără curs sunt excluse și semnalate.
  Filtrare cu debounce și protecție la răspunsuri întârziate, resetarea datelor la
  logout, formulare păstrate la eroare și protecție la dublu submit. Web Locks
  coordonează refresh-ul între taburi când browserul le oferă; fallbackul oferă
  deduplicare în același tab. Focus în dialog, Escape, revenire focus și contrast
  corectate. 121 verificări browser la 1440/390/320 px în light/dark, inclusiv WCAG
  automat, concurență între două taburi și zero erori JavaScript în scenariile testate.

Referință calendar: [RFC 5545](https://www.rfc-editor.org/rfc/rfc5545).

- Backup manual: pipeline cu pipefail, arhive publicate atomic, director 0700 și
  fișiere 0600, credențiale rezolvate în container și excludere din Git. Restore cu
  verificarea gzip, oprire la orice eroare SQL și tranzacție unică. Probă pe
  PostgreSQL izolat: restaurare completă, rollback la SQL invalid, pg_dump eșuat
  fără arhivă incompletă și fără mesaj fals de succes. Backupurile noi includ
  clean/if-exists; un backup vechi fără aceste instrucțiuni poate necesita o bază
  goală și va eșua sigur dacă obiectele există deja.

- Coduri și credențiale: codurile numerice sunt acum protejate cu HMAC, legat de
  cont, adresă și scop; hashul SHA-256 simplu al unui cod cu doar un milion de
  posibilități permitea ghicire offline în cazul unei scurgeri a bazei. Codurile
  vechi trebuie retrimise după lansare (nu sunt acceptate prin fallback). Verificarea
  BCrypt respinge parole care depășesc 72 de octeți UTF-8, inclusiv la login și
  reautentificare. Inputurile auth sunt limitate și codurile acceptă numai cifre.
  Emailul, verificarea și parola au protecție la modificări concurente pe coloane
  existente; un conflict returnează 409 fără detalii interne. 92 teste backend trecute.

## Puncte deschise după această rundă

- Backoff-ul persistent și indexul unic case-insensitive sunt implementate în
  continuarea [LOGIN-PROTECTION.md](LOGIN-PROTECTION.md). Pauzele limitate reduc,
  dar nu elimină blocarea abuzivă; MFA și alertarea externă rămân deschise.
- MFA/passkeys, audit de acces cu retenție explicită și recuperare cont.
- Backup off-site criptat și probă periodică de restaurare, verificarea alertelor
  SMTP, quotas și SLO; probele din această rundă nu înlocuiesc operarea continuă.

## Etapa finală: CI, SEO și lansare verificabilă

- 93 teste backend pe PostgreSQL temporar, inclusiv concurență și verificarea că
  modelul EF corespunde snapshotului fără migrație de schemă. 157 verificări browser
  la 1440/390/320 px, light/dark, inclusiv formulare login/înregistrare și WCAG;
  suplimentar 24 verificări ale conectorului Finance. npm/NuGet fără advisories în
  sursele disponibile la verificare. Aceste rezultate sunt locale; workflow-ul
  GitHub a fost actualizat, nu executat pe GitHub în această sesiune.
- CI folosește PostgreSQL separat, audit NuGet tranzitiv cu advisories tratate ca
  erori, audit npm și ambele suite browser. Acțiunile sunt fixate la SHA complet.
  Build-ul Docker UI folosește npm ci; containerul de distribuție trece de la
  Alpine 3.19 la o ramură 3.24 suportată, conform
  [calendarului Alpine](https://alpinelinux.org/releases/). Nu s-a efectuat o scanare
  exhaustivă CVE a tuturor imaginilor sau serviciilor hostului.
- Canonical/social metadata, sitemap numai pentru pagina publică și robots pentru
  domeniul corect. Dashboardul autentificat setează noindex; API rămâne no-store și
  noindex. Regulile shared ale altor domenii nu sunt modificate.
- Politici nginx distincte pentru SPA/API, fără headere contradictorii și fără
  unsafe-inline în script-src. Politica din meta este aliniată cu domeniile de
  analytics existente. Verificare idempotentă și nginx -t înainte de lansare;
  verificări HTTP în procedura de deploy și rollback automat la eșec.
- API și UI înregistrează commitul sursă. Deploy-ul respinge surse necomise,
  imagini sau frontend construite din alt commit; API-ul este activat prin ID-ul
  imuabil al imaginii. Configurația și datele vechi sunt păstrate privat pentru
  rollback. Dovezile activării în producție se înregistrează după lansare.

## Compatibilitate Cloudflare verificată în browser

Verificarea publică a primei lansări complete a identificat JavaScript Detections
injectat de Cloudflare și blocat de CSP, plus frame-ancestors ignorat într-un meta
tag. CSP este acum emis exclusiv în HTTP de nginx, cu nonce aleator per cerere
(`$request_id`); Cloudflare poate aplica nonce-ul scripturilor sale. HTML folosește
no-store, iar asseturile cu nume hashuit au cache immutable separat. Politica API
rămâne default-src none. Nu se adaugă unsafe-inline în script-src și nu se schimbă
regulile Bot Fight Mode ale zonei. Referință:
[Cloudflare JavaScript Detections și CSP](https://developers.cloudflare.com/cloudflare-challenges/challenge-types/javascript-detections/).

## Lansare precedentă verificată

Cod activ: `9b6e954`, imagine
`expensetracker_cs-api:release-9b6e95490effbc4e1030cf49bfe0e1b3f6d8836b`.
Backup și rollback private:
`/var/backups/expense-tracker/deploy-20261115T133837Z`. Restore drill trecut înainte
de activare. Nicio migrație, niciun restart PostgreSQL; API healthy, zero restarturi.
`ops/nginx-expenses.conf` reflectă configurația aplicată.

Verificări publice: health 200; insights/calendar/sessions/export Finance anonime
401, cu no-store și un singur set de headere de protecție. Snapshot Finance accesibil
prin .NET HttpClient până la verificarea autorizării (401 fără token), fără schimbarea
contractului. Pagina publică în Chromium la 390 px: fără overflow, erori JavaScript
sau erori CSP după rezolvarea nonce-ului Cloudflare. Nonce diferit pentru două
cereri HTML, HTML no-store și asseturi hash-uite public/immutable. Sitemap public
conține numai pagina publică a domeniului corect.

**Propagare CDN încă în curs:** `/robots.txt` fără query este încă un HIT Cloudflare
al variantei vechi, cu TTL 14400 secunde. Originul și o cerere nouă cu query de
release furnizează deja regulile corecte. Cache-ul existent necesită expirare sau
purge în contul Cloudflare; nu s-a găsit un conector Cloudflare disponibil pentru
această operație și nu s-au folosit credențiale din alte servicii ale VPS-ului.
Regulile Bot Fight Mode nu au fost dezactivate pentru a trece probele automate.
Unele cliente automate (de exemplu urllib cu setările implicite) primesc 403 la
edge; curl, browserul și proba .NET au primit răspunsurile așteptate.

Cele 157 verificări UI și cele 93 teste backend sunt pentru scenariile din suite,
nu o garanție de absență a tuturor defectelor. CI rămâne de executat și pe GitHub
după publicarea commiturilor. Lista prioritizată de mai sus rămâne planul următor.

## Continuare: conturi și bugete

Emailul în așteptare, cooldown-ul persistent și revocarea corectă a refresh-urilor
sunt documentate în [ACCOUNT-SECURITY.md](ACCOUNT-SECURITY.md). Bugetele persistente,
calculele și protecția la editări concurente sunt în [BUDGETS.md](BUDGETS.md).
115 teste backend trec pe PostgreSQL inițializat prin migrațiile reale, fără skip.
259 verificări browser trec la 1440/390/320 px în light/dark, inclusiv bugete,
persistență după reload, conflicte de editare, confirmare/anulare email și WCAG.
Lansarea aplică migrațiile numai după verificarea pe backupul restaurat și probe
de compatibilitate cu imaginile API veche și nouă.

## Lansare conturi și bugete verificată

Cod activ: `270ec04ed2b89cd2a6a87267a3e39f3423f4d626`, imagine
`expensetracker_cs-api:release-270ec04ed2b89cd2a6a87267a3e39f3423f4d626`.
Backup și rollback private:
`/var/backups/expense-tracker/deploy-20261115T174304Z`.

Backupul a fost restaurat în izolare. Bundle-ul candidat a aplicat cele două
migrații aditive și a rulat încă o dată fără schimbări. API-ul vechi și cel nou au
citit schema extinsă; numărul de conturi și abonamente a rămas identic (0/0).
Istoricul bazei active confirmă toate cele 8 migrații. Serviciul PostgreSQL nu a
fost restartat; API-ul nou este healthy, cu zero restarturi.
`ops/nginx-expenses.conf` reflectă configurația activă și release-ul UI imuabil.

Verificările publice confirmă health 200 și cont/bugete/export Finance anonime
401, toate cu no-store și un singur X-Frame-Options: DENY. Revizia publicată de UI
corespunde imaginii API. Chromium la 390 px nu raportează overflow, erori JavaScript
sau CSP. Proba F# cu .NET HttpClient ajunge la verificarea autorizării exportului
Finance (401 fără token); contractul rămâne schemaVersion 1. Nu s-au creat conturi
și nu s-au trimis emailuri de test în producție.

Problema de propagare CDN de la lansarea precedentă este rezolvată: cererea publică
fără query la robots.txt include acum regulile aplicației și sitemap-ul domeniului
corect, alături de conținutul gestionat de Cloudflare. Nu s-a efectuat un purge și
nu s-au modificat regulile Bot Fight Mode.

Validare înainte de lansare: 115 teste backend pe PostgreSQL inițializat prin
migrații, fără skip; 259 verificări dashboard la 1440/390/320 px în light/dark;
24 verificări UI Finance. Acestea acoperă scenariile implementate, nu certifică
absența tuturor vulnerabilităților. CI de pe GitHub rămâne de executat după
publicarea commiturilor; nu s-a făcut push.

## Continuare: login distribuit și evoluția prețurilor

- [LOGIN-PROTECTION.md](LOGIN-PROTECTION.md): backoff persistent pe cont, pauze
  fără prelungire la trafic, recuperare prin cod, sesiuni existente păstrate.
  Username unic case-insensitive în PostgreSQL, căutare cu aceeași mapare în DB
  și răspuns 409 la cursa dintre înregistrări. Fără combinarea conturilor vechi.
- [PRICE-HISTORY.md](PRICE-HISTORY.md): evenimente de cost/valută/frecvență atomice,
  diferențe lunare și procente numai când sunt comparabile, dialog responsive
  cu paginare/erori/tastatură și protecție împotriva formularelor rămase vechi.
  Snapshotul Finance existent rămâne schemaVersion 1; istoricul este o resursă
  opțională separată pentru consumatorul F#.
- [Review și runbook](SECURITY-REVIEW-LOGIN-HISTORY.md): top amenințări, impact,
  detecție disponibilă, lipsa alertării externe și procedura de recuperare.
  Exercițiul tehnic pe conturi sintetice include blocarea, concurența și recuperarea.
- 136 teste backend trecute pe PostgreSQL temporar, fără skip, pornit prin
  migrațiile reale. Include verificarea snapshotului EF, editări concurente cu
  rollback al evenimentului pierzător și ștergere în cascadă a istoricului.
- Browser: 113 aserțiuni trecute pentru fiecare rezoluție 1440/390/320 px, în
  light/dark, în trei loturi (339 execuții de aserțiuni; proba de refresh între
  taburi se repetă în fiecare lot). Suplimentar 24 verificări ale panoului Finance.
  O rulare monolitică întreruptă nu este contabilizată drept succes.

Următoarele funcții de produs prioritizate: identificarea duplicatelor pe întregul
cont cu revizuire manuală; note/link furnizor și perioade de probă; obiective de
economisire persistente; outbox SMTP și notificări cu opt-in/fus orar. MFA/passkeys,
alertarea externă și backupul off-site rămân priorități de securitate/operare.
Aceste puncte sunt planificate, nu declarate implementate.

## Lansare login și istoric verificată

Cod activ: `ca3d806478a45225a31e08932a794236a306a028`, imagine
`expensetracker_cs-api:release-ca3d806478a45225a31e08932a794236a306a028`.
Backupul și rollback-ul sunt private în
`/var/backups/expense-tracker/deploy-20261115T182954Z`.

Restaurare izolată reușită, 8 → 10 migrații, bundle repetat fără schimbări, API vechi
și nou compatibile cu schema extinsă. Numărul de conturi/abonamente s-a păstrat în
probă (0/0 la snapshotul backupului). Migrațiile sunt aditive: trei coloane Users,
indexul unic case-insensitive și tabela SubscriptionPriceChanges cu index/FK.
Nu s-au rescris prețuri, reconstruit evenimente istorice sau redenumit conturi.
Istoricul din baza activă confirmă toate cele 10 migrații. StartedAt al containerului
PostgreSQL este identic înainte/după lansare. API healthy, zero restarturi.

Verificările prin domeniul public: health 200; bugete, istoric propriu și export/
istoric Finance anonime 401, cu no-store și un singur X-Frame-Options: DENY. Revizia
UI coincide cu imaginea; CSP nonce diferit pe două răspunsuri HTML. Chromium la
390 px nu arată overflow, erori JavaScript sau CSP. Proba F# HttpClient confirmă
401/no-store pentru snapshotul existent și noua resursă de istoric, fără token.
Scenariile autentificate au folosit date sintetice pe PostgreSQL temporar și API
sintetic în browser; nu s-au creat conturi sau trimis emailuri de test în producție.

Validare finală: 136 teste backend fără skip, 339 execuții de aserțiuni dashboard
în trei loturi desktop/mobil light/dark și 24 verificări Finance pe buildul final.
Configurația nginx activă este reflectată în ops. Sursa și artefactele au fost
construite din același commit; documentarea ulterioară nu schimbă codul activ.
Nu s-a făcut push și nu se declară o rulare CI remote.

## Continuare: revizuire, probe și portabilitate

- [Duplicate](DUPLICATES.md): analiză pe întregul cont, nume normalizat și valută,
  includerea opțională a suspendatelor, revizuire prin înregistrarea actuală.
  Nicio ștergere/combinare automată și nicio economie presupusă.
- [Detalii și probe](SUBSCRIPTION-DETAILS.md): note text, link HTTP/HTTPS validat,
  probă cu scadență coerentă inclusiv în DB, afișare pe mobil și revizuire.
  Notele și URL-urile rămân în afara Finance, insights, duplicate și calendar.
- [Portabilitate CSV](CSV-PORTABILITY.md): remedierea pierderii planurilor cu nume
  identic, ID sursă per cont, formatare reversibilă sigură pentru spreadsheet,
  reimport idempotent, export pe pagină și fișiere de maximum 1 MB.
- Quota de 5000 este verificată tranzacțional pentru creare/import. Agregările
  citesc numai câmpurile financiare necesare, fără notele și URL-urile lungi.
  Nu s-au introdus dependențe sau furnizori noi și nu se trimit emailuri automate.

Regresii CSV reproduse înainte de fix: 3 eșecuri pe date sintetice. Verificarea
completă ulterioară: 177 teste backend pe PostgreSQL prin migrații, fără skip.
Matricea browser și activarea în producție se consemnează după verificarea finală.

Matricea browser a trecut în trei loturi 1440/390/320 px, light/dark: 145 aserțiuni
per lot (435 execuții, inclusiv repetarea probei dintre taburi), plus 24 verificări
Finance. Include formulare cu note, HTML afișat ca text, linkuri, probe, duplicate,
export pe pagină, conflicte și WCAG. Activarea se consemnează după lansare.

Următorul plan de produs: obiective de economisire persistente; notificări cu
outbox SMTP, opt-in și fus orar; portabilitatea/ștergerea contului. MFA/passkeys,
alertarea externă și backupul off-site rămân deschise. Nu sunt declarate livrate.

## Continuare: răspunsuri întârziate în browser

[BROWSER-SESSION-BOUNDARIES.md](BROWSER-SESSION-BOUNDARIES.md) documentează o regresie
reprodusă: un export autorizat înainte de logout se descărca ulterior. Exporturile
CSV/calendar și clientul API verifică acum contextul local; un 401 vechi nu reia
o mutație după schimbarea sesiunii. Matricea finală include trei probe suplimentare
pentru aceste cazuri. Coordonarea completă login/logout/cookie-uri între taburi
rămâne prioritate de securitate, alături de MFA și alertare.

## Persistența release-ului în Docker Compose

La reluarea lucrului s-a observat API healthy din tagul implicit
`expensetracker_cs-api`, fără etichetă de revizie; containerele API și DB fuseseră
recreate la 15.11.2026 19:56 UTC, iar UI-ul public rămânea la `ca3d806`.
Nu s-a atribuit această recreare sesiunii de lucru și nu s-a presupus ce cod
conține imaginea neidentificată. Istoricul bazei avea încă 10 migrații.

Cauza operațională remediată: override-ul de imagine era folosit numai în comanda
de activare, fără un pin persistent pentru următorul `docker compose up` obișnuit.
Deploymentul publică acum atomic override-ul implicit cu image ID, fără variabile
noi în `.env` și fără schimbarea modelului DB. Nu suprascrie override-uri personale.
Reverifică starea/sursa după restore drill și restaurează pin-ul la rollback.
Patru teste locale, inclusiv încărcarea reală a configurației Compose fără `-f`,
verifică pin-ul, configurația DB identică și refuzul referințelor mutabile,
override-urilor străine și symlink-urilor.

## Lansare produs și persistență verificată — 16 noiembrie

Cod activ: `edc79a16354d2abe796828971cb326259ed30021`, imagine
`expensetracker_cs-api:release-edc79a16354d2abe796828971cb326259ed30021`.
Backup/rollback privat:
`/var/backups/expense-tracker/deploy-20261115T232430Z` (timestamp UTC).

Backupul a fost restaurat izolat; 10 → 12 migrații, repetare idempotentă și probe
de citire cu API-ul vechi și cel nou. Numărul de conturi/abonamente s-a păstrat
în probă (0/0 la snapshot). Cele două migrații adaugă detaliile/proba, constraintul
de scadență și identitatea de import/indexul per cont, fără rescrierea prețurilor
sau evenimentelor. Istoricul bazei active confirmă toate cele 12 migrații.
ID-ul și StartedAt ale containerului PostgreSQL sunt identice înainte/după această
lansare; recrearea observată la 19:56 UTC a fost anterioară. API healthy, zero
restarturi. Configurația nginx activă este reflectată în ops.

Override-ul implicit Compose este root-owned și fixează image ID-ul activ.
Configurația implicită, fără `-f`, rezolvă exact aceeași imagine. Comparația
mediului DB nu arată drift; dry-run-ul pentru API raportează containerul Running,
fără recreare. Nu s-a rulat un `compose up` general sau un restart DB pentru probă.
Cele 4 teste de pin sunt incluse și în workflow-ul CI; nu s-a făcut push sau rulat
workflow-ul remote.

Verificări publice: health 200; abonamente/duplicate/export/bugete/Finance anonime
401, cu no-store și un singur X-Frame-Options: DENY. Revizia UI corespunde imaginii
API. Nonce CSP diferit între răspunsuri HTML. Chromium la 390 px nu arată overflow,
erori JavaScript sau CSP. F# HttpClient confirmă 401/no-store pentru snapshot și
istoric fără token; contractul Finance rămâne schemaVersion 1 și exclude notele/
URL-urile personale.

Validare: 177 teste backend pe PostgreSQL prin migrații, fără skip; 444 execuții de
aserțiuni browser în trei loturi desktop/mobil light/dark; 24 verificări Finance
pe buildul final; 4 teste de deployment. Probele autentificate folosesc numai
date sintetice; nu s-au creat conturi sau trimis emailuri de test în producție.
Documentarea ulterioară nu schimbă revizia de cod activă.

## Continuare: identitatea contului între taburi

[CROSS-TAB-SESSIONS.md](CROSS-TAB-SESSIONS.md) documentează protecția nouă pentru
formular A trimis cu cookie-urile contului B: context opac legat de identitate,
409 fără mutație, inclusiv pentru logout/refresh cu access expirat. Contractul
Finance F# rămâne neschimbat; nu sunt necesare migrații.

SPA coordonează login/logout/refresh și operațiile de credențiale în același Web
Lock. Markerii dintre taburi nu conțin date de identitate sau credențiale.
Schimbarea externă șterge formularele/datele locale și respinge răspunsurile vechi.
Sunt acoperite fallbackurile storage/channel, notificările pierdute, un writer
închis, ordinea refresh/login, sesiuni expirate/revocate și salvarea întârziată.
Headerul opțional păstrează clienții existenți compatibili; limitele acestora și
ale browserelor fără Web Locks sunt explicate explicit în document.

Validare locală: 189 teste backend pe PostgreSQL prin migrații, fără skip; 53
aserțiuni dedicate sesiunilor între taburi; 444 execuții de aserțiuni dashboard
la 1440/390/320 px în light/dark; 24 verificări ale panoului Finance.
Activarea și verificările publice se consemnează după lansare. Testele folosesc
exclusiv conturi/API sintetice; nu trimit emailuri în producție.

Următoarea etapă de produs: obiective de economisire persistente, apoi notificări
opt-in cu outbox și fus orar. MFA/passkeys, backup off-site și alertare externă
rămân priorități deschise, alături de portabilitatea/ștergerea contului. Acestea
necesită implementare și validare separate; nu sunt declarate livrate.
