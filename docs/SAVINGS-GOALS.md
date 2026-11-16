# Obiective de economisire

Obiectivele sunt înregistrări manuale, private pe cont. Aplicația nu transferă
bani și nu deduce contribuții din abonamente suspendate sau din simulator.
Simulatorul poate precompleta valuta și planul lunar al unui obiectiv nou;
utilizatorul alege numele, ținta și salvează explicit.

## Comportament și calcule

Un obiectiv are nume, descriere, valută, țintă pozitivă, plan lunar opțional și
termen opțional. Progresul rezultă exclusiv din contribuțiile înregistrate,
inclusiv contribuții care depășesc ținta. Bara se oprește la 100%, dar suma reală
rămâne vizibilă. După prima contribuție, valuta nu poate fi schimbată cât timp
există contribuții; nu se fac conversii sau însumări între valute.

Estimarea pornește de la data UTC curentă și presupune prima contribuție lunară
peste o lună. Numărul de luni este rotunjit în sus din rămas / plan lunar.
Adăugarea lunilor folosește calendarul, inclusiv sfârșitul lunii și anii bisecți.
Proiecțiile peste 100 de ani nu produc o dată estimată. Necesarul lunar împarte
suma rămasă la numărul de contribuții lunare care încap efectiv până la termen,
apoi rotunjește suma în sus la cent. Dacă termenul vine înaintea primei contribuții
lunare, nu este prezentat un necesar lunar fictiv: UI indică nevoia de a economisi
înainte de termen. Dobânda, inflația și schimbul valutar
nu sunt incluse. Un termen trecut nu este tratat ca o plată restantă.

Obiectivele se filtrează în lucru / atinse / arhivate. Arhivarea păstrează
istoricul și refuză contribuții noi până la reactivare. Eliminarea unei
contribuții scade progresul; ștergerea obiectivului elimină și istoricul lui.
Ambele cer confirmare în UI. Sumele și notele sunt păstrate la eroare; un conflict
de versiune cere reîncărcare explicită și redeschiderea formularului.

## Contract API și securitate

- GET/POST /api/goals: listă privată / creare.
- PUT/DELETE /api/goals/{id}: editare / ștergere cu versiune obligatorie.
- GET /api/goals/{id}/contributions?skip=0&take=20: istoric, maximum 100/pagină.
- POST /api/goals/{id}/contributions: ID UUID generat de client, versiune,
  sumă, data UTC și notă opțională.
- DELETE /api/goals/{id}/contributions/{entryId}?version=...: eliminare.

JWT, revocarea sesiunii, CSRF, limitarea cererilor și X-Account-Context se aplică
și acestor rute. Ownership-ul este verificat la fiecare operație, inclusiv istoric
și eliminarea unei contribuții. Tokenurile Finance nu autorizează rutele goals.

Fiecare mutație folosește tranzacție și lock per cont, în aceeași ordine ca
operațiile existente de credențiale. Versiunea obiectivului se schimbă și când
se modifică istoricul, pentru a nu șterge sau edita peste o contribuție concurentă.
Cheia contribuției este unică pe obiectiv. Retry cu aceeași cheie și aceleași
date returnează intrarea existentă, chiar dacă versiunea inițială este veche;
aceeași cheie cu alte date produce 409. Un răspuns pierdut nu dublează suma.

Limite pe cont: 50 obiective inclusiv arhivate, 5000 contribuții. Sume de maximum
1.000.000, două zecimale; totalul contribuțiilor unui obiectiv este limitat la
1.000.000. Numele, descrierea și nota au limite 100/500/200 caractere și sunt
afișate ca text. Contribuțiile viitoare sunt refuzate. Interogarea listei citește
versiunea și agregatele într-un singur snapshot SQL. Migrația adaugă două tabele,
FK-uri cu cascade, indexuri și constrângeri numerice; nu rescrie datele existente.

Exportul Finance F# rămâne schemaVersion 1; obiectivele, contribuțiile și
notele lor nu sunt adăugate implicit în acel contract. Nu există furnizori noi,
joburi, emailuri sau dependențe noi pentru această funcție.

## CISO review și răspuns la incidente

Data: 16.11.2026. Skill: ciso-review. Cele mai probabile amenințări sunt IDOR
(impact ridicat, probabilitate medie), dublare/pierdere prin concurență
(impact mediu, probabilitate medie) și epuizarea resurselor prin volume mari
(impact mediu, probabilitate medie). Controalele sunt ownership, tranzacțiile,
versiunile, idempotency, limitele și paginarea, verificate pe PostgreSQL.

Compromiterea DB ar expune suplimentar obiectivele, sumele, datele și notele
tuturor conturilor aplicației. Numărul de conturi se verifică la backup.
Nu există date pentru o estimare credibilă a costului sau ALE; nu sunt inventate.

Logurile HTTP existente permit investigarea statusurilor și intervalelor, fără
a adăuga nume, note sau sume în loguri. Creșterea susținută a răspunsurilor 409,
403 sau 429 pe rutele goals este un semnal de investigat; alertarea externă și
MTTD-ul nu sunt implementate/demonstrate de această etapă. Responsabil operațional:
operatorul VPS. Nu se declară realizată o simulare umană on-call.

Runbook:

1. Păstrează privat intervalul, request ID-urile și ID-urile afectate; nu publica
   dump-uri, note, sume sau credențiale în issue-uri.
2. Reproduce incidentul pe backup restaurat izolat. Pentru dublări verifică cheia
   contribuției, versiunea și eventualele retry-uri, înaintea oricărei corecții.
3. La acces neautorizat, limitează accesul aplicației afectate și revocă sesiunea
   compromisă prin mecanismele existente. Nu dezactiva global CSRF/ownership.
4. Corectează înregistrările numai după identificarea cauzei și printr-o operație
   explicită, tranzacțională. Nu recalcula în masă și nu restaura automat backupul
   peste contribuțiile introduse după snapshot.
5. La regresie de lansare, revino la imaginea și UI-ul anterior; schema este
   aditivă. Nu rula migrații Down pe producție.

Exercițiul tehnic acoperă cont străin, retry după răspuns pierdut, cereri concurente,
conflict de versiune și limite. Restore drill-ul și compatibilitatea API vechi/nou
se verifică înaintea migrației active. Rezultatele și revizia activă se consemnează
în audit după execuție. Evaluarea obligațiilor de notificare urmează runbook-ul
existent și responsabilul competent; nu se declară un nou cadru de conformitate.
Furnizorii existenți și starea nedemonstrată a DPA-urilor rămân cele din review-ul
de securitate anterior. Scripturile auxiliare ciso-advisor nu există în instalarea
locală; nu sunt raportate calcule automate sau verificări de conformitate fictive.

Criterii de lansare: teste PostgreSQL, izolare și idempotency, UI mobil/WCAG,
regresii Finance/sesiuni, migrație restaurată și repetată, API vechi compatibil,
backup privat, fără restart DB. Verdictul final se bazează pe dovezile din audit.

Lansare verificată: revizia 8f8ac08, backup restaurat, migrație 12 → 13 repetată
idempotent, API vechi/nou compatibile, 204 teste backend fără skip și matricea
browser trecută. Verdict tehnic: criteriile acestei lansări au trecut. Nu se
declară rezolvate alertarea externă, backupul off-site sau răspunsul uman on-call.
Dovezi și backup/rollback: [audit](AUDIT-AND-ROADMAP.md).
