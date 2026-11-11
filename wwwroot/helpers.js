// Lazy-loads third-party scripts from cdnjs only on the pages that need them.
const loaded = {};
function loadScript(src) {
  return (loaded[src] ??= new Promise((resolve, reject) => {
    const s = document.createElement("script");
    s.src = src; s.onload = resolve; s.onerror = reject;
    document.head.appendChild(s);
  }));
}
function loadCss(id, href) {
  if (document.getElementById(id)) return;
  const l = document.createElement("link");
  l.id = id; l.rel = "stylesheet"; l.href = href;
  document.head.appendChild(l);
}

export async function renderQr(id, text) {
  await loadScript("https://cdnjs.cloudflare.com/ajax/libs/qrcodejs/1.0.0/qrcode.min.js");
  const el = document.getElementById(id);
  if (!el) return;
  el.innerHTML = "";
  new window.QRCode(el, { text, width: 148, height: 148, colorDark: "#0f1115", colorLight: "#ffffff" });
}

export async function highlight(id) {
  loadCss("hljs-css", "https://cdnjs.cloudflare.com/ajax/libs/highlight.js/11.9.0/styles/github-dark.min.css");
  await loadScript("https://cdnjs.cloudflare.com/ajax/libs/highlight.js/11.9.0/highlight.min.js");
  const el = document.getElementById(id);
  if (!el || !window.hljs) return;
  delete el.dataset.highlighted;
  window.hljs.highlightElement(el);
}
