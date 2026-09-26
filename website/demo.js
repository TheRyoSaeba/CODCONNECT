const W = 1180;
const H = 720;
const NS = "http://www.w3.org/2000/svg";
const CODE = "K7M4-P2";
const WIFI = { ssid: "CODCONNECT-7F2A", pass: "k7mq4xp2" };
const FRIENDS = [
  { name: "Kyle", console: "PS5" },
  { name: "Jackson", console: "PS4" },
  { name: "Ana", console: "PS5" },
  { name: "Lee", console: "PS4" },
  { name: "Mo", console: "PS5" },
];
const HINTS = {
  ethernet: "Plug your console into a spare Ethernet port on this PC.",
  wifi: "Your console joins a Wi-Fi network this PC creates.",
};
const HELP = {
  wifi: [
    ["Start a room in Wi-Fi mode", "Choose Wi-Fi, then create a room or join your friend’s. This PC starts a network for your console."],
    ["Connect your console", "On the console, join the network shown on the room screen and enter its password."],
  ],
  ethernet: [
    ["Plug in your console", "Use a spare Ethernet port on this PC. Keep this PC’s Internet on Wi-Fi or another port."],
    ["Meet in a room", "Create a room and share the code, or join your friend’s."],
  ],
  last: ["Open the game’s LAN menu", "When the center ring shows LAN ready, you and your friend are on one network."],
  tip: "Set the console’s network to Automatic. It gets Internet through its own PC, so sign-in and updates work in a room.",
};
const RAIL = {
  room: ["17 17", "M 6,1 L 11,1 11,6 6,6 Z M 1,11 L 6,11 6,16 1,16 Z M 11,11 L 16,11 16,16 11,16 Z M 8.5,6 L 8.5,8.5 M 3.5,11 L 3.5,8.5 13.5,8.5 13.5,11"],
  adapters: ["17 17", "M 2,1 L 15,1 15,11 11,11 11,15 6,15 6,11 2,11 Z M 5,4 L 5,7 M 8.5,4 L 8.5,7 M 12,4 L 12,7"],
  diagnostics: ["19 17", "M 0,9 L 4,9 7,2 11,15 14,7 16,9 19,9"],
  chat: ["18 18", "M 2,2 L 16,2 16,12 8,12 3,16 3,12 2,12 Z M 5,6 L 13,6 M 5,9 L 10,9"],
  dev: ["18 15", "M 5,2 L 1,7.5 5,13 M 13,2 L 17,7.5 13,13 M 10,1 L 8,14"],
};

const railIcon = (key) => `<svg viewBox="0 0 ${RAIL[key][0]}" width="${RAIL[key][0].split(" ")[0]}" height="${RAIL[key][0].split(" ")[1]}"><path d="${RAIL[key][1]}"/></svg>`;
const logo = '<svg class="d-logo" viewBox="0 0 28 30" width="26" height="30"><path d="M 2,24 L 10,5 L 18,5 L 10,24 Z" fill="#73D9E5"/><path d="M 11,5 L 19,5 L 27,24 L 19,24 Z" fill="#497B84"/></svg>';

function markup() {
  return `
  <div class="d-bar"><span>CODConnect</span><span class="d-win"><i></i><i></i></span></div>
  <div class="d-body">
    <nav class="d-rail">
      ${logo}
      <div class="d-navs">
        <span class="d-nav" data-nav="Room">${railIcon("room")}</span>
        <span class="d-nav" data-nav="Connections">${railIcon("adapters")}</span>
        <span class="d-nav" data-nav="Diagnostics">${railIcon("diagnostics")}</span>
        <span class="d-nav" data-nav="Chat">${railIcon("chat")}<b class="d-unread" data-unread></b></span>
        <span class="d-nav" data-nav="Developer">${railIcon("dev")}</span>
      </div>
      <span class="d-nav d-help" data-nav="Guide">?</span>
    </nav>
    <section class="d-main">
      <header class="d-head"><h4 data-title></h4><span class="d-badge" data-badge></span></header>
      <div class="d-page" data-page="Room"><svg class="d-diagram" viewBox="0 0 680 530" preserveAspectRatio="xMidYMid meet"></svg></div>
      <div class="d-page d-detail" data-page="Chat">
        <div class="d-thread" data-thread></div>
        <div class="d-compose"><div class="d-box" data-chatinput><span data-text></span><i class="d-caret"></i></div><span class="d-primary" data-send>Send</span></div>
      </div>
      <div class="d-page d-detail" data-page="Guide">
        <div class="d-row-buttons"><span class="d-ghost is-on">Setup</span><span class="d-ghost">Troubleshooting</span></div>
        <h5>Get ready to play</h5>
        <div class="d-row-buttons d-modes"><span class="d-ghost" data-helpmode="ethernet">Ethernet</span><span class="d-ghost" data-helpmode="wifi">Wi-Fi</span></div>
        <div data-steps></div>
        <p class="d-tip"></p>
      </div>
      <div class="d-banner" data-banner><small>Room chat</small><b data-banner-author></b><p data-banner-text></p></div>
    </section>
    <aside class="d-side">
      <div class="d-panel" data-panel="home">
        <h3>Console connection</h3>
        <div class="d-choices"><span class="d-choice" data-mode="ethernet">Ethernet</span><span class="d-choice" data-mode="wifi">Wi-Fi</span></div>
        <p class="d-muted d-hint" data-hint></p>
        <hr>
        <div class="d-row-buttons d-tabs"><span class="d-ghost is-on">Create room</span><span class="d-ghost">Join room</span></div>
        <label>Your name</label>
        <div class="d-box" data-name><span data-text></span><i class="d-caret"></i></div>
        <span class="d-primary d-submit" data-submit><svg class="d-spin" viewBox="0 0 14 14" width="14" height="14"><path d="M 7,1 A 6,6 0 1 1 1,7"/></svg><b>Create room</b></span>
        <span class="d-ghost d-options">Connection options</span>
        <p class="d-muted d-progress" data-progress></p>
      </div>
      <div class="d-panel" data-panel="session">
        <h3 data-session-title></h3>
        <label class="d-code-label">Room code</label>
        <div class="d-code" data-code></div>
        <span class="d-ghost d-copy" data-copy>Copy room code</span>
        <hr>
        <div data-wifi>
          <label>On your console, join</label>
          <div class="d-row"><span>Network</span><em>${WIFI.ssid}</em></div>
          <div class="d-row"><span>Password</span><em>${WIFI.pass}</em></div>
          <hr>
        </div>
        <div class="d-row"><span>Connection</span><b data-row="connection"></b></div>
        <div class="d-row"><span>Your console</span><b data-row="console"></b></div>
        <div data-friend-rows></div>
        <div class="d-row"><span>Console Internet</span><b data-row="internet"></b></div>
        <hr>
        <p class="d-muted d-lanhint" data-lanhint></p>
        <span class="d-ghost d-leave">Leave room</span>
      </div>
    </aside>
  </div>
  <div class="d-cursor" data-cursor><svg viewBox="0 0 24 24"><path d="M5 3l13 8.5-6 1.2-3.3 5.8z"/></svg></div>`;
}

const svg = (tag, attrs = {}, parent) => {
  const el = document.createElementNS(NS, tag);
  for (const [key, value] of Object.entries(attrs)) el.setAttribute(key, value);
  if (parent) parent.appendChild(el);
  return el;
};
const text = (parent, x, y, value, cls, size = 13) => {
  const el = svg("text", { x, y: y + size * 0.95, class: cls, "font-size": size, "text-anchor": "middle" }, parent);
  el.textContent = value;
  return el;
};

function node(parent, x, y, glyph) {
  const g = svg("g", { class: "d-n", transform: `translate(${x} ${y})` }, parent);
  svg("rect", { x: -24, y: -24, width: 48, height: 48, rx: 7, class: "d-n-box" }, g);
  if (glyph === "console") {
    svg("rect", { x: -7, y: -12, width: 14, height: 24, rx: 2, class: "d-n-ink" }, g);
    svg("path", { d: "M -3,-7 L 3,-7", class: "d-n-ink" }, g);
    svg("circle", { cx: 0, cy: 6, r: 1.5, class: "d-n-dot" }, g);
  } else if (glyph === "pc") {
    svg("rect", { x: -11, y: -9, width: 22, height: 15, rx: 2, class: "d-n-ink" }, g);
    svg("path", { d: "M 0,6 L 0,11 M -7,11 L 7,11", class: "d-n-ink" }, g);
  } else {
    svg("circle", { r: 10, class: "d-n-ink" }, g);
    svg("ellipse", { rx: 4.5, ry: 10, class: "d-n-ink" }, g);
    svg("path", { d: "M -10,0 L 10,0", class: "d-n-ink" }, g);
  }
  const tick = svg("g", { class: "d-tick", transform: "translate(18 -18)" }, g);
  svg("circle", { r: 6 }, tick);
  svg("path", { d: "M -3,0 L -1,2 3,-2" }, tick);
  return g;
}

const setNode = (g, attached, ready, lost = false) => {
  g.classList.toggle("att", attached && !ready && !lost);
  g.classList.toggle("rdy", ready && !lost);
  g.classList.toggle("lost", lost);
};

function relayMarker(parent) {
  const g = svg("g", { class: "d-relay" }, parent);
  svg("circle", { r: 15 }, g);
  svg("path", { d: "M -7,4.5 L 6.5,4.5 A 3.6,3.6 0 0 0 7,-2.5 A 5.2,5.2 0 0 0 -2.6,-3.8 A 4.2,4.2 0 0 0 -7,4.5 Z" }, g);
  text(g, 0, 17, "Relay", "d-t-relay", 10);
  return g;
}

function hubCore(parent) {
  const glow = svg("g", { class: "d-glow d-coreglow" }, parent);
  svg("circle", { cx: 340, cy: 280, r: 99, fill: "url(#d-core)", class: "d-corefill" }, glow);
  svg("circle", { cx: 340, cy: 280, r: 71 }, glow);
  svg("circle", { cx: 340, cy: 280, r: 136 }, glow);
  svg("circle", { cx: 340, cy: 280, r: 71, class: "d-core" }, parent);
  svg("path", { d: "M 318,269 L 329,247 L 340,247 L 329,269 Z", class: "d-logo-a" }, parent);
  svg("path", { d: "M 333,247 L 344,247 L 355,269 L 344,269 Z", class: "d-logo-b" }, parent);
  text(parent, 340, 286, "CODCONNECT", "d-t-text", 12);
  return text(parent, 340, 308, "Virtual LAN", "d-t-hubsub", 11);
}

function buildDuo(root) {
  const g = svg("g", { class: "d-duo" }, root);
  svg("circle", { cx: 340, cy: 280, r: 180, class: "d-s d-s1" }, g);
  svg("circle", { cx: 340, cy: 280, r: 136, class: "d-s d-s9" }, g);
  svg("circle", { cx: 340, cy: 280, r: 136, class: "d-s d-s2" }, g);
  svg("circle", { cx: 340, cy: 280, r: 121, class: "d-s d-s1b" }, g);
  svg("path", { d: "M 120,129 L 120,256", class: "d-dash" }, g);
  svg("path", { d: "M 560,129 L 560,256", class: "d-dash" }, g);
  const linkL = svg("path", { d: "M 144,280 L 204,280", class: "d-link" }, g);
  const linkR = svg("path", { d: "M 476,280 L 536,280", class: "d-link" }, g);
  const tunnel = svg("path", { d: "M 214,334 A 136,136 0 0 0 466,334", class: "d-tunnel" }, g);
  const plugL = svg("rect", { x: 199, y: 261, width: 10, height: 38, rx: 4, class: "d-plug" }, g);
  const plugR = svg("rect", { x: 471, y: 261, width: 10, height: 38, rx: 4, class: "d-plug" }, g);
  const glowL = svg("g", { class: "d-glow d-sideglow" }, g);
  const glowR = svg("g", { class: "d-glow d-sideglow" }, g);
  for (const [group, left] of [[glowL, true], [glowR, false]]) {
    for (const radius of [180, 136, 121]) {
      svg("path", { d: `M 340,${280 - radius} A ${radius},${radius} 0 0 ${left ? 0 : 1} 340,${280 + radius}`, "stroke-width": radius === 136 ? 2.4 : 1.1 }, group);
    }
    svg("path", { d: left ? "M 120,129 L 120,256 M 144,280 L 204,280" : "M 560,129 L 560,256 M 476,280 L 536,280", "stroke-width": 2 }, group);
  }
  const hubsub = hubCore(g);
  const relay = relayMarker(g);
  relay.setAttribute("transform", "translate(340 416)");
  const inet = svg("g", { class: "d-inet" }, g);
  const inetLine = svg("path", { d: "M 120,352 L 120,410", class: "d-inet-line" }, inet);
  const inetGlow = svg("path", { d: "M 120,352 L 120,410", class: "d-glow d-inet-glow", "stroke-width": 2 }, inet);
  const inetNode = node(inet, 120, 434, "globe");
  text(inet, 120, 464, "Internet", "d-t-text");
  const inetDetail = text(inet, 120, 482, "", "d-t-detail", 11);
  const lc = node(g, 120, 105, "console");
  const rc = node(g, 560, 105, "console");
  const lp = node(g, 120, 280, "pc");
  const rp = node(g, 560, 280, "pc");
  text(g, 120, 57, "Your console", "d-t-text");
  const rcLabel = text(g, 560, 57, "Friend’s console", "d-t-text");
  text(g, 120, 325, "This PC", "d-t-text");
  const rpLabel = text(g, 560, 325, "Friend’s PC", "d-t-text");
  return { g, linkL, linkR, tunnel, plugL, plugR, glowL, glowR, hubsub, relay, inet, inetLine, inetGlow, inetNode, inetDetail, lc, rc, lp, rp, rcLabel, rpLabel };
}

function buildRing(root) {
  const g = svg("g", { class: "d-ring" }, root);
  svg("circle", { cx: 340, cy: 280, r: 150, class: "d-s d-s1" }, g);
  svg("circle", { cx: 340, cy: 280, r: 118, class: "d-s d-s7" }, g);
  svg("circle", { cx: 340, cy: 280, r: 118, class: "d-s d-s2" }, g);
  const spokes = svg("g", { transform: "translate(340 280)" }, g);
  const hubsub = hubCore(g);
  const players = svg("g", { transform: "translate(340 280)" }, g);
  return { g, spokes, players, hubsub, slots: new Map() };
}

function ringSlot(ring, key) {
  if (ring.slots.has(key)) return ring.slots.get(key);
  const spoke = svg("g", { class: "d-spoke" }, ring.spokes);
  const base = svg("line", { x1: 122, y1: 0, x2: 166, y2: 0, class: "d-sp-base", pathLength: 1 }, spoke);
  svg("line", { x1: 122, y1: 0, x2: 166, y2: 0, class: "d-sp-lost" }, spoke);
  svg("path", { d: "M 122,0 L 144,30 L 166,0", class: "d-sp-bent" }, spoke);
  const glow = svg("g", { class: "d-glow d-sp-glow" }, spoke);
  svg("line", { x1: 122, y1: 0, x2: 166, y2: 0, class: "d-sp-gline", "stroke-width": 2 }, glow);
  svg("path", { d: "M 122,0 L 144,30 L 166,0", class: "d-sp-gbent", "stroke-width": 2 }, glow);
  const relay = relayMarker(spoke);
  const holder = svg("g", { class: "d-holder" }, ring.players);
  const inner = svg("g", { class: "d-pop" }, holder);
  const box = node(inner, 0, 0, "console");
  const badge = svg("g", { class: "d-ibadge", transform: "translate(-18 -18)" }, inner);
  svg("circle", { r: 7, class: "d-ib-bg" }, badge);
  svg("circle", { r: 3.8, class: "d-ib-ink" }, badge);
  svg("ellipse", { rx: 1.6, ry: 3.8, class: "d-ib-ink" }, badge);
  svg("path", { d: "M -3.8,0 L 3.8,0", class: "d-ib-ink" }, badge);
  const name = text(inner, 0, 30, "", "d-t-text");
  const detail = text(inner, 0, 48, "", "d-t-detail", 11);
  const slot = { spoke, base, relay, holder, inner, box, badge, name, detail, angle: null };
  ring.slots.set(key, slot);
  spoke.classList.add("enter");
  inner.classList.add("enter");
  setTimeout(() => { spoke.classList.remove("enter"); inner.classList.remove("enter"); }, 900);
  return slot;
}

const shortName = (name, max = 12) => name.length <= max ? name : name.slice(0, max - 1) + "…";

class Cancelled extends Error {}

export function createDemo(host, { reduceMotion = false, onChapter = () => {}, onProgress = () => {} } = {}) {
  const app = document.createElement("div");
  app.className = "app";
  app.setAttribute("aria-hidden", "true");
  app.innerHTML = markup();
  host.appendChild(app);

  const $ = (selector) => app.querySelector(selector);
  const $$ = (selector) => [...app.querySelectorAll(selector)];
  const setText = (el, value) => { if (el.textContent !== value) el.textContent = value; };
  const cursor = $("[data-cursor]");

  new ResizeObserver(() => app.style.setProperty("--s", String(host.clientWidth / W))).observe(host);
  app.style.setProperty("--s", String(host.clientWidth / W || 1));

  const diagram = $(".d-diagram");
  const defs = svg("defs", {}, diagram);
  const grad = svg("radialGradient", { id: "d-core" }, defs);
  svg("stop", { offset: "0", "stop-color": "rgb(63,163,236)", "stop-opacity": ".2" }, grad);
  svg("stop", { offset: "1", "stop-color": "rgb(63,163,236)", "stop-opacity": "0" }, grad);
  const blur = svg("filter", { id: "d-blur", x: "-50%", y: "-50%", width: "200%", height: "200%" }, defs);
  svg("feGaussianBlur", { stdDeviation: "3.2", result: "b" }, blur);
  const merge = svg("feMerge", {}, blur);
  svg("feMergeNode", { in: "b" }, merge);
  svg("feMergeNode", { in: "SourceGraphic" }, merge);
  const duo = buildDuo(diagram);
  const ring = buildRing(diagram);

  let S;
  const fresh = () => ({
    page: "Room", screen: "home", mode: "ethernet", name: "Player", busy: false, progress: "", code: "", shownCode: "",
    copied: false, localReady: false, remoteReady: false, internet: null, kind: null, tunnel: false, players: [], ever: new Set(),
    event: null, messages: [], typing: [], unread: 0, banner: null, helpMode: null, selectName: false, typingIn: null, draft: "",
  });

  const lostSet = () => new Set(S.players.filter((p) => !p.connected && S.ever.has(p.name)).map((p) => p.name));
  const screen = () => S.screen === "home" ? "home" : S.tunnel ? "connected" : "hosting";

  function badge() {
    if (S.screen === "home") return { text: "Not connected", tone: "" };
    const lost = S.players.find((p) => lostSet().has(p.name));
    if (lost) return { text: `Reconnecting ${shortName(lost.name, 14)}…`, tone: "warn" };
    const tone = screen() === "connected" && (S.players.length === 0 || S.players.some((p) => p.connected)) ? "live" : "";
    if (S.event) return { text: S.event, tone };
    const joined = S.players.filter((p) => p.connected).map((p) => shortName(p.name, 14));
    if (joined.length === 1) return { text: `${joined[0]} joined`, tone };
    if (joined.length === 2) return { text: `${joined[0]} and ${joined[1]} joined`, tone };
    if (joined.length > 2) return { text: `${joined[0]} and ${joined.length - 1} others joined`, tone };
    if (S.players.length === 1) return { text: `Connecting to ${shortName(S.players[0].name, 14)}`, tone };
    if (S.players.length > 1) return { text: `Connecting to ${S.players.length} friends`, tone };
    return { text: screen() === "hosting" ? "Waiting for friends" : "Friend joined", tone };
  }

  const detailOf = (p, lost) => lost ? "Reconnecting" : !p.connected ? "Connecting" : (p.console ?? "No console yet") + (p.relay ? ", via relay" : "");
  const internetText = (state) => ({ Ready: "Through this PC", Starting: "Setting up…", Waiting: "Waiting for the console", Unavailable: "Unavailable" })[state] ?? "Off";

  function renderSide() {
    const home = S.screen === "home";
    $$("[data-panel]").forEach((panel) => panel.classList.toggle("is-on", panel.dataset.panel === (home ? "home" : "session")));
    $$("[data-mode]").forEach((el) => el.classList.toggle("is-on", el.dataset.mode === S.mode));
    setText($("[data-hint]"), HINTS[S.mode]);
    setText($("[data-name] [data-text]"), S.name);
    $("[data-name]").classList.toggle("selected", S.selectName);
    $("[data-submit]").classList.toggle("busy", S.busy);
    setText($("[data-progress]"), S.busy ? S.progress : "");
    const current = screen();
    setText($("[data-session-title]"), current === "connected" ? "Room connected" : "Your room is open");
    setText($("[data-copy]"), S.copied ? "Copied" : "Copy room code");
    $("[data-wifi]").hidden = S.mode !== "wifi";
    const relayed = S.players.filter((p) => p.connected && p.relay).length;
    const kind = S.kind ?? (current === "connected" ? "Direct" : "Pending");
    setText($('[data-row="connection"]'), kind === "Direct" && relayed > 0 ? `Direct, ${relayed} via relay` : kind);
    setText($('[data-row="console"]'), S.localReady ? "PS5" : "Not identified");
    setText($('[data-row="internet"]'), internetText(S.internet));
    const lost = lostSet();
    const rows = S.players.length === 0 ? [["Friend’s console", S.tunnel && S.remoteReady ? "PS5" : "Not identified"]]
      : S.players.length === 1 ? [[S.players[0].name, detailOf(S.players[0], lost.has(S.players[0].name))]]
      : [["Friends", `${S.players.filter((p) => p.ready && p.connected).length} of ${S.players.length} consoles ready`]];
    const holder = $("[data-friend-rows]");
    const html = rows.map(([label, value]) => `<div class="d-row"><span>${label}</span><b>${value}</b></div>`).join("");
    if (holder.innerHTML !== html) holder.innerHTML = html;
    setText($("[data-lanhint]"), current === "connected" ? "Open your game’s LAN menu." : "Share this code with your friend.");
  }

  function renderChrome() {
    $$("[data-nav]").forEach((nav) => nav.classList.toggle("is-on", nav.dataset.nav === S.page));
    $$("[data-page]").forEach((page) => page.classList.toggle("is-on", page.dataset.page === S.page));
    setText($("[data-title]"), { Room: "Your network", Chat: "Room chat", Guide: "Help" }[S.page]);
    const b = badge();
    setText($("[data-badge]"), b.text);
    $("[data-badge]").dataset.tone = b.tone;
    const unread = $("[data-unread]");
    unread.classList.toggle("is-on", S.unread > 0);
    if (S.unread > 0) setText(unread, S.unread > 9 ? "9+" : String(S.unread));
    const banner = $("[data-banner]");
    banner.classList.toggle("is-on", !!S.banner);
    if (S.banner) {
      setText($("[data-banner-author]"), S.banner.author);
      setText($("[data-banner-text]"), S.banner.text);
    }
  }

  function renderDiagram() {
    const lost = lostSet();
    const friends = S.players.map((p) => ({ ...p, lost: lost.has(p.name) }));
    const inSession = S.screen !== "home";
    const lanReady = friends.length > 1
      ? S.localReady && S.tunnel && friends.some((f) => f.connected) && friends.filter((f) => f.connected).every((f) => f.ready)
      : S.localReady && S.remoteReady && S.tunnel;
    const ringMode = friends.length > 1;
    diagram.classList.toggle("ring-mode", ringMode);
    diagram.classList.toggle("lan", lanReady);

    const one = friends.length === 1 ? shortName(friends[0].name) : null;
    const remoteReady = S.remoteReady && S.tunnel;
    setNode(duo.lc, S.localReady, S.localReady);
    setNode(duo.lp, inSession || S.localReady, S.localReady);
    setNode(duo.rc, remoteReady, remoteReady);
    setNode(duo.rp, S.tunnel || remoteReady, remoteReady);
    duo.linkL.classList.toggle("on", inSession);
    duo.linkR.classList.toggle("on", S.tunnel);
    duo.plugL.classList.toggle("on", inSession);
    duo.plugR.classList.toggle("on", S.tunnel);
    duo.tunnel.classList.toggle("on", S.tunnel);
    duo.g.classList.toggle("lan", lanReady);
    duo.glowL.classList.toggle("is-on", S.localReady);
    duo.glowR.classList.toggle("is-on", remoteReady);
    duo.relay.classList.toggle("is-on", S.kind === "Relay" || (friends.length === 1 && friends[0].connected && friends[0].relay));
    duo.relay.classList.toggle("live", S.tunnel);
    setText(duo.rcLabel, one ? `${one}’s console` : "Friend’s console");
    setText(duo.rpLabel, one ? `${one}’s PC` : "Friend’s PC");
    setText(duo.hubsub, lanReady ? "LAN ready" : "Virtual LAN");
    setText(ring.hubsub, lanReady ? "LAN ready" : "Virtual LAN");
    duo.inet.classList.toggle("is-on", !!S.internet);
    duo.inet.dataset.state = S.internet ?? "";
    const inetReady = S.internet === "Ready";
    setNode(duo.inetNode, inetReady || S.internet === "Starting", inetReady);
    setText(duo.inetDetail, S.internet ? internetText(S.internet) : "");

    const everyone = [{ name: "You", connected: true, ready: S.localReady, detail: inSession ? (S.localReady ? "PS5" : "No console yet") : null, relay: false, lost: false, self: true },
      ...friends.map((f) => ({ ...f, detail: detailOf(f, f.lost), relay: f.relay && f.connected && !f.lost }))];
    const keys = new Set(everyone.map((p) => p.name));
    for (const [key, slot] of ring.slots) {
      if (!keys.has(key) || !ringMode) { slot.spoke.remove(); slot.holder.remove(); ring.slots.delete(key); }
    }
    if (!ringMode) return;
    everyone.forEach((p, i) => {
      const slot = ringSlot(ring, p.name);
      const angle = 180 + (i * 360) / everyone.length;
      slot.angle = angle;
      slot.spoke.style.transform = `rotate(${angle}deg)`;
      slot.relay.style.transform = `translate(144px, 30px) rotate(${-angle}deg)`;
      slot.holder.style.transform = `rotate(${angle}deg) translate(196px, 0px) rotate(${-angle}deg)`;
      const y = 196 * Math.sin((angle * Math.PI) / 180);
      const above = y < -40;
      const top = above ? (p.detail ? -66 : -50) : 30;
      slot.name.setAttribute("y", String(top + 12.35));
      slot.detail.setAttribute("y", String(top + 18 + 10.45));
      setText(slot.name, shortName(p.name));
      setText(slot.detail, p.detail ?? "");
      slot.detail.classList.toggle("warn", p.lost);
      slot.detail.classList.toggle("good", p.ready && !p.lost);
      setNode(slot.box, p.connected || p.ready, p.ready, p.lost);
      slot.spoke.classList.toggle("att", p.connected || p.ready);
      slot.spoke.classList.toggle("rdy", p.ready && !p.lost);
      slot.spoke.classList.toggle("relay", p.relay);
      slot.spoke.classList.toggle("lost", p.lost);
      slot.relay.classList.toggle("is-on", p.relay);
      slot.relay.classList.toggle("live", p.ready);
      slot.badge.classList.toggle("is-on", !!p.self && !!S.internet);
      slot.badge.classList.toggle("rdy", !!p.self && inetReady);
    });
  }

  function renderChat() {
    const thread = $("[data-thread]");
    const signature = JSON.stringify([S.messages, S.typing]);
    if (thread.dataset.sig !== signature) {
      thread.dataset.sig = signature;
      thread.textContent = "";
      for (let i = 0; i < S.messages.length;) {
        const first = S.messages[i];
        const turn = [first];
        while (i + turn.length < S.messages.length && S.messages[i + turn.length].own === first.own && S.messages[i + turn.length].author === first.author) turn.push(S.messages[i + turn.length]);
        const block = document.createElement("div");
        block.className = `d-turn${first.own ? " own" : ""}${first.fresh ? " fresh" : ""}`;
        if (!first.own) block.insertAdjacentHTML("beforeend", `<small></small>`);
        if (!first.own) block.querySelector("small").textContent = first.author;
        for (const message of turn) {
          const bubble = document.createElement("p");
          bubble.textContent = message.text;
          block.appendChild(bubble);
        }
        const last = turn[turn.length - 1];
        block.insertAdjacentHTML("beforeend", `<time>${last.time}${last.sending ? "  Sending…" : ""}</time>`);
        thread.appendChild(block);
        i += turn.length;
      }
      for (const name of S.typing) {
        const block = document.createElement("div");
        block.className = "d-turn typing fresh";
        block.innerHTML = `<small></small><p><i></i><i></i><i></i></p>`;
        block.querySelector("small").textContent = name;
        thread.appendChild(block);
      }
    }
    setText($("[data-chatinput] [data-text]"), S.draft);
    app.classList.toggle("typing-chat", S.typingIn === "chat");
    app.classList.toggle("typing-name", S.typingIn === "name");
  }

  function renderHelp() {
    const mode = S.helpMode ?? (S.mode === "wifi" ? "wifi" : "ethernet");
    $$("[data-helpmode]").forEach((el) => el.classList.toggle("is-on", el.dataset.helpmode === mode));
    const steps = [...HELP[mode], HELP.last];
    const html = steps.map(([title, body], i) => `<div class="d-step"><span>${i + 1}</span><div><b>${title}</b><p>${body}</p></div></div>`).join("");
    const holder = $("[data-steps]");
    if (holder.dataset.mode !== mode) { holder.dataset.mode = mode; holder.innerHTML = html; }
    setText($(".d-tip"), HELP.tip);
  }

  function render() {
    renderSide();
    renderChrome();
    renderDiagram();
    renderChat();
    renderHelp();
    const code = $("[data-code]");
    if (!code.dataset.scrambling) setText(code, S.shownCode);
  }

  const set = (patch) => { Object.assign(S, patch); render(); };
  const player = (name, patch) => {
    const index = S.players.findIndex((p) => p.name === name);
    const next = index < 0 ? { name, connected: false, ready: false, console: null, relay: false, ...patch } : { ...S.players[index], ...patch };
    const players = [...S.players];
    if (index < 0) players.push(next); else players[index] = next;
    if (next.connected) S.ever.add(name);
    set({ players });
  };

  const baseThread = [
    { author: "Kyle", text: "you on yet?", time: "21:04" },
    { author: "Kyle", text: "lobby is up on my side", time: "21:04" },
  ];

  const chapters = [
    {
      length: 11000,
      finish() {
        S.mode = "wifi"; S.name = "Alex"; S.screen = "session"; S.code = CODE; S.shownCode = CODE; S.internet = "Waiting";
      },
      async play(run) {
        await run.wait(500);
        await run.click($('[data-mode="wifi"]'));
        set({ mode: "wifi" });
        await run.wait(400);
        await run.click($("[data-name]"));
        set({ selectName: true, typingIn: "name" });
        await run.wait(350);
        set({ selectName: false, name: "" });
        await run.type("Alex", (value) => set({ name: value }));
        set({ typingIn: null });
        await run.wait(250);
        await run.click($("[data-submit]"));
        set({ busy: true, progress: "Getting ready…" });
        for (const message of ["Opening your room…", "Getting a room code…"]) {
          await run.wait(900);
          set({ progress: message });
        }
        await run.wait(900);
        set({ busy: false, screen: "session", code: CODE, internet: "Waiting" });
        await run.scramble($("[data-code]"), CODE);
        S.shownCode = CODE;
        await run.wait(500);
        await run.click($("[data-copy]"));
        set({ copied: true });
        await run.wait(2000);
        set({ copied: false });
        await run.park();
      },
    },
    {
      length: 5200,
      finish() { S.localReady = true; S.internet = "Ready"; },
      async play(run) {
        await run.wait(900);
        set({ localReady: true });
        await run.wait(1800);
        set({ internet: "Ready" });
        await run.wait(2300);
      },
    },
    {
      length: 11500,
      finish() {
        S.tunnel = true; S.remoteReady = true;
        S.players = FRIENDS.map((f) => ({ name: f.name, connected: true, ready: true, console: f.console, relay: false }));
        FRIENDS.forEach((f) => S.ever.add(f.name));
      },
      async play(run) {
        await run.wait(400);
        const joined = async (friend, first) => {
          player(friend.name, { connected: false });
          await run.wait(700);
          if (first) S.tunnel = true;
          S.event = `${friend.name} joined`;
          player(friend.name, { connected: true });
          await run.wait(900);
          if (first) S.remoteReady = true;
          player(friend.name, { ready: true, console: friend.console });
          await run.wait(700);
          set({ event: null });
        };
        await joined(FRIENDS[0], true);
        await run.wait(900);
        for (const friend of FRIENDS.slice(1)) {
          await joined(friend, false);
          await run.wait(150);
        }
        await run.wait(2200);
      },
    },
    {
      length: 12500,
      finish() {
        S.page = "Chat"; S.unread = 0; S.banner = null; S.typing = [];
        S.messages = [...baseThread, { author: "Jackson", text: "do you see the LAN game?", time: "21:05" }, { author: "Alex", own: true, text: "yeah, joining now", time: "21:05" }];
      },
      async play(run) {
        S.messages = [baseThread[0]];
        await run.wait(500);
        S.messages = [...baseThread];
        set({ unread: 1, banner: { author: "Kyle", text: "lobby is up on my side" } });
        await run.wait(2200);
        await run.click($('[data-nav="Chat"]'));
        set({ page: "Chat", unread: 0, banner: null });
        await run.wait(800);
        set({ typing: ["Jackson"] });
        await run.wait(1600);
        set({ typing: [], messages: [...S.messages, { author: "Jackson", text: "do you see the LAN game?", time: "21:05", fresh: true }] });
        await run.wait(700);
        await run.click($("[data-chatinput]"));
        set({ typingIn: "chat" });
        await run.type("yeah, joining now", (value) => set({ draft: value }));
        await run.wait(200);
        await run.click($("[data-send]"));
        set({ typingIn: null, draft: "", messages: [...S.messages, { author: "Alex", own: true, text: "yeah, joining now", time: "21:05", sending: true, fresh: true }] });
        await run.wait(700);
        set({ messages: S.messages.map((m) => ({ ...m, sending: false })) });
        await run.wait(1200);
        await run.park();
      },
    },
    {
      length: 9800,
      finish() {
        S.page = "Room";
        S.players = S.players.map((p) => p.name === "Jackson" ? { ...p, relay: true } : p);
      },
      async play(run) {
        await run.wait(300);
        await run.click($('[data-nav="Room"]'));
        set({ page: "Room" });
        await run.wait(1200);
        player("Jackson", { connected: false, ready: false });
        await run.wait(3000);
        player("Jackson", { connected: true, relay: true });
        await run.wait(900);
        player("Jackson", { ready: true });
        await run.wait(2600);
        await run.park();
      },
    },
    {
      length: 9000,
      finish() { S.page = "Guide"; S.helpMode = "ethernet"; },
      async play(run) {
        await run.wait(300);
        await run.click($('[data-nav="Guide"]'));
        set({ page: "Guide", helpMode: null });
        await run.wait(900);
        for (const step of $$(".d-step")) await run.move(step.querySelector("b"), { x: 30, y: 2 });
        await run.wait(400);
        await run.click($('[data-helpmode="ethernet"]'));
        set({ helpMode: "ethernet" });
        await run.wait(900);
        for (const step of $$(".d-step")) await run.move(step.querySelector("b"), { x: 30, y: 2 });
        await run.wait(700);
        await run.park();
      },
    },
  ];

  let run = null;
  let paused = true;
  let resumeWaiters = [];
  const place = (x, y) => { cursor.style.transform = `translate(${x.toFixed(1)}px, ${y.toFixed(1)}px)`; };
  const centerOf = (el) => {
    const box = el.getBoundingClientRect(), frame = app.getBoundingClientRect();
    const scale = frame.width / W || 1;
    return { x: (box.left - frame.left + box.width / 2) / scale, y: (box.top - frame.top + box.height / 2) / scale };
  };
  let cursorAt = { x: W * 0.55, y: H * 0.95 };
  place(cursorAt.x, cursorAt.y);

  function newRun(chapterIndex) {
    const self = {
      cancelled: false,
      elapsed: 0,
      check() { if (self.cancelled) throw new Cancelled(); },
      async wait(ms) {
        let left = ms;
        while (left > 0) {
          self.check();
          if (paused) { await new Promise((resolve) => resumeWaiters.push(resolve)); continue; }
          const step = Math.min(left, 80);
          await new Promise((resolve) => setTimeout(resolve, step));
          if (!paused) {
            left -= step;
            self.elapsed += step;
            onProgress(chapterIndex, Math.min(0.98, self.elapsed / chapters[chapterIndex].length));
          }
        }
        self.check();
      },
      async move(el, offset = { x: 0, y: 0 }) {
        const target = centerOf(el);
        target.x += offset.x;
        target.y += offset.y + 4;
        const distance = Math.hypot(target.x - cursorAt.x, target.y - cursorAt.y);
        const duration = Math.round(Math.min(950, 340 + distance * 0.9));
        cursor.style.transitionDuration = `${duration}ms, 300ms`;
        place(target.x, target.y);
        cursorAt = target;
        cursor.classList.add("shown");
        await self.wait(duration + 60);
      },
      async click(el) {
        await self.move(el);
        el.classList.add("d-press");
        cursor.classList.add("press");
        const ripple = document.createElement("i");
        ripple.className = "d-ripple";
        ripple.style.left = `${cursorAt.x}px`;
        ripple.style.top = `${cursorAt.y}px`;
        app.appendChild(ripple);
        setTimeout(() => ripple.remove(), 700);
        await self.wait(150);
        cursor.classList.remove("press");
        el.classList.remove("d-press");
        await self.wait(120);
      },
      async type(value, apply) {
        for (let i = 1; i <= value.length; i++) {
          apply(value.slice(0, i));
          await self.wait(55 + Math.random() * 70);
        }
      },
      async scramble(el, value) {
        const pool = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        el.dataset.scrambling = "1";
        try {
          for (let frame = 1; frame <= value.length * 3; frame++) {
            const settled = Math.floor(frame / 3);
            setText(el, [...value].map((ch, i) => i < settled || ch === "-" ? ch : pool[Math.floor(Math.random() * pool.length)]).join(""));
            await self.wait(40);
          }
        } finally {
          delete el.dataset.scrambling;
        }
        setText(el, value);
      },
      async park() {
        cursor.style.transitionDuration = "900ms, 300ms";
        cursorAt = { x: W * 0.55, y: H * 0.97 };
        place(cursorAt.x, cursorAt.y);
        cursor.classList.remove("shown");
        await self.wait(300);
      },
    };
    return self;
  }

  function snapTo(chapterIndex) {
    app.classList.add("snap");
    S = fresh();
    for (let i = 0; i < chapterIndex; i++) chapters[i].finish();
    render();
    void app.offsetWidth;
    app.classList.remove("snap");
  }

  async function playFrom(chapterIndex, jump) {
    if (run) run.cancelled = true;
    const current = newRun(chapterIndex);
    run = current;
    if (jump) {
      app.classList.add("swap");
      await new Promise((resolve) => setTimeout(resolve, 220));
      if (current.cancelled) return;
      snapTo(chapterIndex);
      app.classList.remove("swap");
    }
    let index = chapterIndex;
    let active = current;
    try {
      while (true) {
        onChapter(index);
        onProgress(index, 0);
        if (reduceMotion) {
          chapters[index].finish();
          render();
          return;
        }
        await chapters[index].play(active);
        onProgress(index, 1);
        await active.wait(500);
        index = (index + 1) % chapters.length;
        if (index === 0) {
          app.classList.add("swap");
          await active.wait(350);
          snapTo(0);
          app.classList.remove("swap");
          await active.wait(300);
        }
        const next = newRun(index);
        if (active.cancelled) return;
        run = next;
        active = next;
      }
    } catch (error) {
      if (!(error instanceof Cancelled)) throw error;
    }
  }

  snapTo(0);

  return {
    count: chapters.length,
    start(chapterIndex = 0) { playFrom(chapterIndex, chapterIndex !== 0 || run !== null); },
    jump(chapterIndex) { playFrom(chapterIndex, true); },
    pause() { paused = true; app.classList.add("paused"); },
    resume() {
      paused = false;
      app.classList.remove("paused");
      const waiters = resumeWaiters;
      resumeWaiters = [];
      waiters.forEach((resolve) => resolve());
    },
    started: () => run !== null,
  };
}
