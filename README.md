# CourseHub

A self-hosted, Udemy-style player for video courses you already own. Point it at a folder of courses and you get a course library with progress tracking, notes, transcripts, AI study tools and stats, for everyone in your household. It's in the spirit of Audiobookshelf (for audiobooks) and Komga (for comics).

Built with ASP.NET Core (.NET 10), Blazor Server and SQLite. It ships as one Docker image for **amd64** and **arm64**, with ffmpeg and whisper.cpp included. Licensed under [AGPL-3.0](LICENSE).

## Features

- **Library:** every folder becomes a course. Sub-folders become sections, and long flat courses are split into sections automatically. Thumbnails, lesson lengths and chapters are read from the files. New folders appear on their own. You can have **several libraries** (for example "Programming" and "Kids") and choose which users see which.
- **Plays every video:** MP4, WebM, MKV, AVI, MOV, WMV, TS… Files a browser can't play are repackaged (fast and lossless) or converted to H.264 the first time someone opens them. Conversion can use **Intel/AMD (VAAPI, QSV), NVIDIA (NVENC) or Apple (VideoToolbox)** hardware.
- **Player:** resumes where you stopped and autoplays the next lesson. It also has chapter markers, picture-in-picture, a mini player when you scroll, speed in 0.1× steps, a skip-intro setting per course, and keyboard shortcuts.
- **Multiple users:** everyone has their own progress, notes, bookmarks, stats, favourites and learning paths. Users can sign in with a password or with **single sign-on** (Authentik, Authelia, Keycloak, Zitadel…).
- **Notes & bookmarks:** both are tied to a moment in the video, and you can export them as Markdown.
- **Transcripts:** CourseHub uses `.srt`/`.vtt` files next to your videos, or creates transcripts on your server with Whisper, with optional English captions for other languages. You get captions, a transcript that follows along, and search across everything said in every lesson.
- **AI study tools** (optional, with a local model such as Ollama): lesson summaries, quizzes, flashcards (with Anki export), "ask this course" with links to the exact moments, and caption translation into 20 languages.
- **Attachments & code:** slides, PDFs and zips named after a lesson appear next to it. Code folders can be browsed with syntax highlighting or downloaded as a zip.
- **Offline:** save lessons or whole courses on a phone or laptop and watch without internet. Progress syncs when you're back online.
- **Stats, paths and motivation:** daily watch time, streaks, finish-date estimates, learning paths, printable completion certificates, and a household activity feed.
- **For admins:** a dashboard (who's watching now, per-user activity, storage per library, background jobs), notifications to **ntfy** or **webhooks**, and a **REST API** with API keys.
- **Everywhere:** light and dark themes; English, German, Spanish and French; installable as an app (PWA) on phones and desktops.

## Install

| Platform | Guide |
| --- | --- |
| Docker Compose | [below](#quick-start-docker) |
| Unraid | [`deploy/unraid/coursehub.xml`](deploy/unraid/coursehub.xml): copy to `/boot/config/plugins/dockerMan/templates-user/`, then *Docker → Add Container → CourseHub* |
| TrueNAS SCALE 24.10+ | [`deploy/truenas`](deploy/truenas/README.md) |
| Synology DSM 7.2+ | [`deploy/synology`](deploy/synology/README.md) |
| Kubernetes (Helm) | [`deploy/helm/coursehub`](deploy/helm/coursehub/values.yaml): `helm install coursehub ./deploy/helm/coursehub --set courses.nfs.server=…,courses.nfs.path=…` |

Replace `OWNER` in the image name (`ghcr.io/OWNER/coursehub`) with the GitHub account that publishes it.

## Quick start (Docker)

```yaml
# docker-compose.yml
services:
  coursehub:
    image: ghcr.io/OWNER/coursehub:latest
    container_name: coursehub
    user: "1000:1000"                        # your user id/group id (`id -u`, `id -g`)
    ports:
      - "8080:8080"
    volumes:
      - /path/to/your/courses:/courses:ro
      - ./config:/config
    environment:
      TZ: Europe/London
    restart: unless-stopped
```

```bash
mkdir -p config && docker compose up -d
```

Open `http://<server>:8080` and create the admin account. CourseHub scans `/courses` right away.

To build the image yourself instead of pulling it, replace `image:` with `build: .` (whisper.cpp takes a few minutes to compile the first time):

```bash
docker build -t coursehub .
```

### Course folder layout

```
courses/
├── Mastering C#/
│   ├── 1. Welcome.mp4
│   ├── 2. Setup.mkv
│   ├── 2. Setup.srt          ← optional subtitles (same name as the video)
│   ├── 2. Setup - slides.pdf ← attachment: anything starting with a lesson's name belongs to it
│   ├── cheatsheet.md         ← other files are shown as section downloads
│   └── cover.jpg             ← optional cover image
├── System Design/
│   ├── 01 Intro.html         ← HTML and PDF lessons work too
│   ├── Videos/               ← sub-folders become sections
│   │   ├── 001 Bitly.mp4
│   │   └── 002 Dropbox.mp4
│   └── demo-app/             ← folders without lessons (or with a .csproj/package.json…) become "Resources"
└── …
```

- Lessons are sorted naturally, so `2.` comes before `10.`. Leading numbers are removed from titles.
- Chapters embedded in MP4/MKV files appear as markers under the video and in a Chapters tab.
- To set a course's title, author, description, cover or sections, add a `course.json` to its folder:

```json
{
  "title": "Rust Deep Dive",
  "author": "Jane Doe",
  "description": "Traits, smart pointers, concurrency…",
  "cover": "cover.jpg",
  "sections": [
    { "title": "Traits and types", "start": 1 },
    { "title": "Smart pointers", "start": 15 }
  ]
}
```

  `start` is the lesson number where a section begins. Admins can also edit these details in the course's Overview tab, but only if `/courses` is mounted read-write.

### More libraries

Mount extra folders into the container, then add them in **Settings → Libraries** using the path inside the container:

```yaml
    volumes:
      - /path/to/your/courses:/courses:ro
      - /path/to/kids-courses:/media/kids:ro
```

In **Settings → Users**, untick libraries a user shouldn't see. Admins always see everything.

### Configuration

Everything has a sensible default in the image. Override settings with environment variables; most options for AI, notifications, transcription and video conversion are changed in **Settings** instead.

| Variable | Default (Docker) | What it does |
| --- | --- | --- |
| `TZ` | `Etc/UTC` | Time zone used for daily stats and streaks. |
| `COURSEHUB_ADMIN_USER` / `COURSEHUB_ADMIN_PASSWORD` | – | Creates the first admin on startup, which skips the setup page. These only apply while no users exist. |
| `CourseHub__TrustForwardedHeaders` | `false` | Set to `true` behind a reverse proxy, so CourseHub sees real client IPs and https. |
| `CourseHub__RescanIntervalMinutes` | `30` | Rescans on a timer, in addition to watching for file changes. File watching doesn't work on SMB/NFS shares or Docker Desktop mounts. `0` turns the timer off. |
| `CourseHub__WatchForChanges` | `true` | Watches library folders and rescans within seconds of a change. |
| `CourseHub__CoursesRoot` | `/courses` | The folder of the main library. |
| `Oidc__…` | – | Single sign-on; see [below](#single-sign-on). |
| `ConnectionStrings__Progress` | `Data Source=/config/coursehub.db` | The SQLite database. |
| `CourseHub__KeysFolder` | `/config/keys` | Keys that encrypt sign-in cookies. Keep them, or everyone gets signed out. |
| `MediaTools__DataFolder` | `/config/storage` | Thumbnails, converted videos, transcripts and speech models. |
| `MediaTools__WhisperModelUrl` | Hugging Face URL | Where Settings downloads speech models from. |
| `ASPNETCORE_HTTP_PORTS` | `8080` | The port inside the container. |

### What's in `/config`

```
config/
├── coursehub.db            users, progress, notes, stats, paths, settings
├── keys/                   sign-in cookie keys
└── storage/
    ├── thumbs/             recreated automatically if deleted
    ├── transcodes/         converted videos (size-capped, recreated on demand)
    ├── transcripts/        Whisper output and caption translations (slow to recreate, so back them up)
    ├── models/             speech models (download again from Settings)
    └── probe-cache.json    video codec information
```

To back up, copy the `config` folder while the container is stopped. Each user can also export their own data from **Settings → Backup**.

## Video compatibility & hardware acceleration

When someone opens a lesson, their browser reports whether it can play the file. If it can't, CourseHub does one of two things:

- **Repackage** the video into MP4 when only the container is the problem (MKV, TS, MP4 with AC-3 audio…). This is lossless and takes seconds.
- **Convert** the video to H.264 for codecs the browser doesn't support (MPEG-4/DivX, WMV, 10-bit H.264, HEVC in some browsers…). This takes about as long as the video on a CPU, and much less with hardware.

Converted copies are cached (50 GB by default, least recently watched removed first). You can also convert in the background as soon as new videos are found, or convert everything at once from **Settings → Video playback**.

Pick the encoder in **Settings → Video playback**:

| Encoder | Hardware | Docker setup |
| --- | --- | --- |
| Software (libx264) | any CPU | nothing |
| VAAPI | Intel (incl. Quick Sync) and AMD GPUs on Linux | `devices: ["/dev/dri:/dev/dri"]` and `group_add: ["video", "render"]` (or their numeric ids) |
| NVENC | NVIDIA GPUs | install the NVIDIA Container Toolkit and add `deploy.resources.reservations.devices: [{driver: nvidia, count: all, capabilities: [gpu, video]}]` |
| VideoToolbox | Macs, when running from source | nothing |

If hardware conversion fails, the job's error says so; switch back to Software.

## Transcripts

1. As admin, open **Settings → Transcripts & subtitles**, choose a speech model and click **Download**. `base.en` (148 MB) is English-only and fast; for other languages pick a multilingual model (`base`, `small`, `large-v3-turbo`) and set the spoken language, or choose *Detect automatically*.
2. Click **Transcribe everything**, or transcribe a single course or lesson from its Transcript tab. You can also have new videos transcribed automatically when they're added.
3. Optional: **Also create English captions for non-English lessons** uses Whisper's built-in translation. With AI turned on, anyone can translate a lesson's captions into other languages from the Transcript tab.

Whisper runs on the CPU. On a modern desktop it processes about 10–20 minutes of audio per minute; a low-power NAS is several times slower. Jobs run one at a time in the background, and queued jobs are lost if the container restarts, but finished transcripts are kept. On x86 the image picks the fastest CPU instructions available, including on older CPUs without AVX.

Subtitle files you already have (`Lesson name.srt`/`.vtt` next to `Lesson name.mp4`) are used straight away and take priority over generated transcripts.

## AI study tools

The AI tools use the transcripts, so transcribe a course first. They work with any **OpenAI-compatible** server, for example:

| Server | Settings → AI → Server URL |
| --- | --- |
| Ollama on the same machine | `http://localhost:11434/v1` (from Docker: `http://host.docker.internal:11434/v1` plus `extra_hosts: ["host.docker.internal:host-gateway"]`) |
| Ollama in the same compose file | `http://ollama:11434/v1` |
| LM Studio, llama.cpp server, vLLM | their `/v1` address |
| OpenAI or another cloud API | the provider's URL and an API key; transcripts are then sent to that provider |

Pick a model your server has (for example `llama3.2`, `qwen2.5:7b` or `mistral-nemo`), then use **Test connection**. Larger models write better quizzes. Summaries, quizzes and flashcards are generated once per lesson and shared by everyone; **Regenerate** replaces them.

## Single sign-on

CourseHub supports any OpenID Connect provider. Create a client with redirect URI `https://<your CourseHub>/signin-oidc`, then set:

```yaml
    environment:
      Oidc__Authority: https://auth.example.com/application/o/coursehub/   # issuer URL
      Oidc__ClientId: coursehub
      Oidc__ClientSecret: <secret>
      Oidc__DisplayName: Authentik            # button label: "Sign in with Authentik"
      Oidc__AdminGroup: coursehub-admins      # optional: members become admins
      # Oidc__DisablePasswordLogin: "true"    # hide the username/password form
      # Oidc__AutoCreateUsers: "false"        # only allow people an admin created first
      # Oidc__MatchExistingByUsername: "true" # link to existing local accounts with the same username
      # Oidc__Scopes: "openid profile email groups"
      # Oidc__GroupsClaim: groups
```

New people get an account on their first sign-in. If no accounts exist yet, the first person becomes an admin.

## Notifications

In **Settings → Notifications**, add any number of destinations:

- **ntfy**: the topic URL, such as `https://ntfy.sh/my-courses` or your own server, with an optional access token. You get phone push notifications with a link to the course; set the public address so the links work.
- **Webhook**: a JSON `POST` (`{ event, title, message, url, data, timestamp }`) to Home Assistant, n8n, Node-RED, or Discord or Slack through a bridge. The optional token is sent as `Authorization: Bearer`.

Events: new course, course removed, transcription batch finished, and someone finished a course (only for people who share their activity).

## REST API

Create a key in **Settings → API keys** and send it as `Authorization: Bearer ch_…`. The OpenAPI description is at `/openapi/v1.json`.

```bash
curl -H "Authorization: Bearer $KEY" https://courses.example.com/api/v1/continue
```

The endpoints cover libraries, courses, lessons (with subtitle, attachment and chapter details), progress, notes, search, stats and activity, plus admin rescans. Video elements can't send headers, so media URLs (`/media`, `/subs`, `/thumb`, `/cover`, `/attachment`) also accept `?api_key=`. Keys act as the user who created them.

## Offline viewing

Use **⬇ Save offline** on a lesson, or **Save whole course offline** at the bottom of the lesson list. Videos are stored in the browser's storage on that device, and **Downloads** lists them and plays them without a connection. Progress you make offline is sent to the server the next time you're online. Offline viewing works best when CourseHub is installed as an app and served over HTTPS.

## Languages

The interface follows the browser language, and you can change it under **Settings → Account**. To add a language, copy `Localization/de.json` to `xx.json` and translate the values; the keys are the English text. Then add the language to `Localizer.Languages` in `Services/Localizer.cs`.

## Reverse proxy

CourseHub's interface uses a live **WebSocket** connection (Blazor Server), so your proxy must forward WebSocket upgrades. Videos use HTTP range requests, which work by default. Also set `CourseHub__TrustForwardedHeaders=true`. CourseHub has to be on its own (sub)domain, such as `courses.example.com`; it can't run under a path like `/coursehub`.

**Caddy**

```
courses.example.com {
    reverse_proxy coursehub:8080
}
```

**Nginx**

```nginx
server {
    server_name courses.example.com;
    client_max_body_size 100m;           # for restoring backups

    location / {
        proxy_pass http://coursehub:8080;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection $connection_upgrade;   # map $http_upgrade $connection_upgrade { default upgrade; '' close; }
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_buffering off;             # stream video directly
        proxy_read_timeout 1h;           # keep the UI connection open
    }
}
```

In **Nginx Proxy Manager**, turn on *Websockets Support* for the proxy host.

## Users & security

- The first account is the admin. Admins add other users in **Settings → Users** and can make them admins, choose their libraries, reset their passwords or delete them. Deleting a user also deletes their data.
- Passwords are hashed with ASP.NET Core Identity's PBKDF2 hasher. After 8 failed attempts, sign-in is blocked for 15 minutes for that username and IP.
- Changing a password or role signs that user out on every device.
- Everything except `/health`, `/offline.html` and the sign-in pages requires an account, including video files and thumbnails, and every file checks the user's library access. The Downloads page only shows what's stored on that device.
- API keys are stored as SHA-256 hashes and shown only once.
- HTML lessons run their own scripts inside CourseHub, as they would in a browser. Only add HTML lessons you trust.
- If you expose CourseHub to the internet, put it behind HTTPS (the Caddy example handles that).

## Upgrading

```bash
docker compose pull && docker compose up -d
```

Database changes are applied automatically on startup. Databases from the early single-user builds are backed up next to the original as `coursehub.db.pre-accounts-*.bak`, and their data goes to the first account you create.

## Running from source

Requirements: .NET 10 SDK. ffmpeg and whisper-cpp are optional; on macOS run `brew install ffmpeg whisper-cpp`.

```bash
dotnet run          # serves http://localhost:5080 and reads courses from the parent folder
```

Local settings are in `appsettings.json`. When CourseHub runs directly on a Mac, admins also get *Open in VS Code / Rider / Finder* buttons for course code. That only works from the Mac itself, and never through a proxy.

To change the database schema:

```bash
dotnet tool restore
dotnet ef migrations add <Name> -o Data/Migrations
```

### Publishing images

`.github/workflows/docker.yml` builds amd64 and arm64 images on native GitHub runners and pushes them to `ghcr.io/<owner>/coursehub`:

- a push to `main` updates `:edge`;
- a tag such as `v1.0.0` publishes `:1.0.0`, `:1.0` and `:latest`.

After the first push, make the package public under GitHub → Packages so others can pull it without signing in.

### Keyboard shortcuts

`Space`/`K` play/pause · `←`/`→` 5s · `J`/`L` 10s · `<`/`>` speed ±0.1 · `↑`/`↓` volume · `F` fullscreen · `M` mute · `P` picture-in-picture · `B` bookmark · `C` captions · `N` next lesson

## License

CourseHub is free software under the [GNU Affero General Public License v3.0](LICENSE). You may use, modify and self-host it. If you run a modified version as a service for others, you must offer them its source code.
