// Offline downloads: lessons are stored with the Cache API (streamed straight to disk, not memory) and
// served back by the service worker (sw.js), including byte ranges so seeking works.
const CACHE = "coursehub-offline-v1";
const INDEX = "/__offline/index.json";
const QUEUE = "coursehub.offline.progress";

async function cache() { return caches.open(CACHE); }

async function readIndex() {
  const r = await (await cache()).match(INDEX);
  return r ? r.json() : {};
}

async function writeIndex(index) {
  await (await cache()).put(INDEX, new Response(JSON.stringify(index), { headers: { "Content-Type": "application/json" } }));
}

export async function list() {
  return Object.values(await readIndex()).sort((a, b) =>
    a.courseTitle === b.courseTitle ? (a.index ?? 0) - (b.index ?? 0) : a.courseTitle.localeCompare(b.courseTitle));
}

export async function isSaved(id) { return !!(await readIndex())[id]; }

export async function usage() {
  const est = navigator.storage?.estimate ? await navigator.storage.estimate() : {};
  return { used: est.usage ?? 0, quota: est.quota ?? 0 };
}

async function download(meta, onProgress) {
  const res = await fetch(meta.url, { credentials: "same-origin" });
  if (!res.ok) throw new Error(`Download failed (${res.status})`);
  const total = Number(res.headers.get("Content-Length")) || 0;
  const type = res.headers.get("Content-Type") || "video/mp4";
  // Split the stream: one copy goes into the cache, the other only counts bytes for the progress bar.
  const [toCache, toCount] = res.body.tee();
  const saving = (await cache()).put(`/offline-media/${meta.id}`,
    new Response(toCache, { headers: { "Content-Type": type, ...(total ? { "Content-Length": String(total) } : {}) } }));
  const reader = toCount.getReader();
  let received = 0, lastPct = -1;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    received += value.length;
    const pct = total ? Math.floor((received / total) * 100) : 0;
    if (pct !== lastPct) { lastPct = pct; onProgress?.(pct); }
  }
  await saving;
  try {
    const thumb = await fetch(meta.thumb, { credentials: "same-origin" });
    if (thumb.ok) await (await cache()).put(`/offline-thumb/${meta.id}`, thumb);
  } catch { /* thumbnails are optional */ }
  const index = await readIndex();
  index[meta.id] = { ...meta, size: received, savedAt: new Date().toISOString() };
  await writeIndex(index);
}

async function persist() {
  // Ask the browser not to evict downloads when space runs low.
  try { await navigator.storage?.persist?.(); } catch { /* not supported */ }
}

export async function saveLesson(meta, dotnet) {
  await persist();
  try {
    await download(meta, (pct) => dotnet?.invokeMethodAsync("OnOfflineProgress", meta.id, pct, false, null));
    dotnet?.invokeMethodAsync("OnOfflineProgress", meta.id, 100, true, null);
  } catch (e) {
    await (await cache()).delete(`/offline-media/${meta.id}`);
    dotnet?.invokeMethodAsync("OnOfflineProgress", meta.id, 0, false, quotaMessage(e));
  }
}

export async function saveCourse(items, dotnet) {
  await persist();
  const index = await readIndex();
  const todo = items.filter((m) => !index[m.id]);
  let i = 0;
  for (const meta of todo) {
    i++;
    try {
      await download(meta, (pct) => dotnet?.invokeMethodAsync("OnCourseOfflineProgress", `${i}/${todo.length} · ${pct}%`));
      dotnet?.invokeMethodAsync("OnOfflineProgress", meta.id, 100, true, null);
    } catch (e) {
      dotnet?.invokeMethodAsync("OnOfflineProgress", meta.id, 0, false, quotaMessage(e));
      break;
    }
  }
  dotnet?.invokeMethodAsync("OnCourseOfflineProgress", "");
}

function quotaMessage(e) {
  return e?.name === "QuotaExceededError" ? "Not enough storage space on this device." : String(e?.message ?? e);
}

export async function removeLesson(id) {
  const c = await cache();
  await c.delete(`/offline-media/${id}`);
  await c.delete(`/offline-thumb/${id}`);
  const index = await readIndex();
  delete index[id];
  await writeIndex(index);
}

export async function updatePosition(id, position) {
  const index = await readIndex();
  if (index[id]) { index[id].position = position; await writeIndex(index); }
}

// ---------- progress made offline, synced when back online ----------

export function queueProgress(id, position, duration, completed) {
  let q = [];
  try { q = JSON.parse(localStorage.getItem(QUEUE) || "[]"); } catch { /* corrupt */ }
  q = q.filter((x) => x.id !== id);
  q.push({ id, position, duration, completed: !!completed, at: Date.now() });
  try { localStorage.setItem(QUEUE, JSON.stringify(q)); } catch { /* full */ }
}

export async function flushProgress() {
  let q = [];
  try { q = JSON.parse(localStorage.getItem(QUEUE) || "[]"); } catch { return; }
  if (!q.length || !navigator.onLine) return;
  const left = [];
  for (const p of q) {
    try {
      const r = await fetch(`/api/v1/lessons/${p.id}/progress`, {
        method: "PUT", credentials: "same-origin", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ position: p.position, duration: p.duration, completed: p.completed || null }),
      });
      if (!r.ok && r.status !== 404) left.push(p);
    } catch { left.push(p); }
  }
  try { localStorage.setItem(QUEUE, JSON.stringify(left)); } catch { /* ignore */ }
}
