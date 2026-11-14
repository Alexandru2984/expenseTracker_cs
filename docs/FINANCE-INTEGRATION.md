# Integrare Finance F#

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

F# își protejează copia tokenului prin DataProtection. Deconectarea în F# șterge credentialul
și snapshotul local; revocă tokenul și aici pentru a invalida orice copie rămasă.

Runtime-ul API și dependențele Microsoft au fost actualizate la .NET 10 LTS. Bundle-ul EF
folosește linux-musl-x64 pentru runtime-ul Alpine, cu factory de tooling care nu necesită
secrete la build. Conexiunea de migrare este citită din environment, nu din argumente CLI.
