# CISO review: autentificare și istoric prețuri

Data: 15.11.2026. Skill aplicat: ciso-review. Domeniu: aplicația din acest repo,
schema PostgreSQL și lansarea pe VPS; fără furnizori noi sau schimbări Cloudflare.

## Threat model și impact

1. Spoofing: ghicirea distribuită a parolei unui cont (probabilitate ridicată,
   impact ridicat). Contor persistent per cont, pauză limitată, ratelimit per IP,
   recuperare prin cod separat și sesiuni revocabile.
2. Tampering: două editări concurente ale prețului produc istoric incorect
   (probabilitate medie, impact mediu). Modificarea prețului și evenimentul se
   salvează atomic, cu verificarea valorilor originale în DB.
3. Information disclosure: citirea istoricului altui utilizator sau folosirea
   unui token nepotrivit (probabilitate medie, impact ridicat). Ownership prin
   abonament, tokenuri Finance cu namespace separat, aceleași reguli de revocare.

Compromiterea completă a DB ar expune emailuri, nume, hashuri de parole, metadata
sesiunilor și datele financiare ale tuturor conturilor din această aplicație.
Hashurile nu sunt parole/tokenuri în clar. Numărul exact de utilizatori se verifică
privat la backup; nu se inventează o estimare financiară. ALE/costul unui incident
nu pot fi calculate din dovezile disponibile.

## Detecție

API-ul emite un eveniment Warning când activează o pauză, cu ID-ul contului și
termenul UTC. Nu loghează parola, username-ul, emailul sau tokenul. Regula propusă
pentru operare: cel puțin trei activări pentru același ID în 15 minute sau un val
de activări pentru conturi distincte necesită investigație. Nu există în repo o
alertare externă/on-call verificată; logurile singure nu garantează detecția.
MTTD curent necunoscut; ținta operațională propusă este sub o oră. Responsabil de
configurarea alertelor: operatorul VPS. Nu se declară această țintă implementată.

## Runbook și exercițiu

1. Confirmă evenimentele, intervalul și ID-urile afectate, păstrând accesul la
   dovezi numai pentru operator. Nu copia secretele sau dump-ul în issue-uri.
2. Pentru blocare abuzivă, verifică dacă pauza expiră fără prelungire la trafic.
   Utilizatorul poate recupera prin email; nu se dezactivează global ratelimit-ul.
3. La suspiciune de cont compromis, utilizatorul schimbă parola și revocă sesiunile
   și tokenurile Finance. Dacă operatorul intervine, păstrează dovada și tranzacția
   explicită; nu rula SQL distructiv dintr-un raport automat.
4. La eșec de migrație/coliziuni, oprește lansarea înainte de DB activă. La eroare
   după activare, folosește imaginea/configurația privată de rollback; nu restaura
   automat backupul peste datele curente și nu rula migrații Down.
5. La istoric incorect, păstrează evenimentele pentru analiză, reproduce pe copia
   izolată și corectează printr-o migrație explicită, fără rescriere în masă.

Exercițiul tehnic se execută numai pe conturi sintetice: activare pauză din clienți
diferiți, cereri concurente, acces continuu din sesiunea existentă, recuperare cu cod
greșit apoi valid, coliziuni de username și editări concurente de preț. Rezultatele
suitei și ale restore drill-ului se consemnează în audit după execuție; acesta nu
simulează răspunsul uman al unei echipe de incidente.

## Obligații și furnizori

Nu s-au stabilit certificări sau un cadru complet de conformitate. Într-un incident,
operatorul trebuie să înregistreze momentul descoperirii și să stabilească obligațiile
de notificare aplicabile cu responsabilul competent; documentul nu substituie acea
evaluare. SMTP, Cloudflare, registrul imaginilor și găzduirea VPS sunt deja în traseu.
Nu sunt adăugați furnizori/subprocesatori; starea DPA-urilor și review-urilor lor nu
este demonstrată de acest repo.

Verdict tehnic: remediere și verificare înainte de lansare. Criterii: teste reale
PostgreSQL, izolare între conturi, compatibilitate Finance, UI mobil/accesibilitate,
backup restaurat, migrații idempotente și API vechi/nou compatibile. Lipsa alertării
externe și riscul rezidual de blocare abuzivă rămân consemnate.

## Rezultatul exercițiului tehnic și al lansării

15.11.2026: 136 teste backend pe PostgreSQL prin migrații, fără skip. Scenariul
de blocare a confirmat contor comun, termen nemodificat la trafic și sesiune
existentă funcțională. Recuperarea cu cod greșit a păstrat blocarea; codul valid a
permis login cu noua parolă. Înregistrările concurente au produs un singur cont
și un răspuns 409. Un al doilea context cu preț vechi a pierdut editarea, fără
eveniment rezidual; retry-ul după reload a produs exact următorul eveniment.
Accesul străin și tokenurile revocate/expirate au fost respinse.

La lansare, backupul a fost restaurat fără rețea publică, bundle-ul repetat și
imaginile API veche/nouă probate pe schema extinsă. Numărul de conturi/abonamente
s-a păstrat; DB activă nu a fost restartată. Endpointurile publice au respins
cererile fără token și au păstrat no-store. Criteriile tehnice ale acestei lansări
au trecut; nu se declară testat răspunsul uman/on-call sau configurată alertarea
externă. Dovezi și revizia activă: [audit](AUDIT-AND-ROADMAP.md).
