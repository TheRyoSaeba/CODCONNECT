import { createScene } from "./scene.js";

const reduceMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
const root = document.documentElement;

const players = ["You", "Kyle", "Jackson", "Ana", "Lee", "Mo"];
const homes = ["One home.", "Two homes.", "Three homes.", "Four homes.", "Five homes.", "Six homes.", "One LAN."];

const steps = [
  { title: "Install", text: "Install CODCONNECT on a PC near your console." },
  { title: "Make a room", text: "Create a room and send friends the code." },
  { title: "Connect console", text: { wifi: "Join the Wi-Fi network your PC creates.", ethernet: "Plug the console into your PC." } },
  { title: "Open LAN mode", text: "Your friends’ lobbies show up like they’re in the other room." },
];

class Wheel {
  constructor(element, words) {
    this.element = element;
    this.element.textContent = "";
    this.words = words.map((word) => {
      const span = document.createElement("span");
      span.className = "wheel-word";
      span.textContent = word;
      span.setAttribute("aria-hidden", "true");
      element.appendChild(span);
      return span;
    });
    this.label = document.createElement("span");
    this.label.className = "visually-hidden";
    element.appendChild(this.label);
    this.index = -1;
  }

  set(index) {
    if (index === this.index) return;
    this.index = index;
    this.words.forEach((span, i) => {
      const offset = i - index;
      span.dataset.offset = offset < -1 ? "far-up" : offset > 1 ? "far-down" : String(offset);
    });
    this.label.textContent = this.words[index].textContent;
  }
}

function setupTheme(scene) {
  const button = document.getElementById("theme");
  const apply = (night) => {
    root.dataset.theme = night ? "night" : "day";
    button.setAttribute("aria-pressed", String(night));
    document.querySelector('meta[name="theme-color"]').content = night ? "#141417" : "#D6D6D8";
    scene?.setTheme(night);
    drawTopo();
  };
  apply(root.dataset.theme === "night");
  button.addEventListener("click", () => {
    const night = root.dataset.theme !== "night";
    try { localStorage.setItem("codconnect-theme", night ? "night" : "day"); } catch {}
    apply(night);
  });
}

const topo = document.getElementById("topo");

function drawTopo() {
  const ctx = topo.getContext("2d");
  const ratio = Math.min(window.devicePixelRatio || 1, 2);
  const w = window.innerWidth, h = window.innerHeight;
  topo.width = w * ratio;
  topo.height = h * ratio;
  ctx.setTransform(ratio, 0, 0, ratio, 0, 0);
  ctx.clearRect(0, 0, w, h);
  ctx.strokeStyle = getComputedStyle(document.body).getPropertyValue("--topo").trim() || "rgba(0,0,0,.07)";
  ctx.lineWidth = 1;

  const step = 12;
  const cols = Math.ceil(w / step) + 1, rows = Math.ceil(h / step) + 1;
  const seed = 7.31;
  const hash = (x, y) => { const s = Math.sin(x * 127.1 + y * 311.7 + seed) * 43758.5453; return s - Math.floor(s); };
  const smooth = (t) => t * t * (3 - 2 * t);
  const noise = (x, y) => {
    const xi = Math.floor(x), yi = Math.floor(y), xf = x - xi, yf = y - yi;
    const a = hash(xi, yi), b = hash(xi + 1, yi), c = hash(xi, yi + 1), d = hash(xi + 1, yi + 1);
    const u = smooth(xf), v = smooth(yf);
    return a + (b - a) * u + (c - a) * v + (a - b - c + d) * u * v;
  };
  const field = new Float32Array(cols * rows);
  for (let y = 0; y < rows; y++) {
    for (let x = 0; x < cols; x++) {
      const px = x * step / 260, py = y * step / 260;
      field[y * cols + x] = noise(px, py) * 0.6 + noise(px * 2.1, py * 2.1) * 0.28 + noise(px * 4.3, py * 4.3) * 0.12;
    }
  }

  ctx.beginPath();
  for (let level = 0.12; level < 0.9; level += 0.055) {
    for (let y = 0; y < rows - 1; y++) {
      for (let x = 0; x < cols - 1; x++) {
        const a = field[y * cols + x], b = field[y * cols + x + 1], c = field[(y + 1) * cols + x + 1], d = field[(y + 1) * cols + x];
        const code = (a > level ? 8 : 0) | (b > level ? 4 : 0) | (c > level ? 2 : 0) | (d > level ? 1 : 0);
        if (code === 0 || code === 15) continue;
        const X = x * step, Y = y * step;
        const lerp = (p, q) => (level - p) / (q - p);
        const top = [X + step * lerp(a, b), Y];
        const right = [X + step, Y + step * lerp(b, c)];
        const bottom = [X + step * lerp(d, c), Y + step];
        const left = [X, Y + step * lerp(a, d)];
        const segments = {
          1: [[left, bottom]], 2: [[bottom, right]], 3: [[left, right]], 4: [[top, right]],
          5: [[left, top], [bottom, right]], 6: [[top, bottom]], 7: [[left, top]], 8: [[left, top]],
          9: [[top, bottom]], 10: [[left, bottom], [top, right]], 11: [[top, right]], 12: [[left, right]],
          13: [[bottom, right]], 14: [[left, bottom]],
        }[code];
        for (const [p, q] of segments) { ctx.moveTo(p[0], p[1]); ctx.lineTo(q[0], q[1]); }
      }
    }
  }
  ctx.stroke();
}

function buildTags() {
  const layer = document.getElementById("tags");
  return players.map((player) => {
    const tag = document.createElement("div");
    tag.className = "tag";
    tag.textContent = player;
    layer.appendChild(tag);
    return tag;
  });
}

function runLobby(scene, tags) {
  const wheel = new Wheel(document.getElementById("lobbyWheel"), homes);
  const count = document.getElementById("playerCount");
  const state = document.getElementById("roomState");
  const join = (i) => {
    scene?.join(i);
    tags[i].classList.add("joined");
    count.textContent = String(i + 1);
    wheel.set(i);
  };
  const finish = () => {
    wheel.set(homes.length - 1);
    wheel.element.classList.add("ready");
    state.textContent = "LAN ready";
    state.classList.add("ready");
    scene?.ready();
  };

  if (reduceMotion) {
    players.forEach((_, i) => join(i));
    finish();
    return;
  }

  join(0);
  let i = 1;
  setTimeout(function next() {
    join(i);
    i += 1;
    if (i < players.length) setTimeout(next, 900);
    else setTimeout(finish, 1100);
  }, 1300);
}

function setupSteps(scene) {
  const wheel = new Wheel(document.getElementById("setupWheel"), steps.map((step) => step.title));
  const section = document.getElementById("setup");
  const text = document.getElementById("stepText");
  const number = document.getElementById("stepNumber");
  let mode = "wifi";
  let current = -1;

  const describe = (i) => typeof steps[i].text === "string" ? steps[i].text : steps[i].text[mode];
  const show = (i, force) => {
    if (i === current && !force) return;
    current = i;
    wheel.set(i);
    section.dataset.step = String(i);
    number.textContent = `Step ${i + 1} of ${steps.length}`;
    text.textContent = describe(i);
    scene?.setStep(i);
  };

  for (const chip of document.querySelectorAll(".chip")) {
    chip.addEventListener("click", () => {
      mode = chip.dataset.mode;
      document.querySelectorAll(".chip").forEach((c) => c.setAttribute("aria-pressed", String(c === chip)));
      scene?.setMode(mode);
      show(current, true);
    });
  }

  show(0);
  return show;
}

function setupChapters(scene, tags, showStep) {
  const sections = [...document.querySelectorAll(".chapter")];
  const links = [...document.querySelectorAll("[data-nav]")];
  const index = document.getElementById("chapterIndex");
  const setup = document.getElementById("setup");
  let active = -1;

  const bar = document.getElementById("progress");
  const fill = bar.querySelector("i");
  const marker = bar.querySelector("b");

  const update = () => {
    const vh = window.innerHeight;
    const scrolled = Math.min(1, window.scrollY / Math.max(1, document.documentElement.scrollHeight - vh));
    fill.style.transform = `scaleY(${scrolled.toFixed(4)})`;
    marker.style.top = `${(scrolled * 100).toFixed(2)}%`;
    bar.classList.toggle("done", scrolled > 0.995);
    const probe = window.scrollY + vh * 0.5;
    let pos = 0;
    let chapter = 0;
    sections.forEach((section, i) => {
      const top = section.offsetTop;
      const height = section.offsetHeight;
      if (probe >= top) {
        chapter = i;
        const leaving = (probe - (top + height - vh * 0.4)) / (vh * 0.4);
        pos = i + Math.min(1, Math.max(0, leaving));
      }
    });
    pos = Math.min(pos, sections.length - 1);
    scene?.setView(pos);
    document.body.dataset.zone = chapter === 2 ? "dark" : "";
    scene?.setZone(chapter === 2);

    const range = setup.offsetHeight - vh;
    const progress = Math.min(0.999, Math.max(0, (window.scrollY - setup.offsetTop) / Math.max(1, range)));
    showStep(Math.floor(progress * steps.length));

    if (chapter !== active) {
      active = chapter;
      index.textContent = String(chapter + 1).padStart(2, "0");
      links.forEach((link) => link.classList.toggle("active", Number(link.dataset.nav) === chapter));
      drawTopoSoon();
    }
  };

  window.addEventListener("scroll", update, { passive: true });
  window.addEventListener("resize", update);
  document.getElementById("theme").addEventListener("click", update);
  update();
  scene?.jumpView(0);

  scene?.onFrame((view, position) => {
    const lobbyWeight = 1 - Math.min(1, position * 3);
    tags.forEach((tag, i) => {
      const point = scene.project(i);
      tag.style.transform = `translate(${point.x.toFixed(1)}px, ${point.y.toFixed(1)}px) translate(-50%, -100%)`;
      tag.classList.toggle("shown", point.visible && lobbyWeight > 0.5);
    });
  });
}

let topoTimer = 0;
function drawTopoSoon() {
  clearTimeout(topoTimer);
  topoTimer = setTimeout(drawTopo, 420);
}

function animateFaq() {
  for (const details of document.querySelectorAll(".faq-list details")) {
    const summary = details.querySelector("summary");
    const answer = document.createElement("div");
    answer.className = "answer";
    while (summary.nextSibling) answer.appendChild(summary.nextSibling);
    details.appendChild(answer);
    if (reduceMotion) continue;
    summary.addEventListener("click", (event) => {
      event.preventDefault();
      if (details.dataset.moving) return;
      details.dataset.moving = "1";
      const opening = !details.open;
      if (opening) details.open = true;
      const height = answer.scrollHeight;
      const motion = answer.animate(
        opening ? [{ height: "0px", opacity: 0 }, { height: `${height}px`, opacity: 1 }]
                : [{ height: `${height}px`, opacity: 1 }, { height: "0px", opacity: 0 }],
        { duration: 300, easing: "cubic-bezier(.3, .7, .2, 1)" });
      motion.onfinish = () => { if (!opening) details.open = false; delete details.dataset.moving; };
    });
  }
}

const canvas = document.getElementById("stage3d");
let scene = null;
try { scene = createScene(canvas, { reduceMotion }); } catch { scene = null; }
if (!scene) canvas.remove();

const tags = buildTags();
setupTheme(scene);
runLobby(scene, tags);
const showStep = setupSteps(scene);
setupChapters(scene, tags, showStep);
animateFaq();
window.addEventListener("resize", drawTopoSoon);
document.fonts?.ready.then(() => scene?.setTheme(root.dataset.theme === "night"));
