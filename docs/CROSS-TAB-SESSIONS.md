# Schimbarea contului între taburi

## Contract API și amenințare

Cookie-urile sunt comune tuturor taburilor aceleiași origini. Un formular deschis
pentru A poate trimite ulterior cookie-urile contului B. Verificarea JWT și CSRF
singură nu identifică această schimbare de intenție.

Login, verificarea emailului, refresh și `GET /api/auth/me` returnează suplimentar
`accountContext`: un HMAC SHA-256 cu scop separat și cheie de server, stabil pentru
același cont. Nu este ID-ul contului și nu autorizează cereri. JWT/cookie-urile,
revocarea sesiunii, ownership-ul și CSRF rămân necesare.

SPA trimite `X-Account-Context` pe rutele private account/subscriptions/budgets.
Backendul compară contextul cu identitatea autentificată înaintea operației.
Nepotrivirea produce 409 cu `code: account_context_changed`, fără modificări sau
cookie-uri de autentificare noi. Refresh și logout verifică și contul tokenului
de refresh atunci când cookie-ul access lipsește sau a expirat.

Headerul rămâne opțional pentru compatibilitatea clienților API existenți.
Clienții fără header, inclusiv un bundle SPA vechi păstrat deschis, nu beneficiază
de această legare a intenției. După actualizare trebuie reîncărcat frontendul.
Integrarea F# Finance are autentificarea sa separată: endpointurile și
`schemaVersion: 1` nu se schimbă, iar contextul browserului nu intră în export.

## Coordonarea browserului

Web Lock-ul existent `expense-session-refresh` ordonează operațiile care scriu
cookie-uri: refresh, login, verificare email, resetare parolă, logout și operațiile
de cont care reemit/revocă sesiunea. Citirea inițială a identității folosește lock
shared; refresh-ul se face după eliberarea acestuia, evitând lock-uri imbricate.
Operațiile sensibile de cont reverifică identitatea în lock înaintea trimiterii.

BroadcastChannel și evenimentele storage transportă numai un ID aleator al
tranziției și faza changing/ready. Nu transportă tokenuri, email, username sau
accountContext. Contextul contului rămâne în memorie. Focus/pageshow și cererile/
răspunsurile reverifică markerul persistent, inclusiv după suspendarea unui tab.
Un marker changing abandonat poate fi închis după obținerea lock-ului și citirea
identității reale. Mesajele întârziate nu suprascriu markerul mai nou din storage.

Schimbarea externă golește datele, formularele, dialogurile, exporturile, stările
busy și notificările locale; identitatea se citește din nou. Generația API și
epoca UI resping răspunsurile vechi și împiedică replay-ul unui 401 în alt cont.
Un 409 de context produce resynchronizare, fără replay automat al operației.
Citirile `/auth/me` au limita separată 60/min/IP, sub limita globală existentă;
operațiile de parolă/OTP păstrează limita strictă 10/min/IP.

## Limite și verificare

Fără Web Locks, serializarea este numai în propriul tab: ordinea cookie-urilor
între taburi nu poate fi garantată. Fără storage și BroadcastChannel, interfața
poate afișa temporar datele încărcate anterior până la următoarea interacțiune.
Legarea de cont pe server continuă să refuze o cerere care ar opera asupra altui
cont. Anularea locală nu revocă o operație deja acceptată de server și nu protejează
împotriva unui XSS cu execuție în aceeași origine.

Testele API folosesc conturi sintetice, inclusiv refresh fără cookie access,
logout, modificări de credențiale, context invalid și compatibilitate fără header.
Testele browser folosesc exclusiv API sintetic și modulele reale ale clientului.
Nu necesită conturi, emailuri sau cereri autentificate în producție.
