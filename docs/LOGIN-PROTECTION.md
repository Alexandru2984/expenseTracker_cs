# Protecție persistentă la autentificare

Încercările de parolă pe același cont sunt serializate prin lock-ul rândului Users.
Contorul este în PostgreSQL și se aplică indiferent de IP, client sau instanță API.
Limita per IP existentă rămâne activă și pentru username-uri necunoscute.

Primele patru parole greșite nu blochează contul. A cincea introduce 30 secunde de
pauză; următoarele încercări greșite, după expirarea pauzei, introduc 60, 120, 240,
apoi maximum 300 secunde. Cererile din timpul pauzei nu modifică termenul sau
contorul. După 15 minute fără o încercare contabilizată, seria începe din nou.
O parolă corectă în afara pauzei și schimbarea/resetarea validă a parolei resetează
starea. Sesiunile existente și recuperarea prin email nu sunt blocate.

Loginul unui cont necunoscut, parola greșită și pauza produc aceeași eroare 401,
fără Retry-After sau expunerea contorului. Conturile necunoscute și blocate trec
printr-un BCrypt dummy; nu se creează rânduri pentru nume arbitrare. Acest lucru
reduce diferențele evidente de procesare, fără a promite timp de răspuns constant.
Pentru un cont deja blocat, lock-ul DB este eliberat înaintea verificării dummy,
astfel încât traficul respins să nu țină inutil blocate operațiile de recuperare.
Tokenurile/cookie-urile de sesiune sunt emise numai după commit.

## Identitatea username-ului

Indexul PostgreSQL unic `IX_Users_Username_CaseInsensitive` folosește
`lower("Username")`. Căutarea aplică aceeași funcție în DB ambelor operanzi, inclusiv
pentru caractere non-ASCII, independent de cultura threadului .NET. Formatul de
afișare este păstrat. Nu se promit echivalențe Unicode NFC sau confusables.
Indexul este deținut explicit de migrație, deoarece EF nu modelează indexuri pe
expresii. Înregistrările concurente care pierd cursa răspund 409, fără eroare SQL
expusă. Username-urile scurte după trim sau cu caractere de control sunt respinse.

Migrația adaugă trei coloane cu NULL/default 0 și indexul, cu lock_timeout 5s și
statement_timeout 30s. Dacă există deja coliziuni de majuscule, crearea indexului
eșuează la restore drill; nu se redenumesc, combină sau șterg conturi automat.
Rollback-ul aplicației păstrează schema aditivă. API-ul vechi poate citi schema,
dar nu aplică backoff-ul nou; rollback-ul este o măsură temporară de recuperare.

Verificare: 17 teste de autentificare trecute pe PostgreSQL temporar inițializat
prin migrații, fără skip, inclusiv înregistrări/parole concurente, username non-ASCII,
recuperare cont și sesiune existentă în timpul blocării. Suita completă a trecut
cu 126 teste înaintea ultimei regresii non-ASCII; se repetă după etapa de istoric.

## Referințe

- [OWASP Authentication: login throttling și riscul de blocare abuzivă](https://cheatsheetseries.owasp.org/cheatsheets/Authentication_Cheat_Sheet.html).
- [PostgreSQL: indexuri pe expresii](https://www.postgresql.org/docs/16/indexes-expressional.html).
