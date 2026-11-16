# Arhiva contului și ștergerea datelor — 16 noiembrie 2026

Setări → Datele mele (`/#data`) permite descărcarea unei arhive JSON și ștergerea
contului din baza activă. CSV-ul existent rămâne disponibil pentru importul
abonamentelor. JSON are format `expense-tracker-account`, schemaVersion 1,
separat de exportul Finance v1; nu are import automat.

## Exportul privat

GET /api/account/data oferă numele, starea MFA, numerele de date, limita și
cooldown-ul. Numerele sunt orientative: ștergerea include și datele proprii
adăugate după deschiderea formularului.

POST /api/account/data/export cere currentPassword și code pentru un cont MFA:
TOTP nou ori cod de recuperare nefolosit. X-Account-Context este obligatoriu pe
rutele noi, inclusiv pentru clienți legacy. Sesiunea este reverificată după
lock-ul de cont, împiedicând o operație care aștepta în timpul revocării ei.

Arhiva cuprinde profilul/emailul, metadata activării MFA, abonamente active și
suspendate cu note/linkuri/probe, istoric de preț, bugete, obiective inclusiv
arhivate și contribuții. Include preferințe/consimțământ și istoricul notificărilor
încă păstrat în DB, plus tipul/datele sesiunilor și tokenurilor Finance păstrate.

Nu serializează direct User/RefreshToken. Nu include parole/hashuri, JWT,
refresh/Finance brute sau hashuri, coduri de email, challenges, secrete/coduri de
recuperare MFA, fingerprintul recipientului sau versiuni interne. Conține totuși
date personale și note private, deci nu este un fișier sigur de publicat.

Dovada parolei/MFA și cooldown-ul se salvează înainte de generare. Un export nou
este permis o dată pe minut pe cont, inclusiv între instanțe/IP-uri. Codul de
recuperare acceptat este consumat chiar dacă generarea ulterioară eșuează.
Parola/codurile greșite folosesc backoff-ul persistent comun al contului.

Generarea citește o tranzacție read-only Repeatable Read, fără lock de cont în
timpul generării/transferului. Credential stamp și sesiunea sunt reverificate la
începutul snapshotului. Toate secțiunile văd același snapshot, inclusiv când
contribuțiile/prețurile se modifică între citiri. Referințe:
[PostgreSQL 16](https://www.postgresql.org/docs/16/transaction-iso.html),
[tranzacții EF Core](https://learn.microsoft.com/en-us/ef/core/saving/transactions).

Generarea are timeout de 30 secunde și limită de 16 MiB pe octeți. Serializarea
este incrementală, maximum două buffere/transferuri per proces, fără fișier pe
disc, URL public de export, job în fundal sau email cu attachment. Bufferul se
eliberează la eroare/terminarea răspunsului. Limita produce 413 JSON fără attachment
parțial; 429 indică limită de cont/capacitate. Pentru conturi mai mari există CSV
pe pagini și export asistat de operator; exportul nelimitat/paginat al tuturor
secțiunilor nu este implementat.

## Ștergere și Finance F#

POST /api/account/data/delete cere parola, factorul MFA când este activ și
confirmationUsername exact, fără normalizare. Nu acceptă un ID de cont țintă.
Formularul cere și bifarea explicită, arată efectele și nu condiționează ștergerea
de descărcarea unei arhive.

În aceeași tranzacție se inserează AccountDeletionRecord și se șterge User.
FK-urile PostgreSQL elimină abonamentele/istoricul, bugetele, obiectivele și
contribuțiile, preferințele/outboxul, sesiunile web/Finance, codurile de email și
stările MFA. Alte conturi rămân intacte. O eroare produce rollback complet;
cookie-urile auth sunt șterse numai după commit. Două cereri concurente au un
singur commit.

Cererile noi cu JWT/refresh/Finance vechi devin neautorizate. Citirile începute
anterior pot încheia răspunsul; datele deja livrate nu pot fi retrase. Emailul deja
în curs de depunere SMTP poate ajunge; nu se țin lock-uri DB în timpul SMTP.
Cozi/schedulări ulterioare nu mai au cont/preferințe.

Datele deja importate în Finance F# și fișierele utilizatorului rămân acolo.
Integrarea este read-only, fără ștergere la distanță. Exportul JSON nu schimbă
contractul sau sincronizarea Finance a altor conturi.

Clientul serializează mutările cookie prin Web Lock și nu reia automat ștergerea.
Butonul este blocat în timpul cererii. Închiderea dialogului nu anulează o ștergere
depusă: succesul golește aplicația printr-un eveniment global chiar dacă panoul
nu mai există. Un răspuns pierdut nu se raportează ca succes; se poate reîncărca
starea. Exportul întârziat nu declanșează download după logout/schimbarea contului.
Parolele și codurile sunt doar în memorie și se golesc la închiderea panoului.

## Backupuri și restaurare

AccountDeletionRecords păstrează numai UserId și DeletedAt, fără FK spre contul
eliminat, nume, email, tokenuri sau conținut financiar. Evidența este pseudonimă,
poate fi asociată unei persoane dintr-un backup vechi și nu este numită anonimă.
Se păstrează pentru verificarea restaurărilor, fără listare publică. Nu are purge
automat până la stabilirea/aplicarea retenției tuturor backupurilor.

Deploymentul colectează receipts după pg_dump și păstrează o uniune în
/var/backups/expense-tracker/account-deletion-ledger.json (0600), plus o copie în
directorul backupului. O bază veche/goală nu elimină receipts păstrate. GUID/date
UTC invalide, duplicate, timestamp schimbat ori symlink opresc actualizarea.
Se verifică baza activă și restaurarea izolată; un User prezent în evidență
blochează deploymentul înainte de activare. O ștergere concurentă cu dumpul poate
cere refacerea backupului. Verificarea nu șterge automat utilizatori.

Înainte de restaurarea planificată, colectează evidența din baza încă disponibilă:

```bash
sudo python3 scripts/deploy-finance-integration.py \
  --snapshot-deletions-container NUME_CONTAINER_CURENT \
  --database-user UTILIZATOR_DB --database NUME_DB
```

Pentru copia restaurată separat, cu API-ul oprit/izolat:

```bash
sudo python3 scripts/deploy-finance-integration.py \
  --check-deletions-container NUME_CONTAINER_RESTAURAT \
  --database-user UTILIZATOR_DB --database NUME_DB
```

Evidența externă este copiată la backup/deployment, **nu continuu la fiecare
ștergere**. DB păstrează fiecare receipt în tranzacția ștergerii. Înainte de o
restaurare planificată, colectează privat și reconciliază receipts cele mai noi din
DB încă disponibilă. La pierderea totală a DB, ștergerile după ultima copie externă
pot lipsi. Nu redeschide copia până la reconcilierea acestei ferestre; trecerea
unui jurnal vechi nu dovedește completitudinea. Nu se pretinde RPO zero ori
sincronizare externă tranzacțională.

Ștergerea în aplicație nu rescrie backupurile sau logurile existente. Nu adaugă
retenție globală automatizată sau copie off-site verificată. Duratele și aplicarea
pe toate copiile rămân de stabilit; UI nu promite dispariția lor imediată.
Nu rula migrații Down: ar elimina evidența. Rollbackul API/UI păstrează DB și
ștergerile comise; un binar vechi nu autentifică un User inexistent.

## CISO review și runbook

1. STRIDE: exportul altui cont (spoofing/info disclosure, medie/ridicat), ștergere
   prin sesiune capturată/CSRF (tampering, medie/ridicat), reapariție la restore
   (repudiation/info disclosure, medie/ridicat). Controale: ownership/context,
   parolă+MFA, backoff, snapshot, tranzacție/cascade și evidență verificată.
2. Blast radius: un compromis complet API/VPS permite accesul/ștergerea tuturor
   conturilor și modificarea evidenței; backupurile cu chei au același risc.
   Arhiva legitimă expune propriile date celui care primește fișierul. Numărul se
   măsoară la backup; nu există o estimare credibilă pentru cost/ALE.
3. Detecție: warning-ul de ștergere comisă conține numai ID/timestamp; guardul
   oprește scriptul. Operatorul investighează orice reapariție sau ștergere
   neobișnuită. Alertarea externă/on-call automat lipsesc; MTTD este necunoscut.
4. Răspuns: revocă accesul compromis și păstrează probele privat. Nu restaura
   automat contul. La matches, păstrează API-ul închis și reconciliază în izolare
   cu evidența curentă; nu goli jurnalul ca să treacă. Testele exercită tehnic
   rollbackul, concurența, revocarea, snapshotul și guardul; un exercițiu uman
   complet nu este declarat testat.
5. Reglementare: evaluarea obligațiilor revine responsabilului competent; această
   funcție nu certifică o conformitate sau un termen legal. Șablon incident:
   interval, operații confirmate, conturi/date afectate, măsuri și pași pentru
   utilizator, fără secrete și fără afirmații înainte de investigație.
6. Furnizori: niciun pachet/serviciu nou. VPS, Cloudflare și SMTP rămân existente;
   repo-ul nu demonstrează DPA-urile lor. Scripturile ciso-advisor lipsesc local,
   fără rezultate automate fictive.

Testele folosesc date sintetice și PostgreSQL temporar. Nu se exercită ștergerea,
exportul personal sau trimiterea de mesaje pe conturi de producție. Dovezile
finale ale lansării se adaugă după verificare.
