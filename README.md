# CourseHub

A self-hosted, Udemy-style player for video courses you already own. Point it at a folder of courses and you get a course library with progress tracking, notes, transcripts and stats, for everyone in your household. It's in the spirit of Audiobookshelf (for audiobooks) and Komga (for comics).

Built with ASP.NET Core (.NET 10), Blazor Server and SQLite. It ships as one Docker image for **amd64** and **arm64**, with ffmpeg and whisper.cpp included.

## Features

- **Library:** every folder becomes a course. Sub-folders become sections, and long flat courses are split into sections automatically. Video thumbnails and lesson lengths are generated for you. New folders appear on their own.
- **Player:** resumes where you stopped and autoplays the next lesson. It also has picture-in-picture, a mini player when you scroll, speed in 0.1× steps, a skip-intro setting per course, and keyboard shortcuts.
- **Multiple users:** everyone has their own progress, notes, bookmarks, stats, favourites and learning paths. Admins manage users.
- **Notes & bookmarks:** both are tied to a moment in the video, and you can export them as Markdown.
- **Transcripts:** CourseHub uses `.srt`/`.vtt` files next to your videos, or creates transcripts on your server with Whisper (nothing leaves your server). You get captions, a transcript that follows along, and search across everything said in every lesson.
- **Code resources:** code folders that come with a course can be browsed with syntax highlighting or downloaded as a zip.
- **Stats:** daily watch time, streaks, and a finish-date estimate for each course.
- **Learning paths:** put courses in order, for example *Getting Started Rust → Deep Dive Rust*.
- **Phones and tablets:** the layout adapts to small screens, and you can install CourseHub as an app (PWA).
- **Backup:** each user can export and restore their own data as JSON.

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
│   ├── 2. Setup.mp4
│   ├── 2. Setup.srt          ← optional subtitles (same name as the video)
│   └── cover.jpg             ← optional cover image
├── System Design/
│   ├── 01 Intro.html         ← HTML and PDF lessons work too
│   ├── Videos/               ← sub-folders become sections
│   │   ├── 001 Bitly.mp4
│   │   └── 002 Dropbox.mp4
│   └── demo-app/             ← folders without lessons (or with a .csproj/package.json…) become "Resources"
└── …
```

- Videos can be `.mp4 .m4v .mov .webm .mkv`, and readings can be `.html .htm .pdf`. MP4 with H.264 or AAC plays in every browser. `.mkv` won't play in Safari, and CourseHub doesn't transcode.
- Lessons are sorted naturally, so `2.` comes before `10.`. Leading numbers are removed from titles.
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

### Configuration

Everything has a sensible default in the image. Override settings with environment variables:

| Variable | Default (Docker) | What it does |
| --- | --- | --- |
| `TZ` | `Etc/UTC` | Time zone used for daily stats and streaks. |
| `COURSEHUB_ADMIN_USER` / `COURSEHUB_ADMIN_PASSWORD` | – | Creates the first admin on startup, which skips the setup page. These only apply while no users exist. |
| `CourseHub__TrustForwardedHeaders` | `false` | Set to `true` behind a reverse proxy, so CourseHub sees real client IPs and https. |
| `CourseHub__RescanIntervalMinutes` | `30` | Rescans on a timer, in addition to watching for file changes. File watching doesn't work on SMB/NFS shares or Docker Desktop mounts. `0` turns the timer off. |
| `CourseHub__WatchForChanges` | `true` | Watches `/courses` and rescans within seconds of a change. |
| `CourseHub__CoursesRoot` | `/courses` | The folder that holds your courses. |
| `ConnectionStrings__Progress` | `Data Source=/config/coursehub.db` | The SQLite database. |
| `CourseHub__KeysFolder` | `/config/keys` | Keys that encrypt sign-in cookies. Keep them, or everyone gets signed out. |
| `MediaTools__DataFolder` | `/config/storage` | Thumbnails, generated transcripts and the speech model. |
| `MediaTools__WhisperModel` | `ggml-base.en.bin` | The Whisper model file. For non-English courses, use a multilingual model such as `ggml-base.bin` together with `MediaTools__WhisperLanguage=auto`. |
| `MediaTools__WhisperModelUrl` | Hugging Face URL | Where Settings downloads the model from. |
| `ASPNETCORE_HTTP_PORTS` | `8080` | The port inside the container. |

### What's in `/config`

```
config/
├── coursehub.db            users, progress, notes, stats, paths
├── keys/                   sign-in cookie keys
└── storage/
    ├── thumbs/             recreated automatically if deleted
    ├── transcripts/        Whisper output (slow to recreate, so back it up)
    └── models/             speech model (download again from Settings)
```

To back up, copy the `config` folder while the container is stopped. Each user can also export their own data from **Settings → Backup**.

## Transcripts

1. As admin, open **Settings → Transcripts & subtitles** and click **Download** for the speech model (about 148 MB).
2. Click **Transcribe everything**, or transcribe a single course or lesson from its Transcript tab.

Whisper runs on the CPU. On a modern desktop it processes about 10–20 minutes of audio per minute; a low-power NAS is several times slower. Jobs run one at a time in the background, and queued jobs are lost if the container restarts, but finished transcripts are kept. On x86 the image picks the fastest CPU instructions available, including on older CPUs without AVX.

Subtitle files you already have (`Lesson name.srt`/`.vtt` next to `Lesson name.mp4`) are used straight away and take priority over generated transcripts.

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

- The first account is the admin. Admins add other users in **Settings → Users** and can make them admins, reset their passwords or delete them. Deleting a user also deletes their data.
- Passwords are hashed with ASP.NET Core Identity's PBKDF2 hasher. After 8 failed attempts, sign-in is blocked for 15 minutes for that username and IP.
- Changing a password or role signs that user out on every device.
- Everything except `/health` and the sign-in pages requires an account, including video files and thumbnails.
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
