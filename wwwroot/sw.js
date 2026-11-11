// Minimal service worker: makes CourseHub installable as an app. Everything still comes from the
// server (videos and progress live there), so requests simply go to the network.
self.addEventListener("install", () => self.skipWaiting());
self.addEventListener("activate", (e) => e.waitUntil(self.clients.claim()));
self.addEventListener("fetch", () => { /* network only */ });
