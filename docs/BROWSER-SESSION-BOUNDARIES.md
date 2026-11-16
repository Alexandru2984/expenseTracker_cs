# Cereri întârziate și schimbarea contextului de autentificare

O probă pe API sintetic a reprodus un export CSV care se descărca după logout:
cererea fusese autorizată înainte, dar fișierul ajungea în browser când interfața
nu mai reprezenta acel cont. Datele din probă sunt sintetice; nu s-a exploatat un
cont real. Aceeași protecție este aplicată exportului de calendar.

Clientul API marchează fiecare cerere cu generația locală a autentificării.
Loginul/bootstrapul identității și resetarea sesiunii avansează generația.
Răspunsurile unei generații vechi sunt respinse ca anulate, inclusiv cele reușite.
Un 401 vechi nu declanșează refresh, replay al unei mutații sau logout în noul
context. După un refresh se verifică din nou generația înainte de replay.
Refresh-urile sunt deduplicate în propria generație; închiderea unei promisiuni
vechi nu șterge starea unui refresh mai nou. Un refresh care așteaptă Web Lock-ul
reverifică generația înainte să facă cereri.

Exporturile au și verificarea epocii UI înaintea creării fișierului și notificării.
Stările busy sunt resetate la logout; finalizarea veche nu suprascrie starea nouă.
Anularea în client nu pretinde că poate revoca o operație deja acceptată de server.
Backendul continuă să valideze sesiunea, ownership-ul și CSRF independent de UI.

Regresii browser: CSV întârziat după logout (reprodus înainte de fix), calendar
întârziat și 401 întârziat pe creare care nu trebuie reluat într-un context ulterior.
Suitele păstrează și proba de refresh simultan între două taburi.

Verificare finală: 148 aserțiuni pentru fiecare rezoluție 1440/390/320 px în
light/dark, în trei loturi (444 execuții, cu probele comune repetate), plus 24
verificări ale panoului Finance. Backendul neschimbat în această etapă are 177
teste trecute pe PostgreSQL prin migrații, fără skip.

Limita etapei inițiale era generația locală instanței SPA. Continuarea
[CROSS-TAB-SESSIONS.md](CROSS-TAB-SESSIONS.md) adaugă coordonarea operațiilor care
scriu cookie-uri, notificarea taburilor și legarea intenției de cont pe server.
Documentul precizează separat limitele browserelor fără Web Locks și ale
clienților vechi care nu trimit headerul de context.
