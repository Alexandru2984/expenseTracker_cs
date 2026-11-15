# Istoricul prețurilor

Fiecare abonament are un istoric al modificărilor de cost, valută și frecvență
salvate în aplicație. Butonul „Istoric preț” deschide un dialog accesibil de la
tastatură, cu paginare, reîncărcare, stări goale și recuperare la erori. Dialogul
funcționează în light/dark și pe mobil; închiderea rămâne accesibilă la scroll.

Crearea/importul unui abonament nu inventează un preț anterior. Schimbările de
nume, categorie, scadență sau stare nu creează evenimente de preț. Sunt păstrate
valoarea anterioară, cea nouă și momentul UTC al salvării în tracker, nu momentul
la care furnizorul și-a modificat prețul. Istoricul nu reprezintă plăți efectuate.

Diferența lunară și procentul folosesc echivalente lunare (anual / 12), numai când
valuta rămâne aceeași. Dacă valoarea anterioară este zero, procentul este null.
Schimbarea valutei nu aplică un curs de schimb și nu afișează o comparație procentuală.

## API și corectitudine

`GET /api/subscriptions/{id}/price-history?skip=0&take=20` necesită sesiune proprie.
Răspuns: subscriptionId, current { cost, currency, billingPeriod }, total, skip,
take și entries. Fiecare eveniment are id, changedAt, previous, current,
monthlyDelta și changePercent. take este limitat la 1–100, skip la 0–1.000.000;
ordinea este changedAt descrescător, apoi id descrescător. Fiecare răspuns folosește
un snapshot PostgreSQL consistent pentru prețul curent, număr și pagină. Paginile
cerute separat pot reflecta evenimente noi; clientul poate reîncărca de la zero.

Modificarea și evenimentul se salvează atomic prin SaveChangesAsync. Valorile
originale cost/currency/billingPeriod și UpdatedAt sunt tokenuri de concurență.
Un conflict SQL nu lasă în DB un eveniment pentru o schimbare eșuată; retry-ul în
același context nu dublează evenimentul. Ștergerea abonamentului șterge în cascadă
istoricul lui. Nu există rute de editare/ștergere individuală a evenimentelor;
acesta nu este un jurnal criptografic rezistent la un administrator DB compromis.

PUT-ul abonamentului acceptă `expectedUpdatedAt` (timestamp UTC primit la citire).
Interfața îl trimite la fiecare editare și refuză versiunea veche cu 409, păstrează
formularul și reîncarcă lista. Redeschide abonamentul actual înainte de a salva
din nou. Câmpul este opțional pentru compatibilitatea clienților API existenți;
aceștia trebuie să îl adopte pentru a detecta un formular rămas vechi înainte de
începerea cererii. Verificarea concurenței în timpul cererii se aplică oricum.

Ownership-ul se verifică atât pe abonament, cât și pe interogarea istoricului.
Un ID străin răspunde 404; lipsa autentificării răspunde 401. Răspunsurile sunt
no-store și nu includ UserId, email, username sau credențiale.

## Finance F# și migrație

Snapshotul existent rămâne neschimbat, schemaVersion 1. Un endpoint opțional
separat este descris în [FINANCE-INTEGRATION.md](FINANCE-INTEGRATION.md); consumatorul
F# existent nu este modificat și nu are obligația să îl apeleze.

Migrația creează numai tabela SubscriptionPriceChanges, indexul de paginare și
foreign key cu cascade. Fără backfill, modificări ale prețurilor sau ștergere de
date. DDL are lock_timeout 5s și statement_timeout 30s. Rollback-ul API păstrează
tabela; versiunea veche poate citi schema, dar nu înregistrează evenimente noi.
Schimbările făcute în acel interval nu se reconstruiesc prin presupuneri.
Evenimentele rămân până la ștergerea abonamentului/contului; o politică de retenție
mai amplă rămâne de stabilit pentru produs.
