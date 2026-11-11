// CourseHub service worker: makes the app installable and plays downloaded lessons offline.
const SHELL = "coursehub-shell-v3";
const OFFLINE = "coursehub-offline-v1";
const SHELL_FILES = ["/offline.html", "/offline.js", "/app.css", "/icon.svg", "/manifest.webmanifest"];

self.addEventListener("install", (e) => {
  e.waitUntil(caches.open(SHELL).then((c) => c.addAll(SHELL_FILES)).then(() => self.skipWaiting()));
});

self.addEventListener("activate", (e) => {
  e.waitUntil((async () => {
    for (const key of await caches.keys()) if (key.startsWith("coursehub-shell-") && key !== SHELL) await caches.delete(key);
    await self.clients.claim();
  })());
});

self.addEventListener("fetch", (e) => {
  const url = new URL(e.request.url);
  if (url.origin !== location.origin) return;

  if (url.pathname.startsWith("/offline-media/") || url.pathname.startsWith("/offline-thumb/")) {
    e.respondWith(fromOfflineCache(e.request, url.pathname));
    return;
  }
  if (url.pathname === "/offline.html" || url.pathname === "/offline.js") {
    // Network first so updates arrive, cache when offline.
    e.respondWith(fetch(e.request).then((r) => { const copy = r.clone(); caches.open(SHELL).then((c) => c.put(url.pathname, copy)); return r; })
      .catch(() => caches.match(url.pathname)));
    return;
  }
  if (e.request.mode === "navigate") {
    // Pages need the server. Only step in when the device is offline: then show the downloads page.
    // (Online navigations are left to the browser untouched, which is the most robust behaviour.)
    if (!self.navigator.onLine) e.respondWith(caches.match("/offline.html"));
    return;
  }
  if (SHELL_FILES.includes(url.pathname)) {
    e.respondWith(fetch(e.request).catch(() => caches.match(url.pathname)));
  }
});

async function fromOfflineCache(request, path) {
  const cache = await caches.open(OFFLINE);
  const cached = await cache.match(path);
  if (!cached) return new Response("Not downloaded", { status: 404 });
  const range = request.headers.get("range");
  if (!range) return cached;
  // Video elements ask for byte ranges; answer them from the stored file so seeking works offline.
  const blob = await cached.blob();
  const size = blob.size;
  const m = /bytes=(\d*)-(\d*)/.exec(range);
  let start = m && m[1] ? Number(m[1]) : 0;
  let end = m && m[2] ? Number(m[2]) : size - 1;
  if (m && !m[1] && m[2]) { start = Math.max(0, size - Number(m[2])); end = size - 1; }
  end = Math.min(end, size - 1);
  if (start >= size || start > end) return new Response(null, { status: 416, headers: { "Content-Range": `bytes */${size}` } });
  return new Response(blob.slice(start, end + 1), {
    status: 206,
    headers: {
      "Content-Type": cached.headers.get("Content-Type") || "video/mp4",
      "Content-Range": `bytes ${start}-${end}/${size}`,
      "Content-Length": String(end - start + 1),
      "Accept-Ranges": "bytes",
    },
  });
}
