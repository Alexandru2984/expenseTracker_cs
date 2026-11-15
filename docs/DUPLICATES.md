# Revizuirea posibilelor duplicate

Panoul „Verifică suprapunerile” analizează întregul cont, independent de pagina și
filtrele listei. Numele sunt comparate după normalizare Unicode NFKC, comprimarea
spațiilor și eliminarea diferențelor de majuscule. Valutele rămân separate;
frecvențele lunare/anuale pot apărea împreună pentru revizuire.

Pot exista legitim mai multe planuri cu același nume. Rezultatul este o sugestie,
fără ștergere, combinare sau suspendare automată. Butonul Revizuiește deschide
abonamentul actual din API în formular, inclusiv atunci când se află pe altă
pagină. Orice schimbare ulterioară folosește controalele obișnuite de ownership,
CSRF și concurență. Datele nu sunt preluate dintr-un rezumat potențial vechi.

Implicit se verifică doar abonamentele active. Opțiunea de includere a celor
suspendate le arată și pe acestea, fără a le adăuga în costul lunar activ al
grupului. Costul reprezintă totalul lunar echivalent al planurilor active (anual / 12),
nu o economie garantată. Interfața arată câte 10 grupuri, cu extindere manuală.

`GET /api/subscriptions/duplicates?includeInactive=false` necesită sesiune proprie,
răspunde no-store și include checkedCount, includeInactive și groups. Fiecare grup
conține name, currency, activeMonthlyTotal și items, fără UserId sau credențiale.
Maximum 5000 de abonamente per analiză; peste limită răspunde 413, fără rezultat
trunchiat. Nu necesită migrație și nu modifică snapshotul Finance F#.

Verificare înaintea commitului etapei: 8 teste selectate pe PostgreSQL temporar,
fără skip, și 60 aserțiuni dashboard la 390 px în dark, inclusiv WCAG, revizuire
din API și opțiunea de includere a celor suspendate. Matricea completă se repetă
după completarea detaliilor abonamentelor.
