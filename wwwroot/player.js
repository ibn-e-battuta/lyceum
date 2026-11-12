// Video player glue: resume, progress + watch-time reporting, keyboard shortcuts, PiP,
// mini player, captions, transcript follow-along.
let state = null;

const store = {
  get(key, fallback) {
    try { const v = localStorage.getItem(key); return v === null ? fallback : JSON.parse(v); } catch { return fallback; }
  },
  set(key, value) {
    try { localStorage.setItem(key, JSON.stringify(value)); } catch { /* private mode */ }
  },
};

export function getPref(key, fallback) { return store.get(key, fallback); }
export function setPref(key, value) { store.set(key, value); }
export function getSavedRate() { return Number(store.get("lyceum.rate", 1)) || 1; }
export function getAutoplay() { return store.get("lyceum.autoplay", true) !== false; }
export function setAutoplay(on) { store.set("lyceum.autoplay", !!on); }

export function setRate(rate) {
  store.set("lyceum.rate", rate);
  if (state?.video) state.video.playbackRate = rate;
}

/**
 * Decides how to play a video: the original file if this browser supports its codecs, else a lossless
 * MP4 repackage if the video codec itself is supported, else an H.264 conversion.
 */
export function chooseSource(directMime, remuxMime) {
  const probe = document.createElement("video");
  const ok = (type) => !!type && probe.canPlayType(type) !== "";
  if (ok(directMime)) return "direct";
  if (ok(remuxMime)) return "remux";
  return "h264";
}

export function setSkipIntro(seconds) { if (state) state.skipIntro = seconds; }
export function getTime() { return state?.video?.currentTime ?? 0; }
export function pause() { state?.video?.pause(); }

export function seek(t) {
  const v = state?.video;
  if (!v) return;
  v.currentTime = Math.max(0, t);
  v.play().catch(() => {});
}

export async function togglePip() {
  const v = state?.video;
  if (!v || v.tagName !== "VIDEO") return;
  try {
    if (document.pictureInPictureElement) await document.exitPictureInPicture();
    else if (v.requestPictureInPicture) await v.requestPictureInPicture();
    else if (v.webkitSetPresentationMode) v.webkitSetPresentationMode("picture-in-picture"); // Safari
  } catch (e) { toast("Picture-in-picture isn't available"); }
}

export function toast(text) {
  const el = document.getElementById("player-toast");
  if (!el) return;
  el.textContent = text;
  el.classList.add("show");
  clearTimeout(el._t);
  el._t = setTimeout(() => el.classList.remove("show"), 1100);
}

export function scrollLessonIntoView(id) {
  // Scroll only the sidebar list (desktop). On phones the list sits below the video and the page must not jump.
  const el = document.getElementById(id);
  const list = el?.closest(".curriculum");
  if (!el || !list || list.scrollHeight <= list.clientHeight) return;
  const r = el.getBoundingClientRect(), c = list.getBoundingClientRect();
  if (r.top < c.top || r.bottom > c.bottom) list.scrollTop += r.top - c.top - c.height / 3;
}

/** Asks the server to open a resource folder in VS Code / Rider / Finder. Returns an error message or null. */
export async function openWith(app, resourceId, path) {
  try {
    const r = await fetch(`api/open/${app}/${resourceId}?path=${encodeURIComponent(path || "")}`, {
      method: "POST", headers: { "X-Lyceum": "1" },
    });
    if (r.ok) return null;
    const body = await r.json().catch(() => null);
    return body?.detail || `Could not open (${r.status}).`;
  } catch (e) { return String(e); }
}

function setCaptions(video, on) {
  for (const t of video.textTracks) t.mode = on ? "showing" : "hidden";
}

export function attach(video, viewer, dotnet, lessonId, resumeAt, rate, skipIntro, meta) {
  // Switching between the video and the listening-mode audio replaces the element, and the reference
  // passed in can still be the previous one. Use the element that is in the player now.
  video = viewer?.querySelector(".video-wrap video, .video-wrap audio") || video;
  const previous = state?.video;
  detach();
  // Never leave the old element playing next to the new one (Safari keeps removed audio playing).
  if (previous && previous !== video) { try { previous.pause(); } catch { /* already gone */ } }
  if (!video) return;

  let lastReport = Date.now();
  let lastTick = performance.now();
  let watched = 0;

  const report = () => {
    if (!video.duration || Number.isNaN(video.duration)) return;
    lastReport = Date.now();
    const w = watched;
    watched = 0;
    dotnet.invokeMethodAsync("ReportPosition", lessonId, video.currentTime, video.duration, w).catch(() => {});
  };

  const s = { video, viewer, dotnet, lessonId, skipIntro: skipIntro || 0, locked: false };
  const isVideo = video.tagName === "VIDEO";
  setupMediaSession(video, dotnet, meta);

  const handlers = {
    loadedmetadata: () => {
      video.playbackRate = rate || 1;
      video.volume = Math.min(1, Math.max(0, Number(store.get("lyceum.volume", 1))));
      video.muted = !!store.get("lyceum.muted", false);
      setCaptions(video, !!store.get("lyceum.captions", false));
      if (resumeAt > 5 && resumeAt < video.duration - 10) video.currentTime = resumeAt;
      else if (s.skipIntro > 0 && video.duration > s.skipIntro + 5) video.currentTime = s.skipIntro;
      if (meta?.autoplay !== false) video.play().catch(() => { /* autoplay blocked until the user interacts */ });
    },
    ratechange: () => {
      store.set("lyceum.rate", video.playbackRate);
      dotnet.invokeMethodAsync("OnRateChanged", video.playbackRate).catch(() => {});
    },
    volumechange: () => { store.set("lyceum.volume", video.volume); store.set("lyceum.muted", video.muted); },
    play: () => { lastTick = performance.now(); },
    timeupdate: () => {
      const now = performance.now();
      if (!video.paused && !video.seeking) watched += Math.min((now - lastTick) / 1000, 2);
      lastTick = now;
      if (Date.now() - lastReport > 5000) report();
    },
    pause: report,
    seeked: report,
    ended: () => { report(); dotnet.invokeMethodAsync("OnEnded", lessonId).catch(() => {}); },
  };
  for (const [ev, fn] of Object.entries(handlers)) video.addEventListener(ev, fn);
  // Captions loaded after metadata (track element) need the saved preference applied too.
  video.textTracks.onaddtrack = () => setCaptions(video, !!store.get("lyceum.captions", false));

  const onKey = (e) => {
    const tag = (e.target?.tagName || "").toLowerCase();
    if (["input", "select", "textarea", "button"].includes(tag) || e.target?.isContentEditable || e.metaKey || e.ctrlKey || e.altKey) return;
    const seekBy = (d) => { video.currentTime = Math.max(0, Math.min(video.duration || 0, video.currentTime + d)); };
    const speed = (d) => {
      video.playbackRate = Math.round(Math.min(4, Math.max(0.25, video.playbackRate + d)) * 10) / 10;
      toast(`${video.playbackRate.toFixed(1)}×`);
    };
    switch (e.key) {
      case " ": case "k": case "K":
        if (tag === "video" && e.key === " ") return; // native control handles it
        video.paused ? video.play() : video.pause(); break;
      case "ArrowLeft": if (tag === "video") return; seekBy(-5); break;
      case "ArrowRight": if (tag === "video") return; seekBy(5); break;
      case "ArrowUp": video.volume = Math.min(1, video.volume + 0.1); toast(`Volume ${Math.round(video.volume * 100)}%`); break;
      case "ArrowDown": video.volume = Math.max(0, video.volume - 0.1); toast(`Volume ${Math.round(video.volume * 100)}%`); break;
      case "j": case "J": seekBy(-10); break;
      case "l": case "L": seekBy(10); break;
      case ">": speed(0.1); break;
      case "<": speed(-0.1); break;
      case "m": case "M": video.muted = !video.muted; toast(video.muted ? "Muted" : "Unmuted"); break;
      case "f": case "F": if (!isVideo) return; document.fullscreenElement ? document.exitFullscreen() : video.requestFullscreen?.(); break;
      case "p": case "P": if (!isVideo) return; togglePip(); break;
      case "c": case "C": {
        if (video.textTracks.length === 0) { toast("No captions for this lesson"); break; }
        const on = !store.get("lyceum.captions", false);
        store.set("lyceum.captions", on);
        setCaptions(video, on);
        toast(on ? "Captions on" : "Captions off");
        break;
      }
      case "b": case "B":
        dotnet.invokeMethodAsync("AddBookmark", lessonId, video.currentTime).catch(() => {});
        toast("Bookmarked");
        break;
      case "n": case "N": dotnet.invokeMethodAsync("Next").catch(() => {}); break;
      default: return;
    }
    e.preventDefault();
  };
  document.addEventListener("keydown", onKey);

  const onHide = () => { if (document.visibilityState === "hidden") report(); };
  document.addEventListener("visibilitychange", onHide);
  window.addEventListener("pagehide", report);

  // Mini player: when the video scrolls out of view while playing, pin it to the corner.
  let observer = null;
  const wrap = isVideo ? viewer?.querySelector(".video-wrap") : null;
  if (viewer && wrap && "IntersectionObserver" in window) {
    observer = new IntersectionObserver(([entry]) => {
      const mini = entry.intersectionRatio < 0.35 && !video.paused;
      wrap.classList.toggle("mini", mini || (wrap.classList.contains("mini") && entry.intersectionRatio < 0.35));
    }, { threshold: [0, 0.35, 0.6, 1] });
    observer.observe(viewer);
    const back = wrap.querySelector(".mini-close");
    if (back) back.onclick = (e) => { e.stopPropagation(); viewer.scrollIntoView({ behavior: "smooth", block: "start" }); };
  }

  // Picture-in-picture must be requested in the click itself, so the button is wired here rather than through the server.
  const pipBtn = document.getElementById("pip-btn");
  if (pipBtn) pipBtn.onclick = (e) => { e.preventDefault(); if (isVideo) togglePip(); };

  // If metadata already loaded before we attached (cached file), run the setup now.
  if (video.readyState >= 1) handlers.loadedmetadata();

  Object.assign(s, { handlers, onKey, onHide, report, observer, wrap });
  state = s;
}

// ---------- lock-screen / headset controls ----------

function setupMediaSession(media, dotnet, meta) {
  if (!("mediaSession" in navigator) || !meta) return;
  try {
    navigator.mediaSession.metadata = new MediaMetadata({
      title: meta.title, artist: meta.course, album: "Lyceum",
      artwork: meta.artwork ? [{ src: meta.artwork, sizes: "480x270", type: "image/jpeg" }] : [],
    });
    const set = (action, fn) => { try { navigator.mediaSession.setActionHandler(action, fn); } catch { /* unsupported action */ } };
    set("play", () => media.play());
    set("pause", () => media.pause());
    set("seekbackward", (d) => { media.currentTime = Math.max(0, media.currentTime - (d.seekOffset || 10)); });
    set("seekforward", (d) => { media.currentTime = Math.min(media.duration || 0, media.currentTime + (d.seekOffset || 10)); });
    set("seekto", (d) => { if (d.seekTime != null) media.currentTime = d.seekTime; });
    set("nexttrack", meta.hasNext ? () => dotnet.invokeMethodAsync("Next").catch(() => {}) : null);
    set("previoustrack", meta.hasPrev ? () => dotnet.invokeMethodAsync("Previous").catch(() => {}) : null);
  } catch { /* older browsers */ }
}

/** Kids' daily limit reached: stop and refuse to play until the page is reloaded tomorrow. */
export function lock() {
  if (!state) return;
  state.locked = true;
  state.video.pause();
  state.video.addEventListener("play", () => state?.locked && state.video.pause());
}

/** Sleep timer: lower the volume over a few seconds, then pause and restore the volume. */
export function fadeOutAndPause(seconds) {
  const media = state?.video;
  if (!media || media.paused) return;
  const start = media.volume, steps = Math.max(1, Math.round((seconds || 8) * 10));
  let i = 0;
  const t = setInterval(() => {
    i++;
    media.volume = Math.max(0, start * (1 - i / steps));
    if (i >= steps) { clearInterval(t); media.pause(); media.volume = start; toast("Sleep timer: paused"); }
  }, 100);
}

export function detach() {
  if (!state) return;
  const { video, handlers, onKey, onHide, report, observer, wrap } = state;
  if (!document.contains(video)) { try { video.pause(); } catch { /* already gone */ } }
  report();
  for (const [ev, fn] of Object.entries(handlers)) video.removeEventListener(ev, fn);
  document.removeEventListener("keydown", onKey);
  document.removeEventListener("visibilitychange", onHide);
  window.removeEventListener("pagehide", report);
  observer?.disconnect();
  wrap?.classList.remove("mini");
  state = null;
}

// ---------- transcript follow-along ----------

let transcript = null;

document.addEventListener("timeupdate", (e) => {
  if (!state || e.target !== state.video) return;
  // Chapter bar playhead
  const bar = document.getElementById("chapter-bar");
  if (bar) {
    const d = Number(bar.dataset.duration) || state.video.duration || 0;
    const head = bar.querySelector(".playhead");
    if (head && d > 0) head.style.left = `${Math.min(100, (state.video.currentTime / d) * 100)}%`;
  }
  if (transcript) highlight(state.video.currentTime);
}, true);

function highlight(t) {
  const { container, starts, items } = transcript;
  if (!container.isConnected) { transcript = null; return; }
  // Binary search for the last cue starting at or before t.
  let lo = 0, hi = starts.length - 1, idx = -1;
  while (lo <= hi) {
    const mid = (lo + hi) >> 1;
    if (starts[mid] <= t + 0.2) { idx = mid; lo = mid + 1; } else hi = mid - 1;
  }
  if (idx === transcript.active) return;
  items[transcript.active]?.classList.remove("active");
  transcript.active = idx;
  const el = items[idx];
  if (!el) return;
  el.classList.add("active");
  const follow = document.getElementById("transcript-follow");
  if (follow?.checked) {
    container.scrollTo({ top: el.offsetTop - container.offsetTop - container.clientHeight / 3, behavior: "smooth" });
  }
}

export function attachTranscript(id) {
  const container = document.getElementById(id);
  if (!container) return;
  const items = [...container.querySelectorAll("p[data-s]")];
  transcript = { container, items, starts: items.map((p) => Number(p.dataset.s)), active: -1 };
  container.onclick = (e) => {
    const p = e.target.closest("p[data-s]");
    if (p) seek(Number(p.dataset.s));
  };
  if (state?.video) highlight(state.video.currentTime);
}

// Reading pages hand links to other lessons to the player (the server answers such a link with a postMessage).
let lessonLinks = null;
export function listenLessonLinks(dotnet) {
  if (lessonLinks) window.removeEventListener("message", lessonLinks);
  lessonLinks = null;
  if (!dotnet) return;
  lessonLinks = (e) => {
    if (e.origin === location.origin && typeof e.data?.lyceumLesson === "string") dotnet.invokeMethodAsync("OpenLesson", e.data.lyceumLesson).catch(() => {});
  };
  window.addEventListener("message", lessonLinks);
}
