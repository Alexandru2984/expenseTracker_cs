# Bugete lunare

Bugetele se salvează în PostgreSQL, separat pentru fiecare cont. Un buget poate
acoperi toate categoriile dintr-o valută sau o singură categorie. Valutele nu sunt
amestecate și nu se folosesc cursuri de schimb în calculul bugetului.

Suma planificată include toate abonamentele active ale contului, indiferent de
pagină și filtre. Abonamentele anuale contribuie cu costul împărțit la 12; cele
suspendate sunt excluse. Calculul reprezintă costul lunar echivalent, nu istoricul
plăților efectuate. Categoria „Altele” include abonamentele fără categorie.

Interfața arată limita, suma planificată, spațiul rămas și depășirea. Bugetele
generale și de categorie pot urmări aceleași abonamente și nu se însumează automat.
Ștergerea unui buget nu șterge abonamentele. Un cont poate avea maximum 50 de bugete.

## API

- `GET /api/budgets`: bugete proprii cu monthlyLimit, monthlyPlanned, remaining,
  usagePercent, isOverBudget și version. Răspuns no-store; fără UserId/credențiale.
- `POST /api/budgets`: `{ currency, category, monthlyLimit }`. Categoria goală
  înseamnă buget general. Limita este 0,01–1.000.000, cu maximum două zecimale.
- `PUT /api/budgets/{id}`: aceleași câmpuri plus version primit la citire.
- `DELETE /api/budgets/{id}?version=...`: ștergere cu verificarea versiunii.

Sesiunea și protecția CSRF sunt obligatorii. Operațiile pe un buget al altui cont
răspund 404. Un index unic împiedică bugete duplicate pentru aceeași valută și
categorie normalizată. Categoria se compară fără diferențe de majuscule și cu
normalizare Unicode. Limita pozitivă este verificată și printr-un constraint SQL.

Editarea și ștergerea refuză o versiune veche cu 409. Interfața păstrează valorile
introduse la eroare; utilizatorul poate reîncărca bugetele și deschide versiunea
actuală înainte de salvare. Un update concurent nu suprascrie silențios alt update.

Migrația creează numai tabela Budgets, indexul și foreign key către Users. Nu
modifică abonamentele și nu schimbă contractul Finance F#. Se lansează prin același
restore drill și verificare a compatibilității cu API-ul anterior.
