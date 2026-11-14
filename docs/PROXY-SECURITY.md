# Identitatea clientului și limitarea cererilor

Remediere 14.11.2026: `ClientIp.Partition` prefera direct `CF-Connecting-IP` primit în
cerere. Un client cu acces la origin îl putea schimba pentru a evita rate limiting.
nginx nu aplica snippetul de verificare Cloudflare în acest vhost și propaga un lanț
X-Forwarded-For extins de la client; API avea încredere implicit în toate rețelele private.

Acum nginx aplică lista de transporturi Cloudflare/loopback verificată, suprascrie
X-Forwarded-For cu `$remote_addr`, fixează schema și elimină CF-Connecting-IP înainte
de upstream. API folosește numai RemoteIpAddress normalizat de middleware: un singur
hop, antete simetrice, numai proxy-uri configurate explicit. Implicit acceptă doar
127.0.0.1/32 și ::1/128. În acest Docker deployment se configurează gateway-ul bridge
real /32 în `ForwardedHeaders__TrustedNetworks`; nu se presupune că orice container
dintr-o rețea RFC1918 este nginx. CIDR invalid/gol sau /0 oprește pornirea.

`sudo python3 scripts/harden-proxy.py` pregătește vhostul și gateway-ul în `.env`,
fără restart API sau DB, păstrează originalele privat și revine la ele dacă verificarea
nginx/health eșuează. Se rulează o singură dată înaintea activării imaginii API noi
prin utilitarul existent de deploy. Configurația trebuie revizuită dacă bridge-ul Docker
este recreat cu alt gateway. Portul API rămâne publicat exclusiv pe 127.0.0.1.

Limita de încredere rămâne hostul: un proces ostil cu acces local la socketul nginx ori
gateway poate pretinde antete de proxy. Noul cod nu pretinde o identitate criptografică
a nginx. Nu expuneți portul API direct și nu lărgiți rețelele de încredere automat.

45 teste backend trecute, inclusiv middleware real pentru proxy necunoscut, gateway
explicit, antete multiple/asimetrice și configurații invalide. Antetele brute nu schimbă
partiția de limitare. Nicio probă brute-force nu este rulată pe conturi reale.

[Documentația Microsoft pentru proxy-uri](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0).
