# CourseHub on Synology DSM 7.2+ (Container Manager)

1. In **File Station**, create `docker/coursehub` for CourseHub's data. Your courses can live anywhere, e.g. `video/courses`.
2. Find your user's ids: **Control Panel → Terminal & SNMP** → enable SSH, then `ssh you@nas` and run `id` (typically `uid=1026 gid=100`).
3. **Container Manager → Project → Create**, name `coursehub`, path `/volume1/docker/coursehub`, choose *Create docker-compose.yml* and paste
   [`docker-compose.yml`](docker-compose.yml) (adjust paths and the `user:` line).
4. Open `http://<nas>:8080` and create the admin account.

**Remote access:** use **Control Panel → Login Portal → Advanced → Reverse Proxy**: source `https://courses.example.com`, destination
`http://localhost:8080`, and under *Custom Header* add **WebSocket** (CourseHub's interface needs it). Then set
`CourseHub__TrustForwardedHeaders: "true"`.

**Hardware conversion:** Synology models with Intel CPUs (e.g. DS920+, DS423+) can keep the `devices` entry and use **VAAPI**
in Settings → Video playback. ARM/AMD Ryzen embedded models: leave it out and use Software.

File watching doesn't work reliably on Synology shares, so the compose file rescans every 15 minutes; press *Rescan now* in
Settings for an immediate update.
