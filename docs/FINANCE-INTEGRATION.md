# Integrare Finance F#

Actualizare 15.11.2026: contractul rămâne `schemaVersion: 1`, cu aceleași câmpuri și
frecvențe Monthly/Yearly. Suspendarea/reactivarea apare în `isActive`; prognoza din
C# nu modifică scadența și nu creează plăți în F#. Sesiunile JWT sunt acum legate
de rândul revocabil, iar parolele/codurile beneficiază de controalele din
[auditul curent](AUDIT-AND-ROADMAP.md). Suitele de verificare separă contractul API,
ownership/revocarea și interfața browser; nu echivalează cu un test complet al
tuturor funcțiilor aplicației F#.

C# este sursa abonamentelor planificate. F# consumă un snapshot pentru prognoze și alerte,
fără să creeze cheltuieli și fără să modifice abonamentele în C#.

După activarea `FinanceIntegration__Enabled=true`, deschide Setări cont → Conectează Finance,
confirmă parola și creează un token (1–90 zile). Copiază-l în Finance → Integrations. Se
afișează o singură dată, nu este stocat în browser și poate fi revocat individual din C#.
Schimbarea/resetarea parolei și logout global revocă și tokenurile de integrare.

Tokenul are 256 biți aleatorii și prefix `fin_`; DB stochează exclusiv `finance:` + SHA-256
în infrastructura existentă RefreshTokens. Namespace-ul separă tokenurile de sesiunile
obișnuite. Endpointul de refresh nu le poate schimba în sesiuni. Nu este necesară o
migrație a bazei pentru acest conector; cleanupul existent gestionează expirarea/revocarea.

- GET `/api/account/finance-tokens`: metadata tokenurilor proprii, fără valori/hashuri.
- POST aceeași rută: `{ currentPassword, validDays }`; necesită sesiune + CSRF + parolă.
- DELETE `/api/account/finance-tokens/{id}`: revocare doar a unui token propriu de integrare.
- GET `/api/integrations/finance/subscriptions`: `Authorization: Bearer fin_…`, doar citire,
  un singur cont, maximum 5000 abonamente, schemaVersion 1. Cookieurile nu autorizează ruta.

Snapshot: generatedAt, tokenExpiresAt, subscriptions[]. Fiecare abonament are sourceId,
name, cost, currency, billingPeriod (Monthly/Yearly), nextBillingDate, category, isActive,
updatedAt. Fără email, username, UserId sau informații de autentificare. Răspunsurile API
sunt no-store și nu se loghează corpuri/tokenuri.

## Istoric opțional de preț

`GET /api/integrations/finance/subscriptions/{sourceId}/price-history?skip=0&take=20`
folosește același token fin_ read-only. Contract separat, schemaVersion 1:
generatedAt, tokenExpiresAt și history { subscriptionId, current, total, skip, take,
entries }. Structura history și regulile de calcul sunt în [PRICE-HISTORY.md](PRICE-HISTORY.md).
take este limitat la 100. ID-ul unui abonament străin produce 404; cookie-urile,
JWT-urile normale, tokenurile revocate/expirate nu autorizează ruta.

Consumatorul F# poate cere istoricul numai la deschiderea unui abonament sau la
diagnostic; snapshotul și logica existente rămân compatibile. Pentru paginări care
se suprapun peste editări noi, deduplică după ID și reîncarcă de la zero. Endpointul
nu creează plăți și nu permite scriere. În această etapă se extinde API-ul C#;
nu se declară implementată o interfață nouă în aplicația F# separată.

F# își protejează copia tokenului prin DataProtection. Deconectarea în F# șterge credentialul
și snapshotul local; revocă tokenul și aici pentru a invalida orice copie rămasă.

Runtime-ul API și dependențele Microsoft au fost actualizate la .NET 10 LTS. Bundle-ul EF
folosește linux-musl-x64 pentru runtime-ul Alpine, cu factory de tooling care nu necesită
secrete la build. Conexiunea de migrare este citită din environment, nu din argumente CLI.

## Lansare și rollback

`sudo python3 scripts/deploy-finance-integration.py --image IMAGINE_API` activează numai API-ul
și frontendul deja construit. Înainte de orice schimbare face un dump consistent al bazei
active și îl restaurează integral într-un container PostgreSQL separat fără rețea; copiile
și configurația de rollback sunt private în `/var/backups/expense-tracker`.
Frontendul este copiat root-owned în `/opt/expense-tracker/releases/`, iar nginx este verificat
înainte de reload și prin răspunsul efectiv. API-ul vechi este păstrat ca imagine de rollback.
Un eșec restabilește imaginea/configurația, fără restaurarea automată a datelor clientului.
Conectorul inițial nu a necesitat migrații. Extinderea cu istoric necesită migrația
aditivă descrisă în PRICE-HISTORY; folosește `--migrate`, cu restore drill și probe
de compatibilitate, conform [deploy.md](../deploy.md).

Verificare inițială a conectorului: 35 teste backend, 24 verificări UI/WCAG pe desktop/390/320 px, build Vite 8 și
auditurile NuGet/npm fără advisories cunoscute în sursele consultate. CI include testul
panoului pe API sintetic, separat de testele de autentificare reale cu WebApplicationFactory.

Runda cu istoric: 136 teste backend pe PostgreSQL prin migrații, 339 execuții de
aserțiuni dashboard în trei loturi light/dark și 24 verificări ale panoului Finance.
Proba F# HttpClient pe domeniul public confirmă autorizarea obligatorie/no-store
pentru snapshot și istoric. Rezultatele nu implică modificarea aplicației F# externe.
