import * as THREE from "three";
import { RoomEnvironment } from "three/addons/environments/RoomEnvironment.js";
import { RoundedBoxGeometry } from "three/addons/geometries/RoundedBoxGeometry.js";

const TAU = Math.PI * 2;
const PAD_DISTANCE = 4.3;
const PAD_RADIUS = 1.05;
const ANGLES = [180, -120, -60, 0, 120, 60];
const KINDS = ["slab", "tower", "slab", "handheld", "cube", "slim"];

const PALETTES = {
  day: {
    pad: "#D9D9DE", padEdge: "#BDBDC4", hub: "#DEDEE3", ringIdle: "#2A2A30", cableIdle: "#A2A2AA",
    live: "#17899A", lan: "#2C78BE", white: "#E9E9EC", black: "#1A1A1F", laptop: "#AEAFB6",
    hemi: 0.35, sun: 2.2, rim: 0.8, env: 0.42, exposure: 0.95, shadow: 0.2, cast: 0.38,
  },
  night: {
    pad: "#202027", padEdge: "#34343D", hub: "#22222A", ringIdle: "#3A3A43", cableIdle: "#3A3A44",
    live: "#73D9E5", lan: "#439DDD", white: "#D4D5DB", black: "#121216", laptop: "#4A4B54",
    hemi: 0.25, sun: 1.6, rim: 2.2, env: 0.35, exposure: 1.05, shadow: 0.45, cast: 0.45,
  },
};

const VIEWS = [
  { dist: 17.5, polar: 60, azimuth: 0, target: [0, 0.8, 0], shift: 0.1, side: 0, wire: 0, box: 0, opacity: 1, narrow: 1, spin: 1 },
  { dist: 4, polar: 62, azimuth: 96, target: [-4.1, 0.5, 0.2], shift: 0.2, side: 0.2, wire: 0, box: 0, opacity: 1, narrow: 0.35, spin: 0 },
  { dist: 20, polar: 4, azimuth: 0, target: [-2.4, 0, 0], shift: 0, side: 0, wire: 1, box: 0, opacity: 0.5, narrow: 0.3, spin: 0 },
  { dist: 17, polar: 64, azimuth: 30, target: [0, 1.4, 0], shift: 0, side: 0, wire: 0.4, box: 0, opacity: 0.12, narrow: 0.1, spin: 0 },
  { dist: 8.6, polar: 72, azimuth: 0, target: [0, 1.15, 0], shift: 0.05, side: 0, wire: 0, box: 1, opacity: 1, narrow: 1, spin: 0 },
];

const STEP_VIEWS = [
  { dist: 6.4, polar: 60, azimuth: 112, target: [-3.9, 0.4, 0.3], shift: 0.02, side: 0.17 },
  { dist: 13, polar: 58, azimuth: -90, target: [-0.6, 0.9, 0], shift: 0.04, side: 0.17 },
  { dist: 6.2, polar: 57, azimuth: 92, target: [-4.1, 0.35, 0.05], shift: 0.02, side: 0.17 },
  { dist: 16.5, polar: 50, azimuth: 60, target: [0, 0.4, 0], shift: 0.04, side: 0.17 },
];

function canvasTexture(width, height, draw) {
  const canvas = document.createElement("canvas");
  canvas.width = width;
  canvas.height = height;
  const ctx = canvas.getContext("2d");
  draw(ctx, width, height);
  const texture = new THREE.CanvasTexture(canvas);
  texture.colorSpace = THREE.SRGBColorSpace;
  texture.anisotropy = 8;
  texture.userData = { canvas, ctx };
  return texture;
}

function drawMark(ctx, x, y, size, a = "#73D9E5", b = "#497B84") {
  const s = size / 28;
  ctx.save();
  ctx.translate(x, y);
  ctx.scale(s, s);
  ctx.fillStyle = a;
  ctx.beginPath(); ctx.moveTo(2, 24); ctx.lineTo(10, 5); ctx.lineTo(18, 5); ctx.lineTo(10, 24); ctx.closePath(); ctx.fill();
  ctx.fillStyle = b;
  ctx.beginPath(); ctx.moveTo(11, 5); ctx.lineTo(19, 5); ctx.lineTo(27, 24); ctx.lineTo(19, 24); ctx.closePath(); ctx.fill();
  ctx.restore();
}

function roundedRect(w, h, r) {
  const s = new THREE.Shape();
  s.moveTo(-w / 2 + r, -h / 2);
  s.lineTo(w / 2 - r, -h / 2);
  s.quadraticCurveTo(w / 2, -h / 2, w / 2, -h / 2 + r);
  s.lineTo(w / 2, h / 2 - r);
  s.quadraticCurveTo(w / 2, h / 2, w / 2 - r, h / 2);
  s.lineTo(-w / 2 + r, h / 2);
  s.quadraticCurveTo(-w / 2, h / 2, -w / 2, h / 2 - r);
  s.lineTo(-w / 2, -h / 2 + r);
  s.quadraticCurveTo(-w / 2, -h / 2, -w / 2 + r, -h / 2);
  return s;
}

function extrudeUp(shape, height, bevel = 0.012) {
  const g = new THREE.ExtrudeGeometry(shape, { depth: height, bevelEnabled: true, bevelThickness: bevel, bevelSize: bevel, bevelSegments: 3, curveSegments: 18 });
  g.rotateX(-Math.PI / 2);
  return g;
}

function screenTexture() {
  return canvasTexture(512, 320, () => {});
}

function paintScreen(texture, state, progress = 0) {
  const { ctx } = texture.userData;
  const w = 512, h = 320;
  ctx.fillStyle = "#18181D";
  ctx.fillRect(0, 0, w, h);
  drawMark(ctx, 36, 30, 52);
  ctx.fillStyle = "#ECEBF1";
  ctx.font = "600 30px Geist, Segoe UI, sans-serif";
  ctx.fillText("CODCONNECT", 104, 70);
  if (state === "install") {
    ctx.fillStyle = "#8F8E99";
    ctx.font = "500 26px Geist, Segoe UI, sans-serif";
    ctx.fillText(progress < 1 ? "Installing…" : "Installed", 40, 170);
    ctx.fillStyle = "#2E2E36";
    ctx.fillRect(40, 200, w - 80, 16);
    ctx.fillStyle = "#73D9E5";
    ctx.fillRect(40, 200, (w - 80) * progress, 16);
  } else if (state === "room") {
    ctx.fillStyle = "#8F8E99";
    ctx.font = "500 24px Geist, Segoe UI, sans-serif";
    ctx.fillText("Room code", 40, 160);
    ctx.fillStyle = "#73D9E5";
    ctx.font = "600 64px Geist, Segoe UI, sans-serif";
    ctx.fillText("K7M4-P2", 40, 232);
  } else if (state === "on") {
    ctx.strokeStyle = "#73D9E5";
    ctx.lineWidth = 6;
    ctx.beginPath(); ctx.arc(w / 2, 196, 52, 0, TAU); ctx.stroke();
    ctx.beginPath(); ctx.moveTo(w / 2 - 22, 196); ctx.lineTo(w / 2 - 4, 214); ctx.lineTo(w / 2 + 26, 180); ctx.stroke();
  } else {
    ctx.fillStyle = "#5A5A64";
    ctx.font = "500 24px Geist, Segoe UI, sans-serif";
    ctx.fillText("Connecting…", 40, 190);
  }
  texture.needsUpdate = true;
}

function codePlate() {
  return canvasTexture(640, 220, (ctx, w, h) => {
    ctx.fillStyle = "rgba(18,18,22,.88)";
    ctx.beginPath(); ctx.roundRect(8, 8, w - 16, h - 16, 36); ctx.fill();
    ctx.strokeStyle = "rgba(115,217,229,.8)";
    ctx.lineWidth = 3;
    ctx.stroke();
    ctx.fillStyle = "#8F8E99";
    ctx.font = "500 30px Geist, Segoe UI, sans-serif";
    ctx.textAlign = "center";
    ctx.fillText("ROOM CODE", w / 2, 76);
    ctx.fillStyle = "#ECEBF1";
    ctx.font = "600 92px Geist, Segoe UI, sans-serif";
    ctx.fillText("K7M4-P2", w / 2, 170);
  });
}

function hubLogo(ink) {
  return canvasTexture(512, 512, (ctx, w) => {
    drawMark(ctx, w / 2 - 70, 150, 140);
    ctx.fillStyle = ink;
    ctx.textAlign = "center";
    ctx.font = "600 46px Geist, Segoe UI, sans-serif";
    ctx.fillText("CODCONNECT", w / 2, 352);
  });
}

function boxFaces() {
  const face = (w, h, draw) => canvasTexture(w, h, (ctx) => {
    ctx.fillStyle = "#151518";
    ctx.fillRect(0, 0, w, h);
    ctx.strokeStyle = "rgba(255,255,255,.1)";
    ctx.lineWidth = 2;
    ctx.strokeRect(18, 18, w - 36, h - 36);
    draw(ctx, w, h);
  });
  const front = face(1024, 640, (ctx, w, h) => {
    drawMark(ctx, 84, 84, 110);
    ctx.fillStyle = "#F2F2F5";
    ctx.font = "500 118px Geist, Segoe UI, sans-serif";
    ctx.fillText("CODCONNECT", 80, h - 160);
    ctx.fillStyle = "rgba(255,255,255,.66)";
    ctx.font = "400 40px Geist, Segoe UI, sans-serif";
    ctx.fillText("Six homes. One LAN.", 84, h - 92);
    ctx.textAlign = "right";
    ctx.font = "500 24px Geist, Segoe UI, sans-serif";
    ctx.fillText("FOR WINDOWS 10 / 11", w - 70, 104);
  });
  const back = face(1024, 640, (ctx, w, h) => {
    ctx.fillStyle = "#F2F2F5";
    ctx.font = "500 30px Geist, Segoe UI, sans-serif";
    ctx.fillText("FREE AND OPEN SOURCE", 80, 120);
    for (let i = 0, x = 80; i < 34; i++) {
      const bar = [3, 6, 2, 9, 4][i % 5];
      ctx.fillRect(x, h - 230, bar, 130);
      x += bar + 5;
    }
  });
  const spine = face(384, 656, (ctx, w, h) => {
    drawMark(ctx, w / 2 - 44, 60, 88);
    ctx.save();
    ctx.translate(w / 2 + 22, h - 70);
    ctx.rotate(-Math.PI / 2);
    ctx.fillStyle = "#F2F2F5";
    ctx.font = "500 64px Geist, Segoe UI, sans-serif";
    ctx.fillText("CODCONNECT", 0, 0);
    ctx.restore();
  });
  const top = face(1024, 374, (ctx, w, h) => {
    drawMark(ctx, 70, h / 2 - 40, 80);
    ctx.fillStyle = "rgba(255,255,255,.72)";
    ctx.font = "500 30px Geist, Segoe UI, sans-serif";
    ctx.fillText("SIX HOMES. ONE LAN.", 180, h / 2 + 12);
  });
  return [spine, spine, top, top, front, back];
}

export function createScene(canvas, { reduceMotion = false } = {}) {
  let renderer;
  try {
    renderer = new THREE.WebGLRenderer({ canvas, antialias: true, alpha: true, powerPreference: "high-performance" });
  } catch {
    return null;
  }
  renderer.setClearColor(0x000000, 0);
  renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
  renderer.toneMapping = THREE.ACESFilmicToneMapping;
  renderer.shadowMap.enabled = true;
  renderer.shadowMap.type = THREE.PCFSoftShadowMap;

  const scene = new THREE.Scene();
  const pmrem = new THREE.PMREMGenerator(renderer);
  scene.environment = pmrem.fromScene(new RoomEnvironment(), 0.04).texture;
  const camera = new THREE.PerspectiveCamera(30, 1, 0.1, 120);
  const hemi = new THREE.HemisphereLight(0xffffff, 0x9a9aa4, 0.5);
  const sun = new THREE.DirectionalLight(0xfff6ec, 2.4);
  sun.position.set(7, 8, 5);
  sun.castShadow = true;
  sun.shadow.mapSize.set(2048, 2048);
  Object.assign(sun.shadow.camera, { left: -8, right: 8, top: 8, bottom: -8, near: 1, far: 30 });
  sun.shadow.bias = -0.0004;
  sun.shadow.normalBias = 0.02;
  sun.shadow.radius = 5;
  const rim = new THREE.DirectionalLight(0x9fdde6, 0.9);
  rim.position.set(-7, 5, -8);
  const roomLight = new THREE.PointLight(0x439ddd, 0, 10, 1.6);
  roomLight.position.set(0, 1.2, 0);
  scene.add(hemi, sun, rim, roomLight);

  const colors = {};
  const mat = {
    pad: new THREE.MeshStandardMaterial({ roughness: 0.8, metalness: 0 }),
    padEdge: new THREE.MeshStandardMaterial({ roughness: 0.8 }),
    hub: new THREE.MeshPhysicalMaterial({ roughness: 0.45, metalness: 0, clearcoat: 0.5, clearcoatRoughness: 0.3 }),
    ring: new THREE.MeshStandardMaterial({ roughness: 0.28, metalness: 0.35 }),
    orbit: new THREE.MeshBasicMaterial({ transparent: true, opacity: 0.35, toneMapped: false }),
    white: new THREE.MeshPhysicalMaterial({ roughness: 0.32, metalness: 0, clearcoat: 0.6, clearcoatRoughness: 0.25 }),
    black: new THREE.MeshPhysicalMaterial({ roughness: 0.22, metalness: 0.1, clearcoat: 0.9, clearcoatRoughness: 0.15 }),
    matte: new THREE.MeshStandardMaterial({ roughness: 0.7, metalness: 0.05 }),
    laptop: new THREE.MeshStandardMaterial({ roughness: 0.3, metalness: 0.8 }),
    keys: new THREE.MeshStandardMaterial({ color: 0x1a1a1f, roughness: 0.9, polygonOffset: true, polygonOffsetFactor: -2, polygonOffsetUnits: -2 }),
    red: new THREE.MeshPhysicalMaterial({ color: 0xe5484d, roughness: 0.4, clearcoat: 0.5 }),
    blue: new THREE.MeshPhysicalMaterial({ color: 0x1fa6e0, roughness: 0.4, clearcoat: 0.5 }),
    cableIdle: new THREE.LineBasicMaterial({ transparent: true, opacity: 0.9, toneMapped: false }),
    live: new THREE.MeshBasicMaterial({ toneMapped: false }),
    accent: new THREE.MeshBasicMaterial({ transparent: true, toneMapped: false }),
  };
  const transparentGroup = ["pad", "padEdge", "white", "black", "matte", "laptop", "keys", "red", "blue"];

  const shadow = new THREE.Mesh(new THREE.PlaneGeometry(16, 16), new THREE.MeshBasicMaterial({
    map: canvasTexture(256, 256, (ctx, w) => {
      const g = ctx.createRadialGradient(w / 2, w / 2, 0, w / 2, w / 2, w / 2);
      g.addColorStop(0, "rgba(0,0,0,.5)");
      g.addColorStop(1, "rgba(0,0,0,0)");
      ctx.fillStyle = g;
      ctx.fillRect(0, 0, w, w);
    }),
    transparent: true, depthWrite: false,
  }));
  shadow.rotation.x = -Math.PI / 2;
  shadow.position.y = 0.001;
  const catcher = new THREE.Mesh(new THREE.PlaneGeometry(40, 40), new THREE.ShadowMaterial({ opacity: 0.25 }));
  catcher.rotation.x = -Math.PI / 2;
  catcher.receiveShadow = true;

  const lan = new THREE.Group();
  scene.add(lan);
  lan.add(catcher, shadow);

  const hub = new THREE.Mesh(new THREE.CylinderGeometry(1.3, 1.36, 0.3, 72), mat.hub);
  hub.position.y = 0.15;
  const logoMaterial = new THREE.MeshBasicMaterial({ transparent: true, depthWrite: false, toneMapped: false, polygonOffset: true, polygonOffsetFactor: -2, polygonOffsetUnits: -2 });
  const logo = new THREE.Mesh(new THREE.PlaneGeometry(1.45, 1.45), logoMaterial);
  logo.rotation.x = -Math.PI / 2;
  logo.position.y = 0.305;
  const ring = new THREE.Mesh(new THREE.TorusGeometry(1.85, 0.08, 18, 180), mat.ring);
  ring.rotation.x = -Math.PI / 2;
  ring.position.y = 0.12;
  const inner = new THREE.Mesh(new THREE.TorusGeometry(1.62, 0.028, 12, 180), mat.ring);
  inner.rotation.x = -Math.PI / 2;
  inner.position.y = 0.12;
  const orbit = new THREE.Mesh(new THREE.RingGeometry(2.78, 2.795, 160), mat.orbit);
  orbit.rotation.x = -Math.PI / 2;
  orbit.position.y = 0.01;
  hub.castShadow = true;
  hub.receiveShadow = true;
  ring.castShadow = true;
  lan.add(hub, logo, ring, inner, orbit);

  const glowTexture = canvasTexture(128, 128, (ctx, w) => {
    const g = ctx.createRadialGradient(w / 2, w / 2, 0, w / 2, w / 2, w / 2);
    g.addColorStop(0, "rgba(255,255,255,1)");
    g.addColorStop(0.35, "rgba(255,255,255,.35)");
    g.addColorStop(1, "rgba(255,255,255,0)");
    ctx.fillStyle = g;
    ctx.fillRect(0, 0, w, w);
  });
  const blobTexture = canvasTexture(128, 128, (ctx, w) => {
    const g = ctx.createRadialGradient(w / 2, w / 2, 0, w / 2, w / 2, w / 2);
    g.addColorStop(0, "rgba(0,0,0,.55)");
    g.addColorStop(0.6, "rgba(0,0,0,.18)");
    g.addColorStop(1, "rgba(0,0,0,0)");
    ctx.fillStyle = g;
    ctx.fillRect(0, 0, w, w);
  });
  const grilleTexture = canvasTexture(256, 256, (ctx, w) => {
    ctx.fillStyle = "#101014";
    ctx.fillRect(0, 0, w, w);
    ctx.fillStyle = "#26262d";
    for (let y = 8; y < w; y += 14) {
      for (let x = 8 + ((y / 14) % 2) * 7; x < w; x += 14) {
        ctx.beginPath(); ctx.arc(x, y, 4, 0, TAU); ctx.fill();
      }
    }
  });
  const handheldScreen = canvasTexture(256, 150, (ctx, w, h) => {
    const g = ctx.createLinearGradient(0, 0, w, h);
    g.addColorStop(0, "#1b3a52");
    g.addColorStop(1, "#0d1b28");
    ctx.fillStyle = g;
    ctx.fillRect(0, 0, w, h);
    ctx.fillStyle = "rgba(115,217,229,.9)";
    ctx.font = "600 22px Geist, Segoe UI, sans-serif";
    ctx.fillText("LAN", 18, 38);
    for (let i = 0; i < 3; i++) {
      ctx.fillStyle = `rgba(255,255,255,${0.14 + i * 0.05})`;
      ctx.fillRect(18, 56 + i * 28, w - 36, 20);
    }
  });

  function rbox(w, h, d, r, material) {
    return new THREE.Mesh(new RoundedBoxGeometry(w, h, d, 4, r), material);
  }

  function lightStrip(geometry) {
    return new THREE.Mesh(geometry, new THREE.MeshBasicMaterial({ color: 0x5a5a64, toneMapped: false }));
  }

  function glow(scale) {
    const sprite = new THREE.Sprite(new THREE.SpriteMaterial({ map: glowTexture, transparent: true, opacity: 0, depthWrite: false, blending: THREE.AdditiveBlending, toneMapped: false }));
    sprite.scale.set(scale[0], scale[1], 1);
    return sprite;
  }

  function blob(w, d) {
    const mesh = new THREE.Mesh(new THREE.PlaneGeometry(w, d), new THREE.MeshBasicMaterial({ map: blobTexture, transparent: true, depthWrite: false, toneMapped: false }));
    mesh.rotation.x = -Math.PI / 2;
    mesh.position.y = 0.004;
    return mesh;
  }

  function flaredWing(side) {
    const shape = new THREE.Shape();
    shape.moveTo(-0.25, 0);
    shape.bezierCurveTo(-0.33, 0.36, -0.4, 0.82, -0.32, 1.18);
    shape.quadraticCurveTo(0, 1.24, 0.31, 1.2);
    shape.bezierCurveTo(0.38, 0.84, 0.34, 0.32, 0.24, 0);
    shape.quadraticCurveTo(0, -0.02, -0.25, 0);
    const geometry = new THREE.ExtrudeGeometry(shape, { depth: 0.026, bevelEnabled: true, bevelThickness: 0.012, bevelSize: 0.012, bevelSegments: 4, curveSegments: 32 });
    geometry.rotateY(Math.PI / 2);
    const position = geometry.attributes.position;
    for (let i = 0; i < position.count; i++) {
      const y = position.getY(i);
      position.setX(i, position.getX(i) + side * 0.07 * Math.pow(y / 1.2, 2));
    }
    geometry.computeVertexNormals();
    return geometry;
  }

  function buildConsole(kind) {
    const group = new THREE.Group();
    const lights = [];
    const glows = [];
    const addGlow = (sprite, x, y, z) => { sprite.position.set(x, y, z); group.add(sprite); glows.push(sprite); };
    if (kind === "tower") {
      const core = rbox(0.19, 1.04, 0.56, 0.05, mat.black);
      core.position.y = 0.6;
      for (const side of [-1, 1]) {
        const wing = new THREE.Mesh(flaredWing(side), mat.white);
        wing.position.set(side * 0.105 - (side > 0 ? 0.026 : 0), 0.06, 0);
        group.add(wing);
        const strip = lightStrip(new THREE.BoxGeometry(0.008, 0.96, 0.01));
        strip.position.set(side * 0.097, 0.6, 0.281);
        group.add(strip);
        lights.push(strip);
        addGlow(glow([0.12, 1.1]), side * 0.097, 0.6, 0.3);
      }
      const port = rbox(0.05, 0.025, 0.01, 0.005, mat.matte);
      port.position.set(0, 0.28, 0.282);
      const stand = new THREE.Mesh(new THREE.CylinderGeometry(0.2, 0.22, 0.035, 48), mat.matte);
      stand.position.y = 0.0175;
      group.add(core, port, stand, blob(0.9, 0.9));
    } else if (kind === "slab") {
      const profile = new THREE.Shape();
      profile.moveTo(-0.5, 0); profile.lineTo(0.5, 0); profile.lineTo(0.6, 0.085); profile.lineTo(-0.4, 0.085); profile.lineTo(-0.5, 0);
      const slabGeometry = new THREE.ExtrudeGeometry(profile, { depth: 0.66, bevelEnabled: true, bevelThickness: 0.012, bevelSize: 0.01, bevelSegments: 4 });
      slabGeometry.translate(0, 0, -0.33);
      const lower = new THREE.Mesh(slabGeometry, mat.matte);
      lower.position.y = 0.05;
      const upper = new THREE.Mesh(slabGeometry, mat.black);
      upper.position.set(0.045, 0.155, 0);
      upper.scale.set(0.9, 1, 1);
      const groove = new THREE.Mesh(new THREE.BoxGeometry(1.0, 0.02, 0.62), new THREE.MeshStandardMaterial({ color: 0x0a0a0c, roughness: 0.9 }));
      groove.position.set(0.03, 0.148, 0);
      const bar = lightStrip(new THREE.BoxGeometry(0.03, 0.008, 0.64));
      bar.position.set(0.14, 0.256, 0);
      lights.push(bar);
      const edge = lightStrip(new THREE.BoxGeometry(0.95, 0.008, 0.012));
      edge.position.set(0.03, 0.148, 0.34);
      lights.push(edge);
      for (const x of [-0.42, 0.42]) {
        const foot = new THREE.Mesh(new THREE.CylinderGeometry(0.03, 0.03, 0.05, 16), mat.matte);
        foot.position.set(x, 0.025, 0);
        group.add(foot);
      }
      group.add(lower, upper, groove, bar, edge, blob(1.4, 1.0));
      addGlow(glow([0.2, 0.9]), 0.14, 0.27, 0);
      group.rotation.y = Math.PI / 2;
    } else if (kind === "cube") {
      const body = rbox(0.5, 1.0, 0.5, 0.06, mat.matte);
      body.position.y = 0.52;
      const rim = new THREE.Mesh(new THREE.TorusGeometry(0.2, 0.012, 12, 64), mat.black);
      rim.rotation.x = -Math.PI / 2;
      rim.position.y = 1.02;
      const grille = new THREE.Mesh(new THREE.CircleGeometry(0.2, 64), new THREE.MeshStandardMaterial({ map: grilleTexture, roughness: 0.8, polygonOffset: true, polygonOffsetFactor: -2 }));
      grille.rotation.x = -Math.PI / 2;
      grille.position.y = 1.021;
      const underglow = lightStrip(new THREE.RingGeometry(0.2, 0.225, 64));
      underglow.rotation.x = -Math.PI / 2;
      underglow.position.y = 1.022;
      lights.push(underglow);
      const power = lightStrip(new THREE.CircleGeometry(0.018, 24));
      power.position.set(-0.17, 0.9, 0.2505);
      lights.push(power);
      const slot = new THREE.Mesh(new THREE.BoxGeometry(0.3, 0.012, 0.01), new THREE.MeshStandardMaterial({ color: 0x08080a }));
      slot.position.set(0.02, 0.62, 0.251);
      group.add(body, rim, grille, underglow, power, slot, blob(0.9, 0.9));
      addGlow(glow([0.7, 0.7]), 0, 1.06, 0);
    } else if (kind === "slim") {
      const body = rbox(0.76, 0.19, 0.52, 0.035, mat.white);
      body.position.y = 0.105;
      const vent = new THREE.Mesh(new THREE.CircleGeometry(0.15, 64), new THREE.MeshStandardMaterial({ map: grilleTexture, roughness: 0.8, polygonOffset: true, polygonOffsetFactor: -2 }));
      vent.rotation.x = -Math.PI / 2;
      vent.position.set(0.15, 0.2005, 0);
      const ring = new THREE.Mesh(new THREE.TorusGeometry(0.15, 0.01, 10, 64), mat.black);
      ring.rotation.x = -Math.PI / 2;
      ring.position.set(0.15, 0.2, 0);
      const light = lightStrip(new THREE.CircleGeometry(0.014, 20));
      light.position.set(-0.3, 0.12, 0.261);
      lights.push(light);
      group.add(body, vent, ring, light, blob(1.1, 0.8));
      addGlow(glow([0.18, 0.18]), -0.3, 0.12, 0.28);
    } else {
      const dock = rbox(0.66, 0.36, 0.16, 0.04, mat.matte);
      dock.position.y = 0.19;
      const tablet = rbox(0.5, 0.3, 0.035, 0.015, mat.black);
      tablet.position.set(0, 0.47, 0.03);
      const screen = new THREE.Mesh(new THREE.PlaneGeometry(0.43, 0.24), new THREE.MeshBasicMaterial({ map: handheldScreen, toneMapped: false, polygonOffset: true, polygonOffsetFactor: -2 }));
      screen.position.set(0, 0.47, 0.049);
      for (const [side, material] of [[-1, mat.red], [1, mat.blue]]) {
        const pad = rbox(0.09, 0.3, 0.04, 0.035, material);
        pad.position.set(side * 0.3, 0.47, 0.03);
        const stick = new THREE.Mesh(new THREE.CylinderGeometry(0.018, 0.02, 0.02, 20), mat.black);
        stick.rotation.x = Math.PI / 2;
        stick.position.set(side * 0.3, 0.52 + side * -0.05, 0.058);
        group.add(pad, stick);
        for (let b = 0; b < 4; b++) {
          const angle = b * Math.PI / 2;
          const button = new THREE.Mesh(new THREE.CylinderGeometry(0.008, 0.008, 0.01, 12), mat.black);
          button.rotation.x = Math.PI / 2;
          button.position.set(side * 0.3 + Math.cos(angle) * 0.022, 0.42 + side * 0.05 + Math.sin(angle) * 0.022, 0.055);
          group.add(button);
        }
      }
      const led = lightStrip(new THREE.BoxGeometry(0.04, 0.008, 0.01));
      led.position.set(0.24, 0.06, 0.081);
      lights.push(led);
      group.add(dock, tablet, screen, led, blob(1.0, 0.6));
      addGlow(glow([0.8, 0.5]), 0, 0.47, 0.06);
    }
    return { group, lights, glows };
  }

  function buildLaptop() {
    const group = new THREE.Group();
    const base = rbox(0.5, 0.022, 0.34, 0.01, mat.laptop);
    base.position.y = 0.011;
    const keys = new THREE.Mesh(new THREE.PlaneGeometry(0.44, 0.16), mat.keys);
    keys.rotation.x = -Math.PI / 2;
    keys.position.set(0, 0.0235, -0.035);
    const pad = new THREE.Mesh(new THREE.PlaneGeometry(0.14, 0.08), new THREE.MeshStandardMaterial({ color: 0x8c8d94, roughness: 0.5, metalness: 0.6, polygonOffset: true, polygonOffsetFactor: -2 }));
    pad.rotation.x = -Math.PI / 2;
    pad.position.set(0, 0.0235, 0.1);
    const hinge = new THREE.Group();
    hinge.position.set(0, 0.022, -0.165);
    hinge.rotation.x = -0.22;
    const lid = rbox(0.5, 0.32, 0.012, 0.008, mat.laptop);
    lid.position.set(0, 0.16, 0);
    const texture = screenTexture();
    paintScreen(texture, "idle");
    const display = new THREE.Mesh(new THREE.PlaneGeometry(0.46, 0.28), new THREE.MeshBasicMaterial({ map: texture, toneMapped: false, polygonOffset: true, polygonOffsetFactor: -4, polygonOffsetUnits: -4 }));
    display.position.set(0, 0.16, 0.0075);
    hinge.add(lid, display);
    group.add(base, keys, pad, hinge, blob(0.7, 0.5));
    return { group, texture };
  }

  const padGeometry = new THREE.CylinderGeometry(PAD_RADIUS, PAD_RADIUS + 0.03, 0.06, 72);
  const rimGeometry = new THREE.TorusGeometry(PAD_RADIUS + 0.01, 0.014, 8, 96);
  const cableStart = PAD_DISTANCE - PAD_RADIUS;
  const cableEnd = 1.95;
  const cableLength = cableStart - cableEnd;
  const liveGeometry = new THREE.CylinderGeometry(0.03, 0.03, cableLength, 10);
  liveGeometry.translate(0, cableLength / 2, 0);

  const homes = ANGLES.map((degrees, i) => {
    const angle = degrees * Math.PI / 180;
    const dir = new THREE.Vector3(Math.cos(angle), 0, Math.sin(angle));
    const home = new THREE.Group();
    home.position.copy(dir).multiplyScalar(PAD_DISTANCE);
    home.lookAt(0, 0, 0);

    const pad = new THREE.Mesh(padGeometry, mat.pad);
    pad.position.y = 0.03;
    const rimMaterial = new THREE.MeshBasicMaterial({ color: 0x9a9aa2, transparent: true, opacity: 0.6, toneMapped: false });
    const rimMesh = new THREE.Mesh(rimGeometry, rimMaterial);
    rimMesh.rotation.x = -Math.PI / 2;
    rimMesh.position.y = 0.062;

    const { group: consoleModel, lights, glows } = buildConsole(KINDS[i]);
    consoleModel.position.set(-0.22, 0.06, 0.05);
    consoleModel.rotation.y += 0.35;
    const laptop = buildLaptop();
    laptop.group.position.set(0.5, 0.06, 0.28);
    laptop.group.rotation.y = -0.55;
    home.add(pad, rimMesh, consoleModel, laptop.group);
    pad.receiveShadow = true;
    for (const group of [consoleModel, laptop.group]) {
      group.traverse((node) => { if (node.isMesh && node.material.toneMapped !== false && !node.material.transparent) { node.castShadow = true; node.receiveShadow = true; } });
    }
    lan.add(home);

    const idle = new THREE.Line(new THREE.BufferGeometry().setFromPoints([
      dir.clone().multiplyScalar(cableStart).setY(0.05), dir.clone().multiplyScalar(cableEnd).setY(0.05),
    ]), mat.cableIdle);
    const live = new THREE.Mesh(liveGeometry, mat.live);
    live.position.copy(dir).multiplyScalar(cableStart).setY(0.05);
    live.quaternion.setFromUnitVectors(new THREE.Vector3(0, 1, 0), dir.clone().negate());
    live.scale.set(1, 0.0001, 1);
    lan.add(idle, live);

    const top = new THREE.Vector3(-0.22, 1.45, 0.05);
    return { home, rimMaterial, lights, glows, laptop, live, dir, joined: 0, target: 0, top, focus: 1, screenState: "" };
  });

  const you = homes[0];

  const code = new THREE.Sprite(new THREE.SpriteMaterial({ map: codePlate(), transparent: true, depthWrite: false, toneMapped: false }));
  code.position.set(0, 1.9, 0);
  code.scale.set(0.001, 0.001, 1);
  lan.add(code);

  const inviteDot = new THREE.SphereGeometry(0.05, 14, 14);
  const invites = homes.slice(1).map((homeInfo) => {
    const end = homeInfo.dir.clone().multiplyScalar(PAD_DISTANCE).setY(1.2);
    const start = new THREE.Vector3(0, 1.6, 0);
    const mid = start.clone().lerp(end, 0.5).setY(3.1);
    const curve = new THREE.QuadraticBezierCurve3(start, mid, end);
    const dots = [0, 1, 2, 3].map(() => {
      const dot = new THREE.Mesh(inviteDot, new THREE.MeshBasicMaterial({ transparent: true, opacity: 0, depthWrite: false, toneMapped: false }));
      lan.add(dot);
      return dot;
    });
    return { curve, dots };
  });

  const ripples = [0, 1, 2].map(() => {
    const mesh = new THREE.Mesh(new THREE.RingGeometry(0.93, 1, 72), new THREE.MeshBasicMaterial({ transparent: true, opacity: 0, side: THREE.DoubleSide, depthWrite: false, toneMapped: false }));
    mesh.rotation.x = -Math.PI / 2;
    mesh.position.set(0.5, 0.075, 0.28);
    you.home.add(mesh);
    return mesh;
  });
  const ethernetCurve = new THREE.CatmullRomCurve3([
    new THREE.Vector3(0.42, 0.075, 0.12), new THREE.Vector3(0.3, 0.075, -0.3), new THREE.Vector3(-0.05, 0.075, -0.35), new THREE.Vector3(-0.2, 0.075, -0.12),
  ]);
  const ethernet = new THREE.Mesh(new THREE.TubeGeometry(ethernetCurve, 40, 0.022, 10, false), mat.accent);
  ethernet.visible = false;
  const pulse = new THREE.Mesh(new THREE.SphereGeometry(0.045, 14, 14), new THREE.MeshBasicMaterial({ color: 0xffffff, toneMapped: false }));
  pulse.visible = false;
  you.home.add(ethernet, pulse);

  const packetGeometry = new THREE.SphereGeometry(0.055, 12, 12);
  const packets = [];
  let lastPacket = 0;

  const box = new THREE.Mesh(new THREE.BoxGeometry(2.6, 1.625, 0.95), boxFaces().map((map) => new THREE.MeshPhysicalMaterial({ map, roughness: 0.55, metalness: 0.05, clearcoat: 0.4, clearcoatRoughness: 0.35 })));
  box.position.set(0, 1.2, 0);
  box.castShadow = true;
  box.scale.setScalar(0.001);
  scene.add(box);

  let night = false, dark = false;
  let readyMix = 0, readyTarget = 0;
  let viewPos = 0, smoothPos = 0;
  let step = 0, mode = "wifi";
  let width = 1, height = 1;
  const listeners = [];
  const stepView = { ...STEP_VIEWS[0], target: [...STEP_VIEWS[0].target] };
  let installProgress = 0;
  let lastScreenPaint = 0;

  function palette() { return PALETTES[night || dark ? "night" : "day"]; }

  function applyPalette() {
    const p = palette();
    for (const key of Object.keys(p)) if (typeof p[key] === "string") colors[key] = new THREE.Color(p[key]);
    mat.pad.color.copy(colors.pad);
    mat.padEdge.color.copy(colors.padEdge);
    mat.hub.color.copy(colors.hub);
    mat.orbit.color.copy(colors.cableIdle);
    mat.cableIdle.color.copy(colors.cableIdle);
    mat.white.color.copy(colors.white);
    mat.black.color.copy(colors.black);
    mat.matte.color.copy(colors.black).lerp(colors.white, night || dark ? 0.1 : 0.06);
    mat.laptop.color.copy(colors.laptop);
    hemi.intensity = p.hemi;
    sun.intensity = p.sun;
    rim.intensity = p.rim;
    scene.environmentIntensity = p.env;
    renderer.toneMappingExposure = p.exposure;
    shadow.material.opacity = p.shadow;
    catcher.material.opacity = p.cast;
    logoMaterial.map = hubLogo(night || dark ? "#ECEBF1" : "#121214");
    logoMaterial.needsUpdate = true;
  }

  function resize() {
    width = canvas.clientWidth || window.innerWidth;
    height = canvas.clientHeight || window.innerHeight;
    renderer.setSize(width, height, false);
    camera.aspect = width / height;
    camera.updateProjectionMatrix();
  }

  function blendView(pos) {
    const i = Math.max(0, Math.min(VIEWS.length - 1, Math.floor(pos)));
    const j = Math.min(VIEWS.length - 1, i + 1);
    const f = pos - i;
    const t = f * f * (3 - 2 * f);
    const a = { ...VIEWS[i] }, b = { ...VIEWS[j] };
    if (i === 1) Object.assign(a, stepView);
    if (j === 1) Object.assign(b, stepView);
    const mix = (x, y) => x + (y - x) * t;
    const keys = ["dist", "polar", "azimuth", "shift", "side", "wire", "box", "opacity", "narrow", "spin"];
    const view = Object.fromEntries(keys.map((k) => [k, mix(a[k], b[k])]));
    view.target = [0, 1, 2].map((k) => mix(a.target[k], b.target[k]));
    return view;
  }

  let spin = 0;
  let previous = performance.now();
  const probe = new THREE.Vector3();

  function ease(x) { return x < 0.5 ? 2 * x * x : 1 - Math.pow(-2 * x + 2, 2) / 2; }

  function frame(now) {
    requestAnimationFrame(frame);
    const dt = Math.min(0.25, (now - previous) / 1000);
    previous = now;
    if (document.hidden) return;
    const k = reduceMotion ? 1 : 1 - Math.pow(0.002, dt);

    const goal = STEP_VIEWS[step];
    for (const key of ["dist", "polar", "azimuth", "shift", "side"]) stepView[key] += (goal[key] - stepView[key]) * k;
    for (let a = 0; a < 3; a++) stepView.target[a] += (goal.target[a] - stepView.target[a]) * k;

    smoothPos += (viewPos - smoothPos) * k;
    const view = blendView(smoothPos);
    if (!reduceMotion) spin = (spin + dt * 0.1) % TAU;
    const spinAngle = spin > Math.PI ? spin - TAU : spin;
    const polar = view.polar * Math.PI / 180;
    const azimuth = view.azimuth * Math.PI / 180 + spinAngle * view.spin;
    const dist = view.dist * Math.max(1, 1.35 / camera.aspect);
    const narrowScreen = width < 860;
    const side = narrowScreen ? 0 : view.side;
    const lift = narrowScreen && smoothPos > 0.5 && smoothPos < 1.5 ? 0.2 : view.shift;
    camera.setViewOffset(width, height, -side * width, -lift * height, width, height);
    const target = new THREE.Vector3(...view.target);
    camera.position.set(
      target.x + dist * Math.sin(polar) * Math.sin(azimuth),
      target.y + dist * Math.cos(polar),
      target.z + dist * Math.sin(polar) * Math.cos(azimuth));
    camera.up.set(0, 1, 0);
    if (polar < 0.2) camera.up.set(Math.sin(azimuth + Math.PI), 0, Math.cos(azimuth + Math.PI));
    camera.lookAt(target);
    canvas.style.opacity = (width < 860 ? view.narrow : view.opacity).toFixed(3);

    const inSetup = Math.max(0, 1 - Math.abs(smoothPos - 1) * 2);
    const showReady = Math.max(readyTarget * (1 - inSetup), inSetup * (step === 3 ? 1 : 0));
    readyMix += (showReady - readyMix) * (reduceMotion ? 1 : Math.min(1, dt * 2.4));
    const glowing = night || dark;
    mat.ring.color.copy(colors.ringIdle).lerp(colors.lan, readyMix);
    mat.ring.emissive.copy(colors.lan).multiplyScalar(readyMix * (glowing ? 0.55 : 0.22));
    roomLight.intensity = readyMix * (glowing ? 14 : 5);
    const liveColor = colors.live.clone().lerp(colors.lan, readyMix);
    mat.live.color.copy(liveColor);
    mat.accent.color.copy(colors.live);

    const fade = 1 - 0.85 * view.wire;
    for (const name of [...transparentGroup, "hub"]) {
      const material = mat[name];
      const see = fade < 0.995;
      if (material.transparent !== see) { material.transparent = see; material.depthWrite = !see; material.needsUpdate = true; }
      material.opacity = see ? fade : 1;
    }
    sun.castShadow = fade > 0.9;

    homes.forEach((h, i) => {
      const wanted = inSetup > 0.5 ? (step === 3 ? 1 : i === 0 && step >= 2 ? 1 : 0) : h.target;
      h.joined += (wanted - h.joined) * (reduceMotion ? 1 : Math.min(1, dt * 4.5));
      h.live.scale.y = Math.max(0.0001, h.joined);
      const focusGoal = i > 0 && inSetup > 0.5 && (step === 0 || step === 2) ? 0 : 1;
      h.focus += (focusGoal - h.focus) * (reduceMotion ? 1 : Math.min(1, dt * 5));
      const shown = h.focus * h.focus * (3 - 2 * h.focus);
      h.home.scale.setScalar(Math.max(0.001, shown));
      h.home.visible = shown > 0.01;
      h.rimMaterial.color.copy(colors.padEdge).lerp(liveColor, h.joined);
      h.rimMaterial.opacity = 0.5 + h.joined * 0.5;
      for (const light of h.lights) light.material.color.set(0x5a5a64).lerp(liveColor, h.joined);
      for (const sprite of h.glows) {
        sprite.material.color.copy(liveColor);
        sprite.material.opacity = h.joined * (glowing ? 0.85 : 0.45);
      }
      let state = h.joined > 0.5 ? "on" : "idle";
      if (i === 0 && inSetup > 0.5) state = step === 0 ? "install" : step === 1 ? "room" : "on";
      if (state !== h.screenState || (state === "install" && now - lastScreenPaint > 80)) {
        if (state === "install") lastScreenPaint = now;
        h.screenState = state;
        paintScreen(h.laptop.texture, state, installProgress);
      }
    });

    if (inSetup > 0.5 && step === 0) {
      installProgress = reduceMotion ? 1 : Math.min(1, installProgress + dt * 0.45);
    } else if (step !== 0) {
      installProgress = 0;
    }

    const codeGoal = inSetup > 0.5 && step === 1 ? 1 : 0;
    const codeScale = code.scale.x / 1.5;
    const nextCode = codeScale + (codeGoal - codeScale) * (reduceMotion ? 1 : Math.min(1, dt * 5));
    code.scale.set(Math.max(0.001, nextCode * 1.5), Math.max(0.001, nextCode * 0.52), 1);
    code.position.y = 1.9 + (reduceMotion ? 0 : Math.sin(now / 700) * 0.06);
    invites.forEach((invite, i) => {
      invite.dots.forEach((dot, d) => {
        if (!codeGoal) { dot.material.opacity = 0; return; }
        const t = reduceMotion ? (d + 1) / 5 : ((now / 1600) + i * 0.17 + d * 0.25) % 1;
        dot.position.copy(invite.curve.getPoint(t));
        dot.material.color.copy(colors.live);
        dot.material.opacity = Math.sin(t * Math.PI) * nextCode;
        dot.scale.setScalar(0.7 + Math.sin(t * Math.PI) * 0.5);
      });
    });

    const connecting = inSetup > 0.5 && step === 2;
    ripples.forEach((ripple, i) => {
      if (!connecting || mode !== "wifi") { ripple.material.opacity = 0; return; }
      const t = reduceMotion ? 0.5 : ((now / 1500) + i / 3) % 1;
      ripple.scale.setScalar(0.1 + t * 0.75);
      ripple.material.opacity = Math.min(1, (1 - t) * 1.4);
      ripple.material.color.copy(colors.live);
    });
    ethernet.visible = connecting && mode === "ethernet";
    pulse.visible = ethernet.visible && !reduceMotion;
    if (pulse.visible) pulse.position.copy(ethernetCurve.getPointAt((now / 1100) % 1));

    const flowing = (readyTarget === 1 && inSetup < 0.5) || (inSetup > 0.5 && step === 3);
    if (!reduceMotion && flowing && view.box < 0.5 && now - lastPacket > 420) {
      lastPacket = now;
      const from = homes[Math.floor(Math.random() * 6)];
      let to = homes[Math.floor(Math.random() * 6)];
      if (to === from) to = homes[(homes.indexOf(from) + 3) % 6];
      const dot = new THREE.Mesh(packetGeometry, mat.live);
      lan.add(dot);
      packets.push({ dot, from, to, born: now });
    }
    for (let p = packets.length - 1; p >= 0; p--) {
      const packet = packets[p];
      const t = (now - packet.born) / 1600;
      if (t >= 1) { lan.remove(packet.dot); packets.splice(p, 1); continue; }
      const leg = t < 0.5 ? t * 2 : (t - 0.5) * 2;
      const e = ease(leg);
      const r = t < 0.5 ? cableStart + (cableEnd - cableStart) * e : cableEnd + (cableStart - cableEnd) * e;
      const dir = t < 0.5 ? packet.from.dir : packet.to.dir;
      packet.dot.position.set(dir.x * r, 0.1, dir.z * r);
    }

    box.scale.setScalar(Math.max(0.001, view.box));
    box.rotation.y = -0.42 + (reduceMotion ? 0 : Math.sin(now / 2600) * 0.38);
    box.rotation.x = 0.08;
    box.position.y = 1.2 + (reduceMotion ? 0 : Math.sin(now / 900) * 0.06);
    lan.scale.setScalar(1 - 0.75 * view.box);
    lan.visible = view.box < 0.98;

    renderer.render(scene, camera);
    for (const listener of listeners) listener(view, smoothPos);
  }

  function project(i) {
    probe.copy(homes[i].top);
    homes[i].home.localToWorld(probe);
    probe.project(camera);
    return { x: (probe.x + 1) / 2 * width, y: (1 - probe.y) / 2 * height, visible: probe.z < 1 };
  }

  applyPalette();
  resize();
  window.addEventListener("resize", resize);
  requestAnimationFrame(frame);

  return {
    join(i) { homes[i].target = 1; },
    ready() { readyTarget = 1; },
    setTheme(isNight) { night = isNight; applyPalette(); },
    setZone(isDark) { if (dark !== isDark) { dark = isDark; applyPalette(); } },
    setView(pos) { viewPos = pos; },
    jumpView(pos) { viewPos = pos; smoothPos = pos; },
    setStep(i) { step = i; },
    setMode(m) { mode = m; },
    project,
    onFrame(listener) { listeners.push(listener); },
  };
}
