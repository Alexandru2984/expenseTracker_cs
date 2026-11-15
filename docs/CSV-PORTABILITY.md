# Import/export fără pierderea planurilor cu același nume

Importul nu mai deduplică numai după nume. Două rânduri exportate cu ID-uri distincte
se păstrează ca două abonamente, chiar dacă numele și toate valorile coincid.
Importul creează ID-uri locale noi; ID-ul sursă este păstrat separat, per cont.
Un ID din CSV nu permite citirea sau modificarea abonamentului original, inclusiv
când provine din alt cont. Importul nu actualizează înregistrări existente.

La reimport se sar ID-urile locale și ID-urile sursă deja cunoscute în același
cont. Fără ID, se compară întregul conținut normalizat salvat: nume, cost, valută,
frecvență, scadență, categorie, stare, note, link și probă. Două planuri diferite
nu se pierd doar fiindcă au același nume. Planurile perfect identice din fișiere
fără ID nu se pot deosebi; folosește ID-uri distincte pentru a le păstra separat.
ID-ul nul, GUID-ul invalid și versiunile necunoscute sunt raportate ca rânduri
respinse. imported/skipped/rejected și primele zece erori rămân explicite.

## Format și protecție pentru spreadsheet

Exportul adaugă `FormatVersion=2`. Câmpurile text care pot fi interpretate ca
formule sunt prefixate cu apostrof. Un apostrof original la început este dublat.
Importul v2 elimină exact prefixul adăugat, fără să modifice datele originale.
Protecția și caracterele originale, inclusiv linii noi și apostrofuri, coexistă.
Fișierele vechi fără marker sau cu versiunea 1 rămân acceptate; nu se ghicește
dacă apostroful lor a fost original sau adăugat de alt program.

Coloanele sunt Id, Name, Cost, Currency, BillingPeriod, NextBillingDate, Category,
IsActive, CreatedAt, Notes, ProviderUrl, TrialEndsOn și FormatVersion. Minimumul
pentru un fișier vechi rămâne Name, Cost și NextBillingDate. CreatedAt este informativ;
abonamentul importat are propriul moment de creare. Notele/linkul/proba folosesc
aceeași validare ca formularul. Exportul CSV este privat, cerut explicit de titular.

## Fișiere și capacitate

Importul și exportul sunt limitate la 1.000.000 octeți. Un export complet peste
limită răspunde 413 înaintea publicării unui fișier incomplet. Selectorul din listă
permite CSV pentru pagina curentă, cu filtrele și ordinea vizibile. API:
`GET /api/subscriptions/export?pageOnly=true&skip=0&take=10&search=...&sortBy=cost`.
Un lot are maximum 50 de abonamente; exportă paginile pe rând pentru conturi mari.
Fără pageOnly, se exportă întregul cont, independent de filtre, în limita de 5000.
Ordinea are întotdeauna ID ca departajare stabilă între valori egale.

Maximum 5000 de abonamente per cont, active și suspendate împreună. Crearea și
importul iau același lock de utilizator înainte de verificarea capacității și de
scriere, în tranzacție. Importurile concurente sunt idempotente; create + import
concurent nu depășesc limita. Rândurile noi peste limită sunt raportate ca respinse,
iar duplicatele cunoscute sunt în continuare sărite. Conturile istorice peste limită
nu sunt șterse sau trunchiate automat; pot folosi lista/exportul pe pagini pentru
revizuire. Analizele, sumarul și Finance păstrează limitele și contractele existente.

Migrația adaugă numai ImportSourceId nullable și indexul unic
(UserId, ImportSourceId). Valoarea nu este expusă în API-ul de abonamente ori în
Finance; nu este foreign key către sursă. DDL are lock_timeout 5s și statement_timeout
30s. Rollback-ul păstrează coloana și datele; API-ul vechi nu implementează noile
reguli de import sau capacitate și trebuie folosit temporar pentru recuperare.

## Dovezi și răspuns la erori

Trei regresii au eșuat înaintea fixului: două planuri cu același nume (costuri
identice/diferite) și round-trip al unui nume protejat ca formulă. După fix au trecut,
împreună cu probele PostgreSQL pentru concurență, izolarea sursei, limita contului,
fișier mare/export paginat și validarea metadatelor. Matricea completă este consemnată
în [audit](AUDIT-AND-ROADMAP.md).

Riscurile principale din review sunt pierderea tăcută de date, accesul prin ID sursă
și consumul necontrolat de resurse. Controalele sunt testate pe conturi sintetice;
nu se execută formule și nu se importă pe conturi reale. Dacă un raport e neașteptat,
păstrează CSV-ul și raportul în spațiu privat, reproduce în copia izolată și verifică
ID-urile/formatul înainte de un alt import. Nu restaura automat backupul peste date
și nu modifica în masă conturile pentru a reface un import. Sunt valabili pașii din
[runbook](SECURITY-REVIEW-LOGIN-HISTORY.md); alertarea externă rămâne un punct deschis.
