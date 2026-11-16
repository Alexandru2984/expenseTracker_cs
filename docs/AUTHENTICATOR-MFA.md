# MFA prin autentificator — 16 noiembrie 2026

Funcția este opțională, în Setări → Autentificare în doi pași, cu legătură directă
`/#security`. Activarea cere email verificat, parola curentă și un cod din
aplicația TOTP. QR-ul este generat în browser; cheia nu este trimisă unui serviciu
QR extern. Există alternativă de introducere manuală și zece coduri de recuperare.
Acestea sunt afișate numai după activare/regenerare, la cererea utilizatorului;
nu sunt salvate în localStorage, sessionStorage sau fișiere descărcate automat.
Închiderea panoului șterge datele secrete din starea componentei.

## Contract și protecții

- Profilul [RFC 6238](https://www.rfc-editor.org/rfc/rfc6238) folosit este SHA-1,
  șase cifre, 30 secunde, cheie aleatoare de 160 biți. Verificarea permite pasul
  anterior/curent/următor, dar un pas acceptat nu poate fi refolosit pe acel cont.
  SHA-1 este folosit pentru HMAC-ul protocolului, nu pentru parole.
- Parola corectă a unui cont MFA produce HTTP 202 și un challenge opac de cinci
  minute, fără access/refresh cookie. Numai al doilea factor produce sesiunea.
  Challenge-ul este păstrat ca hash, legat de starea credentialelor, consumabil o
  singură dată, cu maximum cinci încercări și trei challenges active per cont.
  Întoarcerea la formularul de login anulează challenge-ul deținut.
- Eșecurile sunt legate de cont prin backoff-ul persistent existent, inclusiv
  între challenges și IP-uri. Parola corectă singură nu resetează contorul MFA.
  Reautentificarea pentru configurare are același backoff persistent.
  Limitarea HTTP existentă rămâne activă. Configurarea expiră în zece minute,
  are cinci încercări și un minut între porniri.
- Codurile de recuperare au câte 80 biți aleatori, hash SHA-256 cu namespace și
  identificator de utilizator. Nu depind de cheia de criptare. Folosirea concurentă
  a aceluiași cod are un singur câștigător pe PostgreSQL. Regenerarea anulează toate
  codurile vechi și cere parola plus un factor valid.
- Secretul TOTP este criptat AES-256-GCM cu nonce aleator de 96 biți și tag de
  128 biți; utilizatorul și versiunea scopului sunt autentificate ca AAD. HKDF
  separă cheia acestui scop. Secretul temporar folosește aceeași protecție.
  DTO-urile de stare nu returnează secretul ori hashurile.
- Activarea/dezactivarea schimbă versiunea credentialelor, revocă celelalte sesiuni
  web și reemite sesiunea curentă numai după commit. Mutările de cookie păstrează
  Web Lock-ul și protecțiile existente contra schimbării contului între taburi.
  Codurile de email și configurările MFA nefinalizate se invalidează la schimbări
  de credențiale. Resetarea parolei prin email păstrează MFA.

Tokenurile personale de citire Finance F# se păstrează la activarea/dezactivarea
MFA și regenerarea codurilor. Sunt în continuare credențiale bearer independente,
read-only și revocabile; nu cer OTP la fiecare sincronizare. Contractul de export
`schemaVersion: 1` nu se modifică. Schimbarea/resetarea parolei și schimbarea
emailului păstrează politica lor existentă de revocare, inclusiv Finance.

## Cheia și backupul

`Mfa__EncryptionKey` trebuie să fie o cheie independentă de 32 octeți, în base64,
generată o singură dată (`openssl rand -base64 32`), păstrată privat în configurația
API și în backupurile securizate. Nu se pune în Git, frontend, log sau mediul DB.
`Mfa__AllowEnrollment=false` oprește configurări noi, fără să dezactiveze factorul
conturilor existente. Absența/invaliditatea cheii oprește configurarea; la login
OTP produce eroare 503, fără fallback la parolă. Un cod de recuperare nefolosit
permite loginul și dezactivarea chiar dacă decriptarea nu mai este posibilă.

Nu înlocui cheia ca metodă de rezolvare a unei erori: secretele existente ar deveni
inutilizabile. Restaurează cheia corectă sau proiectează explicit o recriptare.
Deploymentul salvează separat cheia API-ului care rulează, când există, și verifică
atât cheia candidatului, cât și cheia curentă pe backupul restaurat izolat.
Comanda operatorului `dotnet ExpenseTracker.Api.dll --check-mfa-key` nu pornește
HTTP, SMTP sau workerii; citește numai înregistrările criptate și afișează un
număr ori tipul erorii. Cheia ajunge prin env-file privat 0600, nu argument CLI.
O cheie invalidă oprește deploymentul chiar dacă nu există încă secrete salvate.
Backupul DB singur nu este suficient pentru recuperarea autentificatoarelor.

## Migrație și revenirea la o versiune veche

Migrația `20261116133211_AddAuthenticatorMfa` adaugă coloanele și cele două tabele;
utilizatorii existenți pornesc cu MFA oprită. Credential stamp pentru un cont care
nu a folosit MFA rămâne identic. Nu se rehașează parolele și nu se activează MFA
automat pentru nimeni. Schimbarea se verifică prin restaurare, migrare repetată
idempotent și probe EF cu binarele vechi/noi înainte de producție.

O versiune veche nu știe să ceară OTP. Pentru a preveni loginul numai cu parola
la rollback, hashul contului activ primește prefixul `$mfa$`, pe care vechiul
verificator BCrypt îl respinge. Constrângerea DB leagă prefixul de `MfaEnabled`,
împiedicând un reset de parolă vechi să îl elimine. Codul nou tratează explicit
prefixul la verificare/schimbare/resetare. Activarea invalidează codurile de email
care ar fi putut fi emise anterior.

**Rollbackul spre un binar fără MFA poate bloca loginul/resetarea conturilor MFA.**
Este o închidere deliberată a accesului: nu promite compatibilitate funcțională
pentru acele conturi. Revino la un build care cunoaște MFA pentru restaurarea
serviciului. Nu elimina prefixul, nu șterge factorii și nu rula migrații Down ca
remediu automat. API/UI/config pot fi revenite; DB nu este restaurată automat
peste date noi. Testul PostgreSQL verifică respingerea scrierii de parolă legacy.

## CISO review și răspuns la incidente

1. **STRIDE / primele trei riscuri:** furtul parolei și încercarea unui bypass
   prin recuperare/rollback (spoofing, probabilitate medie, impact ridicat),
   extragerea secretelor (information disclosure, medie/ridicat), replay și
   încercări concurente (tampering/DoS, medie/ridicat). Controalele sunt factorul
   separat, prefixul/constrângerile, criptarea cu cheie separată, challenges
   limitate și lock-ul de cont. TOTP rămâne susceptibil la phishing în timp real;
   passkeys nu sunt implementate în această etapă.
2. **Blast radius:** o copie DB fără cheia API expune hashuri MFA, ciphertext și
   metadate, alături de datele financiare existente. Compromiterea completă a
   aplicației/VPS poate decripta secretele tuturor conturilor MFA și accesa datele
   tuturor utilizatorilor. Un backup complet include cheia și DB; compromiterea
   lui poate expune secretele tuturor conturilor MFA. Numărul efectiv se măsoară
   la backup. Costul/ALE nu are
   date suficiente pentru o estimare credibilă; nu se inventează.
3. **Detecție:** folosirea unui cod de recuperare scrie warning cu ID de cont,
   fără cod, email sau secret. HTTP 401/429 repetate și erorile decriptării/CLI
   sunt semnale de investigat. Regula propusă operatorului: investighează orice
   eroare de decriptare și rafale de 401/429 MFA în zece minute. Nu există alertare
   externă/on-call automat implementată; MTTD curent este necunoscut. Responsabil:
   operatorul VPS. Logurile singure nu sunt prezentate ca alertare.
4. **Runbook:** la eroare de cheie oprește configurări noi, păstrează probele și
   compară privat configurația cu backupul; nu roti cheia la întâmplare. Revino
   la cheia/buildul corect după verificare pe restore izolat. La preluare de cont
   revocă sesiunile și tokenurile Finance afectate; nu dezactiva MFA ca scurtătură.
   La furt de master key planifică reînrolarea factorilor și evaluează întregul
   compromis al aplicației. Cazurile cheie greșită/lipsă, recuperare, resetare,
   concurență și scriere legacy sunt exercitate tehnic în teste. Răspunsul uman
   al unei echipe de incidente nu este declarat testat.
5. **Reglementare:** continuă runbookul existent pentru păstrarea probelor și
   evaluarea obligațiilor de notificare cu responsabilul competent. Nu sunt
   declarate certificări, temeiuri legale sau conformitate nouă. Șablon de lucru
   pentru comunicare: intervalul incidentului, conturile/datele confirmat afectate,
   măsurile luate și pașii ceruți utilizatorului; se completează după investigație,
   fără a include chei/coduri sau afirmații neverificate.
6. **Furnizori / supply chain:** niciun serviciu nou cu acces la secrete.
   [node-qrcode](https://github.com/soldair/node-qrcode) 1.5.4 este pinuit în lockfile,
   instalat fără scripts și încărcat local la configurare; npm audit verificat.
   VPS, Cloudflare și mailcow rămân existente. Repo-ul nu dovedește starea
   DPA-urilor lor. Scripturile ciso-advisor referite de skill lipsesc local;
   nu se raportează rezultate automate fictive.

## Verificare și limite

Testele folosesc exclusiv conturi `.invalid`, chei/coduri sintetice și PostgreSQL
temporar. Browserul verifică activarea, QR local, coduri ascunse/copiere,
regenerare/dezactivare, login în doi pași, expirare/anulare, răspunsuri întârziate,
absența secretelor în storage, contrast/WCAG și overflow în light/dark la
320/390/1440 px. Finance și suitele existente se rulează împreună cu noile teste.
Dovezile finale și verdictul de lansare se consemnează după deployment.

MFA protejează loginuri noi; un cookie de sesiune valid ori un token Finance furat
nu începe să ceară OTP la fiecare cerere. XSS/compromiterea dispozitivului sau a
serverului necesită controalele lor. Utilizatorii trebuie să păstreze codurile
separat de telefon; nu există recuperare administrativă care ocolește factorii.

### Rezultatul lansării

Cod activ: `23a3174d6fb1114f15aea61dd3100081b4681ff9`, pe domeniul
`https://expenses.micutu.com`. Commiturile etapei folosesc identitatea umană
existentă, fără coautori suplimentari. Documentația finală poate avea un HEAD
ulterior acestui build; labelul imaginii și fișierul public build-revision indică
intenționat commitul codului lansat.

- 251 teste backend trecute, zero skip, pe PostgreSQL temporar; modelul EF nu are
  schimbări în afara migrației. Restore NuGet cu audit pentru toate dependențele
  și erori la avertismentele de vulnerabilități a trecut.
- 998 verificări browser: dashboard 436, Finance 24, sesiuni 58, obiective 168,
  notificări 132, MFA 180. Testele MFA acoperă light/dark la 320/390/1440 px,
  inclusiv WCAG și păstrarea tokenului Finance în interfață.
- 12 teste de operațiuni, instalare npm curată fără scripts, npm audit fără
  vulnerabilități raportate, build UI și imagine Alpine reușite. npm semnalează
  separat deprecarea pachetului existent lucide-vue-next; migrarea bibliotecii
  rămâne de planificat, fără a o prezenta ca vulnerabilitate confirmată.
- Proba cu binarele reale, exclusiv pe cont sintetic și PostgreSQL fără rețea
  externă: activare MFA, login 202 fără cookie auth, recuperare 200, verificarea
  criptării unui secret temporar și activ, respingerea cheii greșite; export
  Finance schemaVersion 1 cu același token în imaginea nouă și veche.
  Loginul cu parola în imaginea veche a produs **500 fără cookie auth** (BCrypt
  vechi nu recunoaște prefixul), iar scrierea de resetare legacy a fost respinsă
  de constrângerea SQL. Aceasta confirmă închiderea accesului, nu un rollback
  funcțional al loginului MFA.
- Backupul privat `/var/backups/expense-tracker/deploy-20261116T141405Z` s-a
  restaurat în izolare. Avea zero utilizatori și abonamente; nu existau încă
  secrete MFA de decriptat în backupul real. Cheia candidatului a trecut validarea
  de format și proba CLI; secretele existente au fost exercitate separat în proba
  sintetică. Migrația 14 → 15, repetarea idempotentă și query-urile EF ale
  imaginilor veche/nouă au trecut înaintea modificării producției.
- Domeniul public: health 200, 13 rute private 401/no-store, nonce CSP diferit pe
  cereri diferite, revizie UI/imagine corectă. Chromium la 390 px: legătura protejată
  `#security` afișează loginul fără dialog privat, overflow, erori JS sau încălcări
  CSP. Clientul F# verifică trei rute anonime 401/no-store.
- API sănătos, non-root, zero restarturi după activare și fără erori de pornire;
  containerul DB și StartedAt au rămas identice. Mediul DB nu conține cheia MFA
  sau secrete JWT/SMTP. Dry-run Compose implicit păstrează API/DB Running.
  Nicio creare de cont ori expediere de email de test în producție.

Verdict tehnic: criteriile acestei lansări au trecut. TOTP rămâne opțional;
alertarea externă, passkeys și un exercițiu uman de incidente rămân puncte deschise.

Corecție de raportare: totalul browser al acestei etape a fost însumat inițial
greșit ca 1.098. Cele șase rezultate păstrate sunt 436 + 24 + 58 + 168 + 132 +
180 = 998. Nu s-au schimbat rezultatele individuale. Evidența privată a lansării
a fost corectată cu istoric al modificării.
