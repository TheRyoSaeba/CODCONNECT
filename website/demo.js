const W = 960;
const H = 590;
const HUB = { x: 308, y: 236 };
const PLAYERS = [
  { name: "You", console: "PS5", at: [-205, 0] },
  { name: "Kyle", console: "PS5", at: [-95, -160] },
  { name: "Jackson", console: "PS4", at: [95, -160] },
  { name: "Ana", console: "PS5", at: [205, 0] },
  { name: "Lee", console: "PS4", at: [-95, 160] },
  { name: "Mo", console: "PS5", at: [95, 160] },
];
const RELAY = [190, -118];
const CODE = "K7M4-P2";
const STEPS = {
  wifi: [
    ["Start a room in Wi-Fi mode", "This PC starts a network for your console."],
    ["Connect your console", "Join the network shown on the room screen."],
    ["Open the game’s LAN menu", "When the ring shows LAN ready, you’re on one network."],
  ],
  ethernet: [
    ["Plug in your console", "Use a spare Ethernet port on this PC."],
    ["Start or join a room", "Share the code with your friends."],
    ["Open the game’s LAN menu", "When the ring shows LAN ready, you’re on one network."],
  ],
};
const HINTS = {
  wifi: "This PC starts a Wi-Fi network for your console.",
  ethernet: "Plug your console into a spare Ethernet port on this PC.",
};

const icon = {
  network: '<svg viewBox="0 0 24 24"><rect x="9" y="3" width="6" height="5" rx="1"/><rect x="3" y="16" width="6" height="5" rx="1"/><rect x="15" y="16" width="6" height="5" rx="1"/><path d="M12 8v4M6 16v-4h12v4"/></svg>',
  port: '<svg viewBox="0 0 24 24"><rect x="4" y="5" width="16" height="12" rx="1.5"/><path d="M9 17v2h6v-2M8 9v2M11 9v2M14 9v2M17 9v2"/></svg>',
  pulse: '<svg viewBox="0 0 24 24"><path d="M3 12h4l2.5-6 5 12 2.5-6h4"/></svg>',
  chat: '<svg viewBox="0 0 24 24"><path d="M4 5h16v11H9l-5 4z"/><path d="M8 9h8M8 12h5"/></svg>',
  code: '<svg viewBox="0 0 24 24"><path d="M9 7l-5 5 5 5M15 7l5 5-5 5M13 5l-2 14"/></svg>',
  console: '<svg viewBox="0 0 24 24"><rect x="8" y="3" width="8" height="18" rx="2"/><circle cx="12" cy="16" r="1"/></svg>',
  pc: '<svg viewBox="0 0 24 24"><rect x="3" y="4" width="18" height="12" rx="1.5"/><path d="M9 20h6M12 16v4"/></svg>',
  globe: '<svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="9"/><path d="M3 12h18M12 3c3 3 3 15 0 18M12 3c-3 3-3 15 0 18"/></svg>',
  cloud: '<svg viewBox="0 0 24 24"><path d="M7 18h10a4 4 0 0 0 .6-7.95A6 6 0 0 0 6.2 9.2 4.4 4.4 0 0 0 7 18z"/></svg>',
  check: '<svg viewBox="0 0 24 24"><path d="M5 12.5l4.5 4.5L19 7.5"/></svg>',
  wifi: '<svg viewBox="0 0 24 24"><path d="M2.5 9a14 14 0 0 1 19 0M5.5 12.5a9.5 9.5 0 0 1 13 0M8.8 16a5 5 0 0 1 6.4 0"/><circle cx="12" cy="19.2" r="1.2"/></svg>',
  mark: '<svg viewBox="0 0 32 32"><path d="M16 4 4 28h7l5-11 5 11h7z" fill="#73D9E5"/><path d="M16 4 4 28h7l5-11z" fill="#3FA9C0"/></svg>',
};

function markup() {
  const nodes = PLAYERS.map((p, i) => {
    const x = HUB.x + p.at[0], y = HUB.y + p.at[1];
    const top = p.at[1] < 0;
    return `<div class="d-node${top ? " top" : ""}" data-node="${i}" style="left:${x}px;top:${y}px">
      <div class="d-box">${icon.console}<i class="d-tick">${icon.check}</i></div>
      <div class="d-label"><b>${p.name}</b><small data-sub></small></div>
    </div>`;
  }).join("");
  const relayX = HUB.x + RELAY[0], relayY = HUB.y + RELAY[1];
  return `
  <div class="d-bar"><span>CODConnect</span><span class="d-win"><i></i><i></i></span></div>
  <div class="d-body">
    <nav class="d-rail">
      <div class="d-logo">${icon.mark}</div>
      <div class="d-railicons">
        <span class="d-ri" data-rail="network">${icon.network}</span>
        <span class="d-ri">${icon.port}</span>
        <span class="d-ri">${icon.pulse}</span>
        <span class="d-ri" data-rail="chat">${icon.chat}<em class="d-badge">1</em></span>
        <span class="d-ri">${icon.code}</span>
      </div>
      <span class="d-ri d-help" data-rail="help">?</span>
    </nav>
    <section class="d-main">
      <header class="d-head"><h4 data-title>Your network</h4><span class="d-pill" data-pill>Not connected</span></header>
      <div class="d-view is-on" data-view="network">
        <div class="d-net">
          <svg class="d-lines" viewBox="0 0 616 492" width="616" height="492"></svg>
          <div class="d-hub">
            <div class="d-ring"></div>
            <div class="d-core">${icon.mark}<b>CODCONNECT</b><small data-hubsub>Virtual LAN</small></div>
          </div>
          ${nodes}
          <div class="d-relay" style="left:${relayX}px;top:${relayY}px"><div class="d-rbox">${icon.cloud}</div><small>Relay</small></div>
          <div class="d-dots"></div>
          <div class="d-uplink">
            <div class="d-station" data-station="0"><div class="d-box">${icon.console}</div><b>Your console</b></div>
            <div class="d-station" data-station="1"><div class="d-box">${icon.pc}</div><b>This PC</b></div>
            <div class="d-station" data-station="2"><div class="d-box">${icon.globe}</div><b>Internet</b></div>
            <i class="d-link" data-link="0"></i><i class="d-link" data-link="1"></i>
            <span class="d-waves">${icon.wifi}</span>
            <p class="d-upstatus" data-upstatus>Joining CODCONNECT-7F2A…</p>
          </div>
        </div>
      </div>
      <div class="d-view" data-view="chat">
        <div class="d-thread" data-thread></div>
        <div class="d-compose"><div class="d-input" data-chatinput><span data-text></span><i class="d-caret"></i></div><span class="d-send" data-send>Send</span></div>
      </div>
      <div class="d-view" data-view="help">
        <div class="d-tabs"><span class="is-on">Setup</span><span>Troubleshooting</span></div>
        <h5>Get ready to play</h5>
        <div class="d-seg d-seg-sm" data-helpseg><span data-helpmode="ethernet">Ethernet</span><span data-helpmode="wifi" class="is-on">Wi-Fi</span></div>
        <ol class="d-steps" data-steps></ol>
      </div>
    </section>
    <aside class="d-side">
      <div class="d-panel is-on" data-panel="setup">
        <h4>Console connection</h4>
        <div class="d-seg" data-modeseg><span data-mode="ethernet" class="is-on">Ethernet</span><span data-mode="wifi">Wi-Fi</span></div>
        <p class="d-hint" data-hint></p>
        <hr>
        <div class="d-seg d-seg-ghost"><span class="is-on">Create room</span><span>Join room</span></div>
        <label>Your name</label>
        <div class="d-input" data-name><span data-text></span><i class="d-caret"></i></div>
        <span class="d-primary" data-create><i class="d-spin"></i><b>Create room</b></span>
        <span class="d-outline">Connection options</span>
        <p class="d-progress" data-progress></p>
      </div>
      <div class="d-panel" data-panel="room">
        <h4>Room connected</h4>
        <label>Room code</label>
        <div class="d-code" data-code></div>
        <span class="d-outline" data-copy>Copy room code</span>
        <hr>
        <label>On your console, join</label>
        <div class="d-row"><span>Network</span><em>CODCONNECT-7F2A</em></div>
        <div class="d-row"><span>Password</span><em>k7mq4xp2</em></div>
        <hr>
        <div class="d-row"><span>Connection</span><b data-row="connection">Direct</b></div>
        <div class="d-row"><span>Your console</span><b data-row="console">Waiting…</b></div>
        <div class="d-row"><span>Friends</span><b data-row="friends">None yet</b></div>
        <div class="d-row"><span>Console Internet</span><b data-row="internet">—</b></div>
        <hr>
        <p class="d-hint" data-lanhint>Open your game’s LAN menu.</p>
      </div>
    </aside>
  </div>
  <div class="d-toast" data-toast><div class="d-thead">${icon.mark}<span>CODCONNECT</span></div><b>Kyle</b><p>lobby is up on my side</p></div>
  <div class="d-cursor" data-cursor><svg viewBox="0 0 24 24"><path d="M5 3l13 8.5-6 1.2-3.3 5.8z"/></svg></div>`;
}

class Cancelled extends Error {}

export function createDemo(host, { reduceMotion = false, onChapter = () => {}, onProgress = () => {} } = {}) {
  const app = document.createElement("div");
  app.className = "app";
  app.setAttribute("aria-hidden", "true");
  app.innerHTML = markup();
  host.appendChild(app);

  const $ = (selector) => app.querySelector(selector);
  const $$ = (selector) => [...app.querySelectorAll(selector)];
  const lines = $(".d-lines");
  const dots = $(".d-dots");
  const cursor = $("[data-cursor]");
  const net = $(".d-net");

  const fit = () => {
    const scale = host.clientWidth / W;
    app.style.setProperty("--s", String(scale));
  };
  new ResizeObserver(fit).observe(host);
  fit();

  const svg = (tag, attrs) => {
    const el = document.createElementNS("http://www.w3.org/2000/svg", tag);
    for (const [key, value] of Object.entries(attrs)) el.setAttribute(key, value);
    return el;
  };
  const pointOf = (at) => ({ x: HUB.x + at[0], y: HUB.y + at[1] });
  const segment = (from, to, startGap, endGap) => {
    const dx = to.x - from.x, dy = to.y - from.y, len = Math.hypot(dx, dy);
    const ux = dx / len, uy = dy / len;
    return { x1: from.x + ux * startGap, y1: from.y + uy * startGap, x2: to.x - ux * endGap, y2: to.y - uy * endGap };
  };
  const link = (key, s) => {
    const line = svg("line", { ...s, pathLength: "1", class: "d-line", "data-line": key });
    lines.appendChild(line);
    const packets = [0, 1].map((n) => {
      const dot = document.createElement("i");
      dot.className = "d-dot";
      dot.dataset.dot = key;
      dot.style.offsetPath = `path("M ${s.x1.toFixed(1)} ${s.y1.toFixed(1)} L ${s.x2.toFixed(1)} ${s.y2.toFixed(1)}")`;
      dot.style.animationDelay = `${-n * 0.9 - Math.random() * 0.4}s`;
      if (n) dot.classList.add("back");
      dots.appendChild(dot);
      return dot;
    });
    return { line, packets };
  };
  lines.appendChild(svg("circle", { cx: HUB.x, cy: HUB.y, r: 128, class: "d-orbit" }));
  const links = PLAYERS.map((p, i) => link(`p${i}`, segment(HUB, pointOf(p.at), 100, 30)));
  const relayPoint = pointOf(RELAY);
  const relayIn = link("r0", segment(HUB, relayPoint, 100, 22));
  const relayOut = link("r1", segment(relayPoint, pointOf(PLAYERS[2].at), 22, 30));

  const nodes = $$("[data-node]");
  const setText = (el, text) => { if (el.textContent !== text) el.textContent = text; };
  const on = (el, flag = true) => el.classList.toggle("is-on", flag);

  const state = {
    view(name) {
      $$("[data-view]").forEach((view) => on(view, view.dataset.view === name));
      $$("[data-rail]").forEach((rail) => on(rail, rail.dataset.rail === name));
      setText($("[data-title]"), { network: "Your network", chat: "Room chat", help: "Help" }[name]);
    },
    pill(text, live = false) { setText($("[data-pill]"), text); $("[data-pill]").classList.toggle("live", live); },
    mode(mode) {
      $$("[data-mode]").forEach((el) => on(el, el.dataset.mode === mode));
      setText($("[data-hint]"), HINTS[mode]);
    },
    name(text) { setText($("[data-name] [data-text]"), text); },
    panel(name) { $$("[data-panel]").forEach((panel) => on(panel, panel.dataset.panel === name)); },
    code(text) { setText($("[data-code]"), text); },
    row(key, text, good = false) { const el = $(`[data-row="${key}"]`); setText(el, text); el.classList.toggle("good", good); },
    player(i, status, sub) {
      const node = nodes[i];
      node.classList.toggle("joined", status !== "off");
      node.classList.toggle("ready", status === "ready");
      setText(node.querySelector("[data-sub]"), sub);
      const direct = !(i === 2 && app.classList.contains("relayed"));
      links[i].line.classList.toggle("drawn", status !== "off" && direct);
      links[i].line.classList.toggle("live", status === "ready");
      links[i].packets.forEach((dot) => on(dot, status === "ready" && direct));
    },
    ready(flag) { app.classList.toggle("lan-ready", flag); setText($("[data-hubsub]"), flag ? "LAN ready" : "Virtual LAN"); },
    relay(stage) {
      app.classList.toggle("relaying", stage === "failing");
      app.classList.toggle("relayed", stage === "relayed");
      links[2].line.classList.toggle("drawn", stage !== "relayed" && nodes[2].classList.contains("joined"));
      links[2].line.classList.toggle("warn", stage === "failing");
      relayIn.line.classList.toggle("drawn", stage === "relayed");
      relayOut.line.classList.toggle("drawn", stage === "relayed");
      relayIn.line.classList.toggle("live", stage === "relayed");
      relayOut.line.classList.toggle("live", stage === "relayed");
      if (stage !== "none") links[2].packets.forEach((dot) => on(dot, false));
      [...relayIn.packets, ...relayOut.packets].forEach((dot) => on(dot, stage === "relayed"));
    },
    uplink(stage) {
      const up = $(".d-uplink");
      up.dataset.stage = String(stage);
      on(up, stage > 0);
      app.classList.toggle("uplinking", stage > 0);
      setText($("[data-upstatus]"), ["", "Joining CODCONNECT-7F2A…", "Connected to this PC", "Online through this PC"][stage] || "");
    },
    thread(messages) {
      const thread = $("[data-thread]");
      thread.textContent = "";
      for (const message of messages) thread.appendChild(bubble(message));
    },
    helpMode(mode) {
      $$("[data-helpmode]").forEach((el) => on(el, el.dataset.helpmode === mode));
      const list = $("[data-steps]");
      list.innerHTML = STEPS[mode].map(([title, text], i) => `<li data-step="${i}"><b>${title}</b><span>${text}</span></li>`).join("");
    },
    badge(flag) { $(".d-badge").classList.toggle("is-on", flag); },
    toast(flag) { on($("[data-toast]"), flag); },
  };

  function bubble({ who, text, mine, time }) {
    const el = document.createElement("div");
    el.className = `d-msg${mine ? " mine" : ""}`;
    el.innerHTML = `${who ? `<small>${who}</small>` : ""}<p></p>${time ? `<time>${time}</time>` : ""}`;
    el.querySelector("p").textContent = text;
    return el;
  }

  const BASE_THREAD = [
    { who: "Kyle", text: "you on yet?" },
    { text: "lobby is up on my side", time: "21:04" },
  ];

  function reset() {
    state.view("network");
    state.pill("Not connected");
    state.mode("ethernet");
    state.name("");
    state.panel("setup");
    state.code("");
    setText($("[data-progress]"), "");
    $("[data-create]").classList.remove("busy");
    setText($("[data-copy]"), "Copy room code");
    state.row("connection", "Direct");
    state.row("console", "Waiting…");
    state.row("friends", "None yet");
    state.row("internet", "—");
    state.relay("none");
    PLAYERS.forEach((_, i) => state.player(i, i === 0 ? "joined" : "off", i === 0 ? "No console yet" : ""));
    links[0].line.classList.remove("drawn");
    state.ready(false);
    state.uplink(0);
    state.thread(BASE_THREAD);
    state.helpMode("wifi");
    state.badge(false);
    state.toast(false);
    setText($("[data-chatinput] [data-text]"), "");
    app.classList.remove("typing-name", "typing-chat");
    $$(".d-steps li").forEach((li) => li.classList.remove("is-on"));
    $("[data-lanhint]").classList.remove("glow");
  }

  const chapters = [
    {
      length: 9500,
      finish() {
        state.mode("wifi");
        state.name("Alex");
        state.panel("room");
        state.code(CODE);
        state.pill("Room open", true);
        state.player(0, "joined", "Waiting for the console");
      },
      async play(run) {
        await run.wait(500);
        await run.click($('[data-mode="wifi"]'));
        state.mode("wifi");
        await run.wait(450);
        await run.click($("[data-name]"));
        app.classList.add("typing-name");
        await run.type($("[data-name] [data-text]"), "Alex");
        app.classList.remove("typing-name");
        await run.wait(250);
        await run.click($("[data-create]"));
        $("[data-create]").classList.add("busy");
        for (const message of ["Opening your room…", "Getting a room code…", "Connecting your console…"]) {
          setText($("[data-progress]"), message);
          await run.wait(850);
        }
        $("[data-create]").classList.remove("busy");
        setText($("[data-progress]"), "");
        state.panel("room");
        state.pill("Room open", true);
        state.player(0, "joined", "Waiting for the console");
        await run.scramble($("[data-code]"), CODE);
        await run.wait(400);
        await run.click($("[data-copy]"));
        setText($("[data-copy]"), "Copied");
        await run.wait(1300);
        setText($("[data-copy]"), "Copy room code");
        await run.park();
      },
    },
    {
      length: 7600,
      finish() {
        state.uplink(0);
        state.player(0, "ready", "PS5");
        state.row("console", "PlayStation", true);
        state.row("internet", "Through this PC", true);
      },
      async play(run) {
        await run.wait(400);
        state.uplink(1);
        await run.wait(1500);
        state.uplink(2);
        state.row("console", "PlayStation", true);
        await run.wait(1200);
        state.row("internet", "Checking…");
        await run.wait(700);
        state.uplink(3);
        state.row("internet", "Through this PC", true);
        await run.wait(2300);
        state.uplink(0);
        await run.wait(250);
        state.player(0, "ready", "PS5");
        await run.wait(900);
      },
    },
    {
      length: 8200,
      finish() {
        PLAYERS.forEach((p, i) => { if (i) state.player(i, "ready", p.console); });
        state.row("friends", "5 of 5 consoles ready", true);
        state.pill("Kyle and 4 others joined", true);
        state.ready(true);
        $("[data-lanhint]").classList.add("glow");
      },
      async play(run) {
        await run.wait(300);
        for (let i = 1; i < PLAYERS.length; i++) {
          state.player(i, "joined", "Connecting");
          state.pill(i === 1 ? "Kyle joined" : `Kyle and ${i - 1} other${i > 2 ? "s" : ""} joined`, true);
          state.row("friends", `${i - 1} of ${i} consoles ready`);
          await run.wait(650);
          state.player(i, "ready", PLAYERS[i].console);
          state.row("friends", `${i} of ${i} consoles ready`, true);
          await run.wait(350);
        }
        await run.wait(400);
        state.ready(true);
        await run.wait(700);
        $("[data-lanhint]").classList.add("glow");
        await run.wait(2000);
      },
    },
    {
      length: 10500,
      finish() {
        state.toast(false);
        state.badge(false);
        state.thread([...BASE_THREAD, { who: "Jackson", text: "do you see the LAN game?", time: "21:05" }, { text: "yeah, joining now", mine: true, time: "21:05" }]);
        state.view("chat");
      },
      async play(run) {
        await run.wait(400);
        state.toast(true);
        state.badge(true);
        await run.wait(1500);
        await run.click($('[data-rail="chat"]'));
        state.toast(false);
        state.badge(false);
        state.view("chat");
        await run.wait(700);
        const thread = $("[data-thread]");
        const typing = bubble({ who: "Jackson", text: "" });
        typing.classList.add("typing");
        typing.querySelector("p").innerHTML = "<i></i><i></i><i></i>";
        thread.appendChild(typing);
        await run.wait(1300);
        typing.remove();
        thread.appendChild(bubble({ who: "Jackson", text: "do you see the LAN game?", time: "21:05" }));
        await run.wait(600);
        await run.click($("[data-chatinput]"));
        app.classList.add("typing-chat");
        await run.type($("[data-chatinput] [data-text]"), "yeah, joining now");
        await run.wait(200);
        await run.click($("[data-send]"));
        app.classList.remove("typing-chat");
        setText($("[data-chatinput] [data-text]"), "");
        thread.appendChild(bubble({ text: "yeah, joining now", mine: true, time: "21:05" }));
        await run.wait(1400);
        await run.park();
      },
    },
    {
      length: 8800,
      finish() {
        state.view("network");
        state.relay("relayed");
        state.player(2, "ready", "PS4, via relay");
        state.row("connection", "Direct, 1 via relay");
        state.pill("Kyle and 4 others joined", true);
      },
      async play(run) {
        await run.wait(300);
        await run.click($('[data-rail="network"]'));
        state.view("network");
        await run.wait(900);
        state.relay("failing");
        state.player(2, "joined", "Direct link lost");
        state.pill("Reconnecting Jackson…");
        await run.wait(1600);
        state.relay("relayed");
        state.player(2, "joined", "Trying the relay");
        await run.wait(1100);
        state.player(2, "ready", "PS4, via relay");
        state.row("connection", "Direct, 1 via relay");
        state.pill("Kyle and 4 others joined", true);
        await run.wait(2200);
        await run.park();
      },
    },
    {
      length: 9000,
      finish() {
        state.view("help");
        state.helpMode("ethernet");
      },
      async play(run) {
        await run.wait(300);
        await run.click($('[data-rail="help"]'));
        state.view("help");
        state.helpMode("wifi");
        await run.wait(600);
        await run.walkSteps();
        await run.click($('[data-helpmode="ethernet"]'));
        state.helpMode("ethernet");
        await run.wait(350);
        await run.walkSteps();
        await run.wait(600);
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
    const scale = frame.width / W;
    return { x: (box.left - frame.left + box.width / 2) / scale, y: (box.top - frame.top + box.height / 2) / scale };
  };
  let cursorAt = { x: W * 0.62, y: H * 0.9 };
  place(cursorAt.x, cursorAt.y);

  function newRun(chapterIndex) {
    const self = {
      cancelled: false,
      chapter: chapterIndex,
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
        const duration = Math.round(Math.min(900, 320 + distance * 1.1));
        cursor.style.transitionDuration = `${duration}ms`;
        place(target.x, target.y);
        cursorAt = target;
        cursor.classList.add("shown");
        await self.wait(duration + 60);
      },
      async click(el) {
        await self.move(el);
        el.classList.add("d-hover");
        cursor.classList.add("press");
        const ripple = document.createElement("i");
        ripple.className = "d-ripple";
        ripple.style.left = `${cursorAt.x}px`;
        ripple.style.top = `${cursorAt.y}px`;
        app.appendChild(ripple);
        setTimeout(() => ripple.remove(), 700);
        await self.wait(140);
        cursor.classList.remove("press");
        el.classList.remove("d-hover");
        await self.wait(120);
      },
      async type(el, text) {
        for (let i = 1; i <= text.length; i++) {
          setText(el, text.slice(0, i));
          await self.wait(60 + Math.random() * 70);
        }
      },
      async scramble(el, text) {
        const pool = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        for (let frame = 0; frame <= text.length * 3; frame++) {
          const settled = Math.floor(frame / 3);
          setText(el, [...text].map((ch, i) => i < settled || ch === "-" ? ch : pool[Math.floor(Math.random() * pool.length)]).join(""));
          await self.wait(40);
        }
        setText(el, text);
      },
      async walkSteps() {
        const items = $$(".d-steps li");
        for (const item of items) {
          items.forEach((li) => on(li, li === item));
          await self.wait(850);
        }
        items.forEach((li) => on(li, false));
      },
      async park() {
        cursor.style.transitionDuration = "900ms";
        cursorAt = { x: W * 0.62, y: H * 0.94 };
        place(cursorAt.x, cursorAt.y);
        cursor.classList.remove("shown");
        await self.wait(300);
      },
    };
    return self;
  }

  function snapTo(chapterIndex) {
    app.classList.add("snap");
    reset();
    for (let i = 0; i < chapterIndex; i++) chapters[i].finish();
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

  reset();

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
