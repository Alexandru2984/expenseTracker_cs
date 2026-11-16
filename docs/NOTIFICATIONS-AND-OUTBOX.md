# Rezumate email opt-in și outbox

Data review: 16.11.2026. Skill aplicat: ciso-review.

## Contract de produs

Notificările sunt oprite implicit. Utilizatorul cu email verificat poate activa
un rezumat zilnic pentru scadențe, sfârșitul probelor și, opțional, termenele
obiectivelor de economisire. Alege fusul IANA, ora locală și intervalul 0–14 zile.
Nu se trimit mesaje dacă nu există elemente. Scadențele trecute, abonamentele
inactive și obiectivele arhivate/atinse sunt excluse; datele nu sunt avansate
automat și nu se presupune că o plată sau anulare a avut loc.

Implicit, mesajul conține numerele de termene și linkul fix către aplicație.
Numele necesită opt-in separat. Sumele, notele, descrierile și URL-urile personale
nu sunt trimise nici în acest mod. Cel mult 20 de elemente sunt detaliate.
Previzualizarea din setări este read-only și nu trimite email. Linkul
https://expenses.micutu.com/#notifications deschide setările după autentificare.

Confirmarea altei adrese oprește notificările și anulează lucrul neexpediat.
Activarea validează și un context HMAC al inboxului: un formular vechi nu poate
transfera consimțământul către altă adresă, chiar dacă nu exista încă un rând de
preferințe. Un mesaj deja în curs de trimitere poate totuși ajunge după opt-out.

## API, ownership și stocare

GET/PUT /api/account/notifications citește/salvează preferințele. PUT cere versiune;
la activare, X-Notification-Recipient trebuie să corespundă recipientContext din
GET. JWT, CSRF, X-Account-Context și limitele existente de scriere se aplică.
GET-urile time-zones, preview și history sunt sub aceeași rută privată.
Tokenurile Finance nu autorizează aceste rute. Exportul F# rămâne schemaVersion 1.

Preferințele păstrează consimțământul UTC, versiunea și următoarea programare.
Outbox-ul stochează ID-ul utilizatorului, ziua locală, fusul orar, versiunea,
fingerprintul HMAC al inboxului, starea, lease-ul și coduri de eroare controlate.
Nu stochează adresa destinatarului, subiectul, corpul sau conținutul financiar.
Constrângerea unică utilizator/zi locală împiedică doi scheduleri să creeze
rezumate duplicate. Schimbarea orei/fusului nu șterge istoricul acestei zile.
Istoricul UI arată cel mult 20 de rânduri din ultimele 30 de zile.

Schimbarea preferințelor și emailului folosește lock-ul de cont existent;
anularea Pending/Preparing este atomică și nu depinde de o versiune de lease
citită înaintea unui claim concurent. Nu se țin lock-uri DB în timpul SMTP.
Workerul procesează loturi limitate și șterge progresiv rândurile terminale
mai vechi de 30 de zile. Un ciclu selectează maximum 50 de preferințe și expediază
maximum 10 mesaje, apoi așteaptă un minut; capacitatea nu este prezentată ca SLA.
Backupurile private au politica lor de retenție;
nu se afirmă ștergerea automată a tuturor copiilor.

## Timp, recuperare și livrare

Imaginea Alpine instalează tzdata. Programarea mută o oră locală inexistentă la
primul minut valid; pentru ora repetată alege prima apariție. Nu reia zilele
ratate după downtime și nu trimite înaintea orei locale curente. Aceste cazuri
folosesc API-urile documentate de Microsoft:
[ore invalide](https://learn.microsoft.com/en-us/dotnet/api/system.timezoneinfo.isinvalidtime?view=net-10.0),
[ore ambigue](https://learn.microsoft.com/en-us/dotnet/api/system.timezoneinfo.getambiguoustimeoffsets?view=net-10.0).

Claim-ul PostgreSQL folosește FOR UPDATE SKIP LOCKED și lease de două minute.
Preparing expirat poate fi reluat; Sending expirat devine Uncertain, fără retry
automat. Imediat înaintea pregătirii mesajului sunt reverificate consimțământul,
emailul verificat, fingerprintul, versiunea, ziua și datele curente.

SMTP folosește TLS obligatoriu, validarea standard a certificatului și timeout
de 20 secunde. Erorile sigure de conexiune/respingere temporară pot fi reluate
după 5 și 30 minute, maximum trei încercări. O eroare ambiguă în timpul DATA sau
un sender care aruncă o excepție necunoscută produce Uncertain. Message-ID este
stabil pe rând, dar nu este tratat ca o garanție de deduplicare a providerului.
Un ACK târziu nu suprascrie recuperarea lease-ului. Separarea erorilor se bazează
pe un timeout separat pentru salvarea confirmării. Expirarea acestui timeout
nu termină workerul; numai anularea cerută de host oprește bucla. Cazul este
verificat pe PostgreSQL prin blocarea controlată a tabelului în timpul ACK-ului.
Clasificarea erorilor SMTP se bazează
pe [implementarea MailKit 4.17.0 folosită de proiect](https://github.com/jstedfast/MailKit/blob/4.17.0/MailKit/Net/Smtp/SmtpClient.cs).

Sent înseamnă acceptat de serverul SMTP, nu livrat în inbox. Nu există promisiune
exactly-once sau confirmare a citirii. Nu se adaugă pixeli de tracking ori furnizori.
Notifications__Enabled=false oprește programarea/expedierea globală; utilizatorii
pot salva dezactivarea chiar când transportul este indisponibil.
Hosted workerul este exclus din mediul Testing; testele folosesc numai senderi
sintetici, nu SMTP de producție.

## CISO review și runbook

Amenințările principale sunt dezvăluirea către un inbox schimbat (impact ridicat,
probabilitate medie), trimiterea repetată după un crash (impact mediu, probabilitate
medie) și accesul la datele altui cont (impact ridicat, probabilitate medie).
Controalele sunt opt-in, inbox verificat/contextual, conținut minim, versiuni,
ownership, cheie zilnică unică și tratarea conservatoare a acceptării incerte.
Compromiterea aplicației/DB afectează toate conturile ei, inclusiv preferințe și
metadata notificărilor. Numărul se verifică la backup. Costul/ALE și MTTD curent
nu pot fi deduse credibil; nu sunt inventate.

Warning-urile conțin ID-ul notificării, rezultatul și numărul încercării, fără
email, corp, nume, secrete sau răspuns SMTP brut. Regula operațională propusă:
orice Uncertain ori creștere susținută a Failed în 10 minute necesită investigație.
Alertarea externă/on-call nu este implementată prin această etapă; logurile și
istoricul UI nu sunt prezentate ca alertare. Responsabil: operatorul VPS.

1. Oprește workerul cu kill switch-ul și păstrează privat ID-urile/intervalul.
2. Pentru Uncertain verifică logul serverului mail după Message-ID. Nu reseta
   automat starea la Pending și nu presupune că lipsa ACK-ului înseamnă nelivrare.
3. La problemă de destinatari, oprește preferințele afectate și revocă sesiunea
   compromisă. Reproduce numai pe backup izolat, cu sender sintetic.
4. La migrație eșuată nu activa API-ul nou; după activare folosește rollback-ul
   de imagine/UI. Nu restaura automat DB peste date noi și nu rula migrații Down.
5. Păstrează probele și urmează evaluarea obligațiilor de notificare împreună cu
   responsabilul competent. Nu sunt declarate certificări sau conformitate nouă.

Exercițiul tehnic acoperă doi scheduleri/workeri, opt-out, inbox schimbat, SMTP
temporar/incert, crash, ACK târziu, DST și acces străin. Nu simulează răspunsul
uman al unei echipe de incidente. Mailcow/SMTP, Cloudflare și VPS sunt furnizori
existenți; repo-ul nu demonstrează starea DPA-urilor. Scripturile auxiliare
ciso-advisor lipsesc local; nu sunt raportate rezultate automate fictive.

Auditul a confirmat separat expunerea inutilă a secretelor JWT/SMTP în mediul
containerului PostgreSQL, prin env_file comun. Remedierea este descrisă în
[DATABASE-ENVIRONMENT-ISOLATION.md](DATABASE-ENVIRONMENT-ISOLATION.md).
Verdictul de lansare se consemnează după teste și restore drill.
