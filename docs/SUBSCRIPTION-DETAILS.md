# Note, furnizor și perioade de probă

Formularul abonamentului păstrează note personale (2000 caractere), pagina
furnizorului (2048 caractere după normalizarea URL) și finalul opțional al probei.
Notele sunt text simplu, cu linii noi; HTML-ul nu se execută. Linkurile necesită
HTTP/HTTPS absolut, fără credențiale, caractere de control sau spații necodificate.
Nu se fac cereri către furnizor de pe server. Deschiderea manuală în browser are
noopener/noreferrer și no-referrer; interfața verifică protocolul și la afișare.

POST/PUT acceptă un obiect opțional `details`:

```json
{
  "notes": "Condiții de reînnoire verificate",
  "providerUrl": "https://furnizor.example/account",
  "trialEndsOn": "2027-01-14"
}
```

La creare, lipsa lui înseamnă câmpuri goale. La editare, un obiect absent/null
păstrează toate detaliile existente, pentru clienții API vechi. Un obiect prezent
înlocuiește întregul set; `{}` sau valori goale/null îl golesc. Interfața trimite
setul complet și versiunea expectedUpdatedAt. Modificarea acestor detalii nu
creează evenimente de preț dacă prețul/valuta/frecvența nu se schimbă.

Prima scadență trebuie să fie la finalul probei sau mai târziu. Regula se aplică
în API, CSV și prin check constraint PostgreSQL, inclusiv la scrieri vechi. Anii
acceptați sunt 1900–9998. Costul este cel recurent după probă. Prognoza și exportul
Finance folosesc în continuare scadența salvată, fără plăți înaintea ei. Bugetele
afișează costul lunar echivalent planificat, nu plăți efectuate în timpul probei.

Dashboardul arată până la 12 termene apropiate de ziua curentă UTC, inclusiv cele
recent încheiate, doar pentru planuri active. Revizuirea deschide înregistrarea
actuală. Datele nu anulează planul la furnizor și nu declanșează emailuri automate.
După revizuire se poate goli data de probă din formular.

Detaliile apar numai în citirea proprie și în exportul CSV cerut de utilizator.
Notele se pot căuta în cont. Nu apar în snapshotul Finance schemaVersion 1,
istoricul de preț, raportul de duplicate, sumarul, insights sau calendarul ICS.
Insights expune numai termenul de probă și câmpurile financiare necesare.
CSV include coloane opționale Notes, ProviderUrl, TrialEndsOn; fișierele vechi
rămân acceptate. Importul păstrează limita de 1 MB și raportează rândurile invalide.

## Migrație, risc și recuperare

Migrația adaugă trei coloane și un check constraint; valorile vechi rămân goale/
NULL. Fără backfill sau modificarea prețurilor. DDL are lock_timeout 5s și
statement_timeout 30s. Versiunea API anterioară poate citi schema; în rollback,
încercarea de a muta o scadență înaintea unei probe deja salvate e respinsă de DB.
Rollback-ul păstrează schema, fără restaurare automată de date sau migrații Down.

Review-ul de securitate continuă metoda ciso-review: riscurile principale sunt
XSS/linkuri executabile, expunerea notelor în integrări și scadențe inconsistente.
Regresiile folosesc date sintetice și verifică respingerea fără scriere, escaparea
în browser, izolarea conturilor și contractul Finance. Nu sunt furnizori noi;
datele extind informația privată din aceeași bază, fără afirmații de conformitate.
La o problemă, păstrează dovezile privat, reproduce în copie izolată și folosește
rollback-ul aplicației; controalele și pașii de incident existenți rămân în
[runbook](SECURITY-REVIEW-LOGIN-HISTORY.md). Alertarea externă rămâne neconfigurată.

Referințe: [OWASP: validarea URL și contexte XSS](https://cheatsheetseries.owasp.org/cheatsheets/Cross_Site_Scripting_Prevention_Cheat_Sheet.html),
[Microsoft: URI bine format](https://learn.microsoft.com/en-us/dotnet/api/system.uri.iswellformeduristring).

Înaintea commitului etapei: 24 teste PostgreSQL specifice trecute, fără skip,
și 70 aserțiuni dashboard la 390 px/dark, inclusiv note HTML ca text, link sigur,
link invalid ascuns, formular cu probă, eroare de dată și WCAG.
