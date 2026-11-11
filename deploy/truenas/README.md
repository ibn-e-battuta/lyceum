# Lyceum on TrueNAS SCALE (24.10 "Electric Eel" and newer)

TrueNAS runs Docker Compose apps directly.

1. Create two datasets, e.g. `tank/media/courses` (your courses) and `tank/apps/lyceum` (Lyceum's data).
   Give the `apps` user (uid 568) read access to the courses and write access to the data dataset.
2. **Apps → Discover Apps → ⋮ → Install via YAML**, name it `lyceum`, and paste [`docker-compose.yml`](docker-compose.yml) with your paths.
3. Open `http://<truenas>:8080` and create the admin account.

For Intel/AMD hardware video conversion keep the `devices` entry and pick **VAAPI** in Lyceum → Settings → Video playback.
For NVIDIA, install the NVIDIA drivers in TrueNAS, replace `devices` with the commented `deploy` block, and pick **NVENC**.
