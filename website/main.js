import { createScene } from "./scene.js?v=__BUILD__";

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

function setupGlance() {
  const DURATION = 6500;
  const EASE = "cubic-bezier(.75, 0, .2, 1)";
  const list = document.getElementById("glance");
  const buttons = [...list.querySelectorAll("button")];
  const figure = document.getElementById("shots");
  const lens = figure.querySelector(".lens");
  const shots = [...lens.querySelectorAll("img")];
  const scan = lens.querySelector(".scan");
  const reticle = lens.querySelector(".reticle");
  const callout = lens.querySelector(".callout");
  list.style.setProperty("--glance-time", `${DURATION}ms`);

  let current = -1;
  let timer = 0;
  let timeouts = [];
  let running = [];

  const clamp = (value, low, high) => low > high ? (low + high) / 2 : Math.min(high, Math.max(low, value));

  const framing = (img, boost = 1) => {
    const w = lens.clientWidth, h = lens.clientHeight;
    const nw = Number(img.getAttribute("width")), nh = Number(img.getAttribute("height"));
    const k = Math.min(w / nw, h / nh);
    const dw = nw * k, dh = nh * k, dx = (w - dw) / 2, dy = (h - dh) / 2;
    const [x, y, rw, rh] = img.dataset.focus.split(" ").map(Number);
    const fx = dx + x * k, fy = dy + y * k, fw = rw * k, fh = rh * k;
    const s = Math.max(1, Math.min(w * 0.8 / fw, h * 0.56 / fh, 2.4)) * boost;
    const tx = clamp(w * 0.5 - (fx + fw / 2) * s, w - (dx + dw) * s, -dx * s);
    const ty = clamp(h * 0.42 - (fy + fh / 2) * s, h - (dy + dh) * s, -dy * s);
    return {
      transform: `translate(${tx.toFixed(1)}px, ${ty.toFixed(1)}px) scale(${s.toFixed(4)})`,
      box: { left: `${(tx + fx * s - 12).toFixed(1)}px`, top: `${(ty + fy * s - 10).toFixed(1)}px`, width: `${(fw * s + 24).toFixed(1)}px`, height: `${(fh * s + 20).toFixed(1)}px` },
    };
  };

  const frameBox = () => ({ left: "10px", top: "10px", width: `${lens.clientWidth - 20}px`, height: `${lens.clientHeight - 20}px` });
  const shade = (alpha) => `0 0 0 100vmax rgba(9, 9, 12, ${alpha})`;

  const placeCallout = (box) => {
    const left = parseFloat(box.left), top = parseFloat(box.top), height = parseFloat(box.height);
    const w = lens.clientWidth, h = lens.clientHeight;
    const below = top + height + 16;
    const y = below + callout.offsetHeight < h - 8 ? below : Math.max(8, top - callout.offsetHeight - 16);
    callout.style.left = `${clamp(left, 8, w - callout.offsetWidth - 8)}px`;
    callout.style.top = `${y}px`;
  };

  const halt = () => {
    timeouts.forEach(clearTimeout);
    timeouts = [];
    for (const animation of running) {
      try { animation.commitStyles(); } catch {}
      animation.cancel();
    }
    running = [];
  };

  const settle = () => {
    if (current < 0) return;
    halt();
    const img = shots[current];
    const end = framing(img, 1.04);
    img.style.transform = end.transform;
    img.style.clipPath = "";
    Object.assign(reticle.style, end.box, { opacity: 1, boxShadow: shade(0.5) });
    placeCallout(end.box);
    callout.classList.add("shown");
  };

  const show = (i) => {
    const previous = current >= 0 ? shots[current] : null;
    const next = shots[i];
    halt();
    shots.forEach((img) => { img.style.clipPath = ""; });
    scan.style.opacity = "";
    scan.style.transform = "";
    current = i;
    buttons.forEach((button, b) => button.setAttribute("aria-pressed", String(b === i)));
    shots.forEach((img) => img.classList.remove("is-current", "is-leaving"));
    if (previous && previous !== next) previous.classList.add("is-leaving");
    next.classList.add("is-current");
    callout.classList.remove("shown");
    callout.textContent = next.dataset.note;

    if (reduceMotion) { settle(); return; }

    const focus = framing(next);
    const drift = framing(next, 1.04);
    const start = { left: `${reticle.offsetLeft}px`, top: `${reticle.offsetTop}px`, width: `${reticle.offsetWidth}px`, height: `${reticle.offsetHeight}px` };
    const startOpacity = Number(getComputedStyle(reticle).opacity);
    const startShade = previous ? 0.5 : 0;
    const at = (offset, box, extra) => ({ ...box, ...extra, offset });
    const full = "translate(0px, 0px) scale(1)";

    next.style.transform = full;
    next.style.clipPath = "";
    if (previous && previous !== next) {
      running.push(next.animate([{ clipPath: "inset(0 100% 0 0)" }, { clipPath: "inset(0 0% 0 0)" }], { duration: 850, easing: EASE }));
      running.push(scan.animate([
        { transform: "translateX(0px)", opacity: 1 },
        { transform: `translateX(${lens.clientWidth}px)`, opacity: 1, offset: 0.92 },
        { transform: `translateX(${lens.clientWidth}px)`, opacity: 0 },
      ], { duration: 850, easing: EASE }));
    } else {
      running.push(next.animate([{ opacity: 0 }, { opacity: 1 }], { duration: 500 }));
    }

    running.push(next.animate([
      { transform: full, offset: 0 },
      { transform: full, offset: 0.14, easing: EASE },
      { transform: focus.transform, offset: 0.38 },
      { transform: drift.transform, offset: 1 },
    ], { duration: DURATION, fill: "forwards" }));

    running.push(reticle.animate([
      at(0, start, { opacity: startOpacity, boxShadow: shade(startShade), easing: EASE }),
      at(0.12, frameBox(), { opacity: 1, boxShadow: shade(0) }),
      at(0.14, frameBox(), { opacity: 1, boxShadow: shade(0), easing: EASE }),
      at(0.38, focus.box, { opacity: 1, boxShadow: shade(0.5) }),
      at(1, drift.box, { opacity: 1, boxShadow: shade(0.5) }),
    ], { duration: DURATION, fill: "forwards" }));

    timeouts.push(setTimeout(() => {
      reticle.classList.remove("locked");
      void reticle.offsetWidth;
      reticle.classList.add("locked");
      placeCallout(drift.box);
      callout.classList.add("shown");
    }, DURATION * 0.38));
  };

  const stop = () => { clearInterval(timer); timer = 0; list.classList.remove("cycling"); };
  const start = () => {
    if (timer || list.dataset.touched || reduceMotion) return;
    list.classList.add("cycling");
    if (current < 0) show(0);
    timer = setInterval(() => show((current + 1) % shots.length), DURATION);
  };

  buttons.forEach((button, i) => button.addEventListener("click", () => {
    list.dataset.touched = "1";
    stop();
    show(i);
  }));

  new IntersectionObserver(([entry]) => {
    if (entry.isIntersecting) {
      if (current < 0 && (reduceMotion || list.dataset.touched)) show(0);
      start();
    } else stop();
  }, { threshold: 0.4 }).observe(figure);

  let resizeTimer = 0;
  window.addEventListener("resize", () => { clearTimeout(resizeTimer); resizeTimer = setTimeout(settle, 150); });

  if (reduceMotion || !window.matchMedia("(pointer: fine)").matches) return;
  const room = document.getElementById("room");
  const tilt = { x: 0, y: 0, tx: 0, ty: 0, active: false, running: false };
  const frame = () => {
    tilt.x += (tilt.tx - tilt.x) * 0.08;
    tilt.y += (tilt.ty - tilt.y) * 0.08;
    figure.style.setProperty("--tilt-x", `${tilt.x.toFixed(3)}deg`);
    figure.style.setProperty("--tilt-y", `${tilt.y.toFixed(3)}deg`);
    lens.style.setProperty("--shine", `${(tilt.y * 6).toFixed(2)}%`);
    if (tilt.active || Math.abs(tilt.x) + Math.abs(tilt.y) > 0.01) requestAnimationFrame(frame);
    else tilt.running = false;
  };
  const kick = () => { if (!tilt.running) { tilt.running = true; requestAnimationFrame(frame); } };
  room.addEventListener("pointermove", (event) => {
    const rect = figure.getBoundingClientRect();
    const nx = Math.max(-1, Math.min(1, (event.clientX - (rect.left + rect.width / 2)) / (rect.width / 2)));
    const ny = Math.max(-1, Math.min(1, (event.clientY - (rect.top + rect.height / 2)) / (rect.height / 2)));
    tilt.tx = -ny * 3.5;
    tilt.ty = nx * 5;
    tilt.active = true;
    kick();
  });
  room.addEventListener("pointerleave", () => { tilt.tx = 0; tilt.ty = 0; tilt.active = false; kick(); });
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
setupGlance();
animateFaq();
window.addEventListener("resize", drawTopoSoon);
document.fonts?.ready.then(() => scene?.setTheme(root.dataset.theme === "night"));
