# Schimbarea emailului și limitarea codurilor

Continuare 15.11.2026. Adresa curentă rămâne verificată și utilizabilă până la
confirmarea celei noi. O cerere greșită poate fi anulată fără pierderea accesului.

## Contract

- `POST /api/account/change-email`: `{ email, currentPassword }`. Creează o cerere
  și trimite un cod la noua adresă. Nu modifică emailul curent, sesiunile sau Finance.
- `POST /api/account/confirm-email-change`: `{ code }`, șase cifre. Necesită sesiune
  și CSRF. Confirmă adresa, invalidează codurile vechi, revocă sesiunile și tokenurile
  Finance și creează o sesiune nouă pentru dispozitivul curent.
- `DELETE /api/account/pending-email`: anulează cererea și invalidează codul ei.
- `GET /api/account` include `pendingEmail`, `pendingEmailExpiresAt` și
  `emailRetryAfterSeconds`. Nu expune coduri, MAC-uri sau alte credențiale.

Codul pentru schimbare are scop separat de înregistrare și resetare parolă. MAC-ul
îl leagă de cont, adresa curentă și noua adresă. Este valabil 15 minute, cu maximum
5 încercări. Confirmarea folosește o tranzacție: un email ocupat între timp produce
409 și păstrează adresa inițială. Încercările greșite sunt păstrate în DB.

## Cooldown persistent

Un cont poate solicita un email de verificare cel mult o dată la 60 secunde,
indiferent de scop, IP sau instanța API. Un lock pe rândul utilizatorului și o
tranzacție serializează rezervarea intervalului; trimiterea SMTP are loc după
commit, în afara lock-ului. O cerere limitată nu înlocuiește codul ori adresa în
așteptare. Erorile SMTP păstrează intervalul rezervat pentru a limita reîncercările.

Ruta autentificată de schimbare răspunde 429 cu Retry-After. Rutele anonime de
recuperare/retrimitere păstrează aceeași formă și același mesaj pentru un cont
existent, necunoscut sau limitat. Acest lucru nu elimină toate diferențele de timp
ale SMTP; un outbox rămâne o îmbunătățire ulterioară.

## Sesiuni și concurență

Operațiile relevante obțin mai întâi lock-ul utilizatorului, apoi lock-urile pe
coduri/tokenuri. Emiterea unei sesiuni revalidează starea credentialelor după lock,
pentru a respinge o parolă devenită veche în timpul cererii. Cookie-urile noi sunt
scrise după commit.

`WasRotated` separă rotația de revocarea intenționată. Refolosirea unui token revocat
prin logout, schimbare/resetare parolă sau confirmare email nu mai revocă sesiunile
nou-create. Refolosirea unei rotații active continuă să invalideze sesiunile. Rotațiile
anterioare migrației nu sunt reconstruite retrospectiv; tokenurile lor deja revocate
rămân inutilizabile.

Contractul exportului F# rămâne `schemaVersion: 1`. Tokenurile Finance sunt revocate
la confirmarea emailului, schimbarea/resetarea parolei și logout global.

## Migrație și verificare

Migrația adaugă numai `Users.PendingEmail`, `Users.VerificationEmailSentAt` și
`RefreshTokens.WasRotated`. Coloanele sunt nullable sau au default false, iar
versiunea veche a API-ului poate citi schema extinsă. Nu se șterg sau rescriu date.
DDL are lock_timeout 5s și statement_timeout 30s în tranzacția migrației.

107 teste backend au trecut pe PostgreSQL temporar, inclusiv concurență între
contexte, cooldown comun și confirmări concurente ale aceleiași adrese. 211
verificări browser au trecut la 1440/390/320 px în light/dark, inclusiv persistența
cererii după redeschiderea setărilor, countdown, anulare, eroare și confirmare.
Datele din probe sunt sintetice; nicio verificare nu trimite emailuri de test către
utilizatori reali.

Procedura de lansare acceptă explicit `--migrate`. Verifică migrațiile pe backupul
restaurat, repetă bundle-ul, exercită citirea utilizatorilor cu API-ul vechi și cel
nou și verifică păstrarea numărului de utilizatori/abonamente. Un eșec în această
probă oprește lucrul înainte de schimbarea bazei active. Testele PostgreSQL pornesc
schema prin migrațiile reale, nu prin EnsureCreated.
