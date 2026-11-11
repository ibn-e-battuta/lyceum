// Chromecast / Google TV support via Google's Cast Web Sender SDK (Chrome and Edge only).
// The TV fetches the video itself, using short-lived signed URLs prepared by the server.
let dotnet = null;
let context = null;
let player = null, controller = null;
let current = null;   // { lessonId, lastReport, lastTime }

function sdkReady() {
  return typeof cast !== "undefined" && cast.framework && typeof chrome !== "undefined" && chrome.cast;
}

export function init(dotnetRef) {
  dotnet = dotnetRef;
  // Only Chromium browsers can cast; don't load Google's script anywhere else.
  const chromium = !!window.chrome && /Chrome|Edg\//.test(navigator.userAgent) && !/Firefox/.test(navigator.userAgent);
  if (!chromium) return;
  if (sdkReady()) { setup(); return; }
  window.__onGCastApiAvailable = (ok) => { if (ok) setup(); };
  if (!document.getElementById("cast-sdk")) {
    const s = document.createElement("script");
    s.id = "cast-sdk";
    s.src = "https://www.gstatic.com/cv/js/sender/v1/cast_sender.js?loadCastFramework=1";
    document.head.appendChild(s);
  }
}

function setup() {
  context = cast.framework.CastContext.getInstance();
  context.setOptions({
    receiverApplicationId: chrome.cast.media.DEFAULT_MEDIA_RECEIVER_APP_ID,
    autoJoinPolicy: chrome.cast.AutoJoinPolicy.ORIGIN_SCOPED,
  });
  const report = () => dotnet?.invokeMethodAsync("OnCastAvailable", context.getCastState() !== cast.framework.CastState.NO_DEVICES_AVAILABLE).catch(() => {});
  context.addEventListener(cast.framework.CastContextEventType.CAST_STATE_CHANGED, report);
  report();

  player = new cast.framework.RemotePlayer();
  controller = new cast.framework.RemotePlayerController(player);
  controller.addEventListener(cast.framework.RemotePlayerEventType.IS_CONNECTED_CHANGED, () => {
    if (!player.isConnected) { current = null; dotnet?.invokeMethodAsync("OnCastState", false, null).catch(() => {}); }
  });
  // While casting, keep saving progress like the local player does.
  controller.addEventListener(cast.framework.RemotePlayerEventType.CURRENT_TIME_CHANGED, () => {
    if (!current || !player.duration) return;
    const now = Date.now();
    if (now - current.lastReport < 5000) return;
    const watched = player.isPaused ? 0 : Math.min((now - current.lastReport) / 1000, 10);
    current.lastReport = now;
    dotnet?.invokeMethodAsync("ReportPosition", current.lessonId, player.currentTime, player.duration, watched).catch(() => {});
  });
  controller.addEventListener(cast.framework.RemotePlayerEventType.PLAYER_STATE_CHANGED, () => {
    if (current && player.playerState === chrome.cast.media.PlayerState.IDLE && player.duration && player.currentTime >= player.duration - 2) {
      dotnet?.invokeMethodAsync("OnEnded", current.lessonId).catch(() => {});
    }
  });
}

export async function castLesson(info, dotnetRef) {
  dotnet = dotnetRef ?? dotnet;
  if (!context) return;
  try {
    if (!context.getCurrentSession()) await context.requestSession();
    const session = context.getCurrentSession();
    if (!session) return;
    const media = new chrome.cast.media.MediaInfo(info.url, info.contentType);
    media.metadata = new chrome.cast.media.GenericMediaMetadata();
    media.metadata.title = info.title;
    media.metadata.subtitle = info.subtitle;
    if (info.image) media.metadata.images = [new chrome.cast.Image(info.image)];
    media.tracks = (info.tracks || []).map((t) => {
      const track = new chrome.cast.media.Track(t.id, chrome.cast.media.TrackType.TEXT);
      track.trackContentId = t.url;
      track.trackContentType = "text/vtt";
      track.subtype = chrome.cast.media.TextTrackType.SUBTITLES;
      track.name = t.name;
      track.language = t.language;
      return track;
    });
    const request = new chrome.cast.media.LoadRequest(media);
    request.currentTime = info.startTime || 0;
    request.autoplay = true;
    await session.loadMedia(request);
    current = { lessonId: info.lessonId, lastReport: Date.now() };
    dotnet?.invokeMethodAsync("OnCastState", true, null).catch(() => {});
  } catch (e) {
    // The user closing the device picker isn't an error worth showing.
    const msg = e === "cancel" || e?.code === "cancel" ? null : `Casting failed: ${e?.description || e?.code || e}`;
    dotnet?.invokeMethodAsync("OnCastState", false, msg).catch(() => {});
  }
}

export function stop() {
  context?.getCurrentSession()?.endSession(true);
  current = null;
  dotnet?.invokeMethodAsync("OnCastState", false, null).catch(() => {});
}

export function dispose() { current = null; }
