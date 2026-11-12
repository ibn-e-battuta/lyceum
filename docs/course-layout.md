# Course folder rules

How Lyceum turns folders into courses, and how to lay out a course so it shows up the way you expect.

## What Lyceum can show

| File | What it becomes |
| --- | --- |
| `.mp4` `.m4v` `.mov` `.webm` `.mkv` `.avi` `.wmv` `.flv` `.ts` `.mpg` `.mpeg` | **Video lesson.** Files a browser can't play are repackaged or converted the first time they're opened. |
| `.pdf` | **Reading lesson**, shown in the browser's PDF viewer. |
| `.html` `.htm` | **Reading lesson**, shown as a page. Its images, stylesheets and links to other pages work (see [Pages](#html-and-markdown-pages)). |
| `.md` `.markdown` | **Reading lesson**, rendered with headings, tables, images and highlighted code. `README`, `CHANGES`, `CHANGELOG`, `LICENSE`, `CONTRIBUTING`, `CODE_OF_CONDUCT` and `SECURITY` stay downloads. |
| `.srt` `.vtt` | **Subtitles** for the video with the same name. |
| `cover.jpg` and similar | **Course cover** (see [Covers](#covers)). |
| `course.json` | **Course details**: title, author, description, cover and sections. |
| Anything else (`.epub`, `.zip`, `.pptx`, `.docx`, `.txt`, images…) | **Download only.** It's an attachment of a lesson or a file of its section. |

EPUB, Word and PowerPoint files can't be read inside Lyceum. Keep a PDF (or Markdown/HTML) copy beside them if you want to read them there.

## How Lyceum reads a folder

1. **Every folder directly inside the courses folder is one course.** Its name is the course title unless `course.json` sets one. A folder with no lessons anywhere inside it is skipped.
2. **Lessons directly in the course folder.**
   - With no sub-folders and **12 or more** lessons, Lyceum guesses sections. A lesson named "Introduction", "Intro" or "Overview" starts a new section, and one ending in "Recap", "Wrap-up", "Summary" or "Conclusion" closes it.
   - With fewer than 12, it shows one "Course Content" section.
   - When the course also has sub-folders, these lessons become a "Main Lessons" section that comes first.
   - `course.json` `sections` replaces the guessing.
3. **Each sub-folder that holds lessons is a section**, named after the folder.
   - Deeper folders become nested sections, shown as `Part / Chapter`. A folder's own lessons come before those of its sub-folders.
   - A `Docs` folder directly in the course comes after the other sections.
4. **A folder with no lessons inside is a Resource**, a browsable and downloadable folder.
   - Code folders are always Resources, even with lessons inside. Lyceum recognises them by one of these files: `*.csproj`, `*.fsproj`, `*.sln`, `*.slnx`, `package.json`, `Cargo.toml`, `go.mod`, `pyproject.toml`, `pom.xml`, `build.gradle` or `build.gradle.kts`.
   - `assets`, `_assets` and `static` folders that hold no lessons are page plumbing (images and styles), so they're hidden.
   - `bin`, `obj`, `node_modules`, `wwwroot`, `Properties` and `target` folders are never read.
5. **Order is natural sort by file name**, so `2.` comes before `10.`. Folder names sort the same way.
6. **Titles come from file names.**
   - A leading number is removed when a space follows it: `12. `, `12 - `, `12) `, `12_ ` or just `12 `.
   - A trailing ` (1)` or `_2` is removed.
   - **URL-style names** have no spaces, like `02.05-method-based-routing.html` or `index.html`. HTML pages named like that use their `<title>`, minus a " — Site name" ending. Other URL-style names lose the leading number and their dashes: `getting-started.pdf` → "Getting started".
   - URL-style folder names are tidied the same way: `lets-go.html/` → "Lets go".
7. **Attachments.** Any file whose name starts with a video's name, followed by a space, `.`, `-`, `_`, `(` or `[`, belongs to that video.
   - For example `3. Generics - slides.pdf`, `3. Generics.md` or `3. Generics_code.zip` belong to `3. Generics.mp4`.
   - Such files are attachments even when they're PDFs or Markdown. They are not separate lessons.
8. **Subtitles** pair the same way: `3. Generics.srt`, `3. Generics.en.vtt`. If there are several, `.vtt` wins.
9. **Section files.** Other files in a lesson folder are listed as that section's downloads. Files in the course folder itself go with the first section.
10. **`index.html` beside other HTML pages** is treated as a duplicate cover or contents page and becomes a download. On its own, it's a lesson.
11. **Lectern documents.** A folder containing Lectern's `.sync-state.json` is a synced document:
    - its Markdown chapters are the lessons;
    - its whole-document PDF, `README.md` and `CHANGES.md` are downloads.
12. **Hidden things are ignored.** That covers names starting with `.`, plus `Thumbs.db` and `desktop.ini`.

## Rules for organising a course

**Must do**

- One course = one top-level folder. Never put lessons loose in the courses folder itself.
- **Number every lesson and every section folder**, with a space after the number: `01. Welcome.mp4`, `02 - Setup.mp4`, `03. Advanced Topics/`. Without numbers the order is alphabetical, and that's rarely the course order.
- Name lessons for people: `07. Dependency Injection.mp4`, not `file_uploads_sites_152896_video_…_007DI.mp4`.
- **Don't rename or move files after you've started a course.** Progress, notes and bookmarks are tied to the file's path. A renamed file is a new lesson.
- Videos need an audio track, and must not be half-downloaded. Lyceum can't repair a broken file.

**Should do**

- Use sub-folders for sections once a course has more than about 15 lessons. A single level is enough: `Course/03. Section/05. Lesson.mp4`.
- Put slides, handouts and zips next to their video, named after it: `05. Lesson - slides.pdf`.
- Keep code in its own folder, such as `Course/code/` or `Course/03. Section/demo-app/`. A project file inside makes it a Resource.
- **Keep one readable copy of a book.** If you have the PDF and HTML versions of the same book, both are listed as lessons. Keep whichever you prefer to read in Lyceum; an EPUB copy only appears as a download.
- Add a `cover.jpg` and a `course.json` with the author and a short description. The course page and cards use them.
- Avoid trailing spaces in folder names (`04. Now Playing `). They work, but are easy to break when copying between systems.

## HTML and Markdown pages

Pages are served from their own folder, so they work the way they do on disk:

- **Images, stylesheets and scripts** a page refers to by relative path load, such as `assets/css/main.css` or `images/diagram.png`. They must be in the page's folder or below it; paths starting with `../` don't resolve.
- **Links to another lesson** of the course open that lesson in the player, so progress and the outline follow. For example, a book's "Next ›" link or `[Part 2](02%20-%20Part%202.md)`.
- **Links to other sites** open in a new tab.
- **Single-file pages** saved with everything inlined also work. Examples are pages made with the SingleFile extension, or saved as "Web Page, Single File".
- **Markdown** is rendered with tables, task lists, footnotes and highlighted code, in light or dark to match Lyceum. Raw HTML inside Markdown isn't rendered, for safety. A leading `<!-- … -->` comment, like the one Lectern adds, is dropped.

## Covers

Lyceum uses the first match in the course folder for `cover`, `folder`, `poster`, `thumb`, `thumbnail`, `artwork`, `course` or `banner`, with `.jpg`, `.jpeg`, `.png`, `.webp`, `.avif` or `.gif`. Names are matched ignoring case. Or point `course.json` `cover` at any image inside the course. Without one, it uses a frame from the first video, which needs ffmpeg.

## Example

```
courses/
└── Mastering C#/
    ├── cover.jpg
    ├── course.json
    ├── 01. Getting Started/
    │   ├── 01. Welcome.mp4
    │   ├── 01. Welcome.srt
    │   ├── 02. Installing .NET.mp4
    │   └── 02. Installing .NET - slides.pdf   ← attachment of lesson 02
    ├── 02. Generics/
    │   ├── 01. Why Generics.mp4
    │   ├── 02. Constraints.mp4
    │   └── 03. Cheat sheet.md                 ← reading lesson
    ├── 03. The Book/                          ← an HTML book: pages are lessons
    │   ├── 01-introduction.html
    │   ├── 02-generics.html
    │   └── assets/                            ← hidden, used by the pages
    ├── code/
    │   └── GenericsDemo/GenericsDemo.csproj   ← Resource
    └── Docs/                                  ← Lectern, listed last
        └── Generics Guide/
            ├── .sync-state.json
            ├── 00 - Generics Guide.md         ← lessons
            ├── 01 - Constraints.md
            ├── Generics Guide.pdf             ← download
            ├── README.md, CHANGES.md          ← downloads
            └── assets/
```
