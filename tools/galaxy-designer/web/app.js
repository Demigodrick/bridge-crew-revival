'use strict';
// Galaxy Designer page: plain JS and canvas 2D. Edits the galaxy file's JSON object in place (so fields the page
// doesn't edit, like skybox and systemTags, survive a save) and talks to the tool's /api only.

const $ = id => document.getElementById(id);
const UNITS = ['Kilometers', 'Skybox', 'Lightseconds', 'AstronomicalUnits'];
// Kilometres per unit, to lay a system's nodes out on one plan. 'Skybox' has no real size; drawn as kilometres.
const UNIT_KM = { Kilometers: 1, Skybox: 1, Lightseconds: 299792.458, AstronomicalUnits: 149597870.7 };
// What a node can be for Patrol: the tag it gets and what Patrol does there.
const KINDS = [
  { tag: 'SpaceStation', label: 'Station', note: 'restock, rescues' },
  { tag: 'Asteroid', label: 'Asteroid field', note: '' },
  { tag: 'Nebula', label: 'Nebula', note: '' },
  { tag: 'Anomaly', label: 'Anomaly', note: 'scans' },
  { tag: 'Planet', label: 'Planet', note: '' },
  { tag: 'Moon', label: 'Moon', note: '' },
  { tag: 'ImpulseDestination', label: 'Impulse destination', note: '' },
];
const KIND_COLOR = { star: '#ffd66b', planet: '#6cb6ff', moon: '#a8b4c0', station: '#7ee787', asteroids: '#b08968', nebula: '#c586ff', other: '#d7dde4' };

let cat = null;            // the catalogue (raw JSON) or null
let gal = newGalaxy();     // the galaxy file (raw JSON object)
let fileName = '';
let dirty = false;
let view = 'galaxy';       // 'galaxy' | 'system'
let selSys = -1;           // index into gal.systems
let selNode = null;        // node id in the current system
let trenchImg = null;      // Image once loaded, 'none' if missing
let jumpHit = null;

function newGalaxy() { return { format: 1, name: '', page: { label: '', background: 'trench-darkened' }, systems: [] }; }

// ---------- helpers
function el(tag, props, ...kids) {
  const e = document.createElement(tag);
  if (props) for (const k in props) { if (k === 'class') e.className = props[k]; else if (k.startsWith('on')) e.addEventListener(k.slice(2), props[k]); else e[k] = props[k]; }
  for (const c of kids) e.append(c);
  return e;
}
function toast(msg, ms = 3500) { const t = $('toast'); t.textContent = msg; t.hidden = false; clearTimeout(toast.t); toast.t = setTimeout(() => t.hidden = true, ms); }
function setDirty(d = true) { dirty = d; $('dirtyMark').hidden = !d; }
const slug = s => (s || '').toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
const num = v => { const n = parseFloat(v); return Number.isFinite(n) ? n : 0; };
const sys = () => gal.systems[selSys];
const nodeById = (s, id) => (s.nodes || []).find(n => n.id === id);
function uniqueId(base, taken) { base = slug(base) || 'item'; let id = base, i = 2; while (taken.includes(id)) id = base + '-' + i++; return id; }
function setField(id, v) { const e = $(id); if (e && document.activeElement !== e) e.value = (Math.round(v * 1000) / 1000); }

// Free-text input without a catalogue, a select with one. getList: [{value,label,group}]
function picker(options, value, onChange, id) {
  if (!options || !options.length) {
    const i = el('input', { type: 'text', value: value || '', spellcheck: false });
    if (id) i.id = id; i.addEventListener('change', () => onChange(i.value)); return i;
  }
  const s = el('select'); if (id) s.id = id;
  let grp = null, gname = null, has = false;
  for (const o of options) {
    if (o.group !== gname) { gname = o.group; grp = gname ? el('optgroup', { label: gname }) : null; if (grp) s.append(grp); }
    const op = el('option', { value: o.value, textContent: o.label || o.value });
    if (o.value === value) has = true;
    (grp || s).append(op);
  }
  if (!has && value) s.prepend(el('option', { value, textContent: value + ' (not in the game)' }));
  s.value = value || options[0].value;
  s.addEventListener('change', () => onChange(s.value));
  return s;
}

async function api(path, opt) {
  const r = await fetch(path, opt);
  if (!r.ok) { let m = r.statusText; try { m = (await r.json()).error || m; } catch { } const e = new Error(m); e.status = r.status; throw e; }
  return r;
}

// ---------- universe mapping
function bounds() {
  if (cat && cat.universe && cat.universe.width > 0 && cat.universe.height > 0)
    return { minX: cat.universe.minX || 0, minZ: cat.universe.minZ || 0, w: cat.universe.width, h: cat.universe.height };
  const pts = (cat ? cat.trenchSystems || [] : []).map(s => s.position);
  for (const s of gal.systems) pts.push(s.position);
  if (!pts.length) return { minX: -500, minZ: -500, w: 1000, h: 1000 };
  let x0 = Infinity, x1 = -Infinity, z0 = Infinity, z1 = -Infinity;
  for (const p of pts) { x0 = Math.min(x0, p[0]); x1 = Math.max(x1, p[0]); z0 = Math.min(z0, p[1]); z1 = Math.max(z1, p[1]); }
  const w = Math.max(x1 - x0, 1), h = Math.max(z1 - z0, 1);
  return { minX: x0 - w * .1, minZ: z0 - h * .1, w: w * 1.2, h: h * 1.2 };
}
// The map rectangle on the canvas (fitted, keeping the universe's shape); z grows up the screen.
function mapRect() {
  const c = $('canvas'), b = bounds();
  const pad = 24, aw = c.width - pad * 2, ah = c.height - pad * 2;
  const k = Math.min(aw / b.w, ah / b.h);
  const w = b.w * k, h = b.h * k;
  return { x: (c.width - w) / 2, y: (c.height - h) / 2, w, h, b };
}
const toPx = (r, x, z) => [r.x + (x - r.b.minX) / r.b.w * r.w, r.y + r.h - (z - r.b.minZ) / r.b.h * r.h];
const fromPx = (r, px, py) => [r.b.minX + (px - r.x) / r.w * r.b.w, r.b.minZ + (r.y + r.h - py) / r.h * r.b.h];

// ---------- canvas
const canvas = $('canvas'), ctx = canvas.getContext('2d');
function resize() {
  const r = canvas.getBoundingClientRect(), d = window.devicePixelRatio || 1;
  canvas.width = Math.max(1, Math.round(r.width * d)); canvas.height = Math.max(1, Math.round(r.height * d));
  draw();
}
addEventListener('resize', resize);
const dpr = () => window.devicePixelRatio || 1;

function stars(w, h) {
  let s = 12345; const rnd = () => (s = (s * 1103515245 + 12345) & 0x7fffffff) / 0x7fffffff;
  ctx.fillStyle = '#05070b'; ctx.fillRect(0, 0, w, h);
  for (let i = 0; i < 220; i++) { ctx.fillStyle = `rgba(200,215,255,${0.15 + rnd() * .5})`; ctx.fillRect(rnd() * w, rnd() * h, 1.2, 1.2); }
}

function draw() {
  ctx.setTransform(1, 0, 0, 1, 0, 0);
  if (view === 'galaxy') drawGalaxy(); else drawSystem();
}

function drawGalaxy() {
  const w = canvas.width, h = canvas.height, d = dpr(), r = mapRect();
  stars(w, h);
  ctx.save();
  ctx.beginPath(); ctx.rect(r.x, r.y, r.w, r.h); ctx.clip();
  if (trenchImg && trenchImg !== 'none') { ctx.globalAlpha = 0.4; ctx.drawImage(trenchImg, r.x, r.y, r.w, r.h); ctx.globalAlpha = 1; }
  else { ctx.fillStyle = '#0b1220'; ctx.fillRect(r.x, r.y, r.w, r.h); }
  ctx.restore();
  ctx.strokeStyle = '#262d36'; ctx.lineWidth = 1; ctx.strokeRect(r.x, r.y, r.w, r.h);
  ctx.font = `${11 * d}px system-ui, sans-serif`; ctx.textBaseline = 'middle';
  if ($('showTrench').checked && cat) for (const s of cat.trenchSystems || []) {
    const [px, py] = toPx(r, s.position[0], s.position[1]);
    ctx.fillStyle = 'rgba(160,175,190,0.45)'; ctx.beginPath(); ctx.arc(px, py, 4 * d, 0, 7); ctx.fill();
    ctx.fillStyle = 'rgba(160,175,190,0.55)'; ctx.fillText(s.name, px + 8 * d, py);
  }
  gal.systems.forEach((s, i) => {
    const [px, py] = toPx(r, s.position[0], s.position[1]);
    const sel = i === selSys;
    ctx.fillStyle = sel ? '#f2c14e' : '#7ee787';
    ctx.beginPath(); ctx.arc(px, py, (sel ? 8 : 6) * d, 0, 7); ctx.fill();
    ctx.lineWidth = 2 * d; ctx.strokeStyle = '#000'; ctx.stroke();
    ctx.fillStyle = sel ? '#f2c14e' : '#e6edf3'; ctx.fillText(s.name, px + 11 * d, py);
  });
  $('help').textContent = 'Click empty space: add one system · drag: move · double-click: edit its nodes · Delete: remove the selected system · Ctrl+S: save';
}

// ----- system plan: world positions in km, centred and zoomed by `cam`
const cam = { x: 0, z: 0, k: 1e-3 };
function worldPos(s, n, depth = 0) {
  const u = UNIT_KM[n.unit] || 1;
  let x = n.position[0] * u, y = n.position[1] * u, z = n.position[2] * u;
  if (n.parent && depth < 50) { const p = nodeById(s, n.parent); if (p) { const [px, py, pz] = worldPos(s, p, depth + 1); x += px; y += py; z += pz; } }
  return [x, y, z];
}
function fitCam() {
  const s = sys(); if (!s) return;
  let m = 0; for (const n of s.nodes || []) { const p = worldPos(s, n); m = Math.max(m, Math.abs(p[0]), Math.abs(p[2])); }
  cam.x = 0; cam.z = 0; cam.k = 0.4 * Math.min(canvas.width, canvas.height) / Math.max(m, 1);
}
const toS = (x, z) => [canvas.width / 2 + (x - cam.x) * cam.k, canvas.height / 2 - (z - cam.z) * cam.k];
const fromS = (px, py) => [cam.x + (px - canvas.width / 2) / cam.k, cam.z - (py - canvas.height / 2) / cam.k];
function kindOf(n) { const p = cat && (cat.prefabs || []).find(p => p.name === n.prefab); return p ? p.kind || 'other' : 'other'; }
const nodeTags = n => n.tags || (n.tags = []);

function drawSystem() {
  const s = sys(), w = canvas.width, h = canvas.height, d = dpr();
  stars(w, h);
  if (!s) return;
  ctx.font = `${11 * d}px system-ui, sans-serif`; ctx.textBaseline = 'middle';
  const [ox, oy] = toS(0, 0);
  ctx.strokeStyle = '#1b2430'; ctx.lineWidth = 1; ctx.beginPath(); ctx.moveTo(0, oy); ctx.lineTo(w, oy); ctx.moveTo(ox, 0); ctx.lineTo(ox, h); ctx.stroke();
  const L = layout(s), items = (s.nodes || []).map(n => ({ n }));
  for (const { n } of items) {
    if (!n.parent) continue; const par = nodeById(s, n.parent); if (!par) continue;
    const a = L[par.id], b = L[n.id], rad = Math.hypot(b.x - a.x, b.y - a.y);
    ctx.strokeStyle = 'rgba(133,146,160,0.25)'; ctx.beginPath();
    if (!b.off) ctx.arc(a.x, a.y, rad, 0, 7);
    ctx.moveTo(a.x, a.y); ctx.lineTo(b.x, b.y); ctx.stroke();
  }
  jumpHit = null;
  for (const { n } of items) {
    const px = L[n.id].x, py = L[n.id].y, k = kindOf(n), sel = n.id === selNode;
    const big = k === 'star' ? 9 : k === 'planet' ? 6 : 4.5;
    ctx.fillStyle = KIND_COLOR[k] || KIND_COLOR.other;
    ctx.beginPath(); if (k === 'station') ctx.rect(px - big * d, py - big * d, big * 2 * d, big * 2 * d); else ctx.arc(px, py, big * d, 0, 7);
    ctx.fill(); ctx.lineWidth = (sel ? 3 : 1.5) * d; ctx.strokeStyle = sel ? '#f2c14e' : '#000'; ctx.stroke();
    if (isJump(n)) { ctx.strokeStyle = '#f2c14e'; ctx.lineWidth = d; ctx.beginPath(); ctx.arc(px, py, (big + 5) * d, 0, 7); ctx.stroke(); }
    ctx.fillStyle = sel ? '#f2c14e' : '#e6edf3'; ctx.fillText(n.name || n.id, px + (big + 5) * d, py);
  }
  $('help').textContent = 'Click: select a node · drag it: set X/Z (Y is a field; a node drawn stacked above its parent is too close to drag: use the fields) · drag empty space: pan · wheel: zoom · F: fit · Delete: remove the node';
}
// Where each node is drawn. A child that would land within ~12 px of its parent is stacked in a short column
// above it (purely visual; stored positions don't change), so dots and labels stay readable. off=true for those.
function layout(s) {
  const d = dpr(), pos = {}, count = {};
  const place = (n, depth) => {
    if (pos[n.id]) return pos[n.id];
    const w = worldPos(s, n), [x, y] = toS(w[0], w[2]);
    const par = n.parent && depth < 50 ? nodeById(s, n.parent) : null;
    if (par) {
      const pp = place(par, depth + 1), pw = worldPos(s, par), [qx, qy] = toS(pw[0], pw[2]);
      if (Math.hypot(x - qx, y - qy) < 12 * d) {
        const i = count[par.id] = (count[par.id] || 0) + 1;
        return pos[n.id] = { x: pp.x, y: pp.y - (8 + 15 * i) * d, off: true };
      }
    }
    return pos[n.id] = { x, y, off: false };
  };
  for (const n of s.nodes || []) place(n, 0);
  return pos;
}
function hitNode(px, py) {
  const s = sys(), d = dpr(); let best = null, bd = 14 * d;
  const L = layout(s);
  for (const n of s.nodes || []) { const dd = Math.hypot(L[n.id].x - px, L[n.id].y - py); if (dd <= bd) { bd = dd; best = n; } }
  return best;
}
function hitSystem(px, py) {
  const r = mapRect(), d = dpr(); let best = -1, bd = 12 * d;
  gal.systems.forEach((s, i) => { const [x, y] = toPx(r, s.position[0], s.position[1]); const dd = Math.hypot(x - px, y - py); if (dd <= bd) { bd = dd; best = i; } });
  return best;
}

// ---------- pointer
let drag = null;
canvas.addEventListener('pointerdown', e => {
  const rc = canvas.getBoundingClientRect(), d = dpr(), px = (e.clientX - rc.left) * d, py = (e.clientY - rc.top) * d;
  canvas.setPointerCapture(e.pointerId);
  if (view === 'galaxy') {
    const i = hitSystem(px, py);
    if (i >= 0) { selSys = i; drag = { kind: 'sys', moved: false }; refreshGalaxyPanel(); }
    else drag = { kind: 'empty', px, py, moved: false };
  } else {
    const n = hitNode(px, py);
    if (n) { selNode = n.id; drag = layout(sys())[n.id].off ? { kind: 'none' } : { kind: 'node', moved: false }; refreshSystemPanel(); }
    else drag = { kind: 'pan', px, py, cx: cam.x, cz: cam.z, moved: false };
  }
  draw();
});
canvas.addEventListener('pointermove', e => {
  const rc = canvas.getBoundingClientRect(), d = dpr(), px = (e.clientX - rc.left) * d, py = (e.clientY - rc.top) * d;
  if (view === 'galaxy') { const [x, z] = fromPx(mapRect(), px, py); $('coords').textContent = `X ${x.toFixed(1)}  Z ${z.toFixed(1)}`; }
  if (!drag) return;
  if (drag.kind === 'sys') {
    const [x, z] = fromPx(mapRect(), px, py), s = sys();
    s.position = [Math.round(x * 10) / 10, Math.round(z * 10) / 10]; drag.moved = true; setDirty();
    setField('sX', s.position[0]); setField('sZ', s.position[1]);
  } else if (drag.kind === 'node') {
    const s = sys(), n = nodeById(s, selNode), [wx, wz] = fromS(px, py);
    let ox = 0, oz = 0;
    if (n.parent) { const p = nodeById(s, n.parent); if (p) { const pp = worldPos(s, p); ox = pp[0]; oz = pp[2]; } }
    const u = UNIT_KM[n.unit] || 1;
    n.position[0] = (wx - ox) / u; n.position[2] = (wz - oz) / u; drag.moved = true; setDirty();
    setField('nX', n.position[0]); setField('nZ', n.position[2]);
  } else if (drag.kind === 'pan') {
    if (Math.hypot(px - drag.px, py - drag.py) > 3) drag.moved = true;
    cam.x = drag.cx - (px - drag.px) / cam.k; cam.z = drag.cz + (py - drag.py) / cam.k;
  } else if (drag.kind === 'empty' && Math.hypot(px - drag.px, py - drag.py) > 3) drag.moved = true;
  draw();
});
let emptyClick = null;
canvas.addEventListener('pointerup', e => {
  const d0 = drag; drag = null; emptyClick = d0 && d0.kind === 'empty' && !d0.moved && view === 'galaxy' ? d0 : null;
});
// Add on the first click only (detail 1): the second click of a double-click adds nothing.
canvas.addEventListener('click', e => { const c = emptyClick; emptyClick = null; if (c && e.detail === 1) addSystemAt(c.px, c.py); });
canvas.addEventListener('dblclick', e => { if (view === 'galaxy' && selSys >= 0 && Date.now() - addedAt > 600) { const rc = canvas.getBoundingClientRect(), d = dpr(); if (hitSystem((e.clientX - rc.left) * d, (e.clientY - rc.top) * d) >= 0) openSystem(selSys); } });
canvas.addEventListener('wheel', e => {
  if (view !== 'system') return; e.preventDefault();
  const rc = canvas.getBoundingClientRect(), d = dpr(), px = (e.clientX - rc.left) * d, py = (e.clientY - rc.top) * d;
  const [wx, wz] = fromS(px, py); cam.k *= e.deltaY < 0 ? 1.2 : 1 / 1.2;
  const [qx, qz] = fromS(px, py); cam.x += wx - qx; cam.z += wz - qz; draw();
}, { passive: false });

// ---------- galaxy view
function trenchTemplates() { return cat ? (cat.trenchSystems || []).map(s => ({ value: s.name })) : []; }

let addedAt = 0;   // a double-click on empty space adds one system and doesn't then open it
function addSystemAt(px, py) {
  const [x, z] = fromPx(mapRect(), px, py), r = mapRect();
  if (px < r.x || px > r.x + r.w || py < r.y || py > r.y + r.h) return;
  const ids = gal.systems.map(s => s.id);
  let n = gal.systems.length + 1, name = 'New system ' + n; while (gal.systems.some(s => s.name === name)) name = 'New system ' + (++n);
  const tpl = cat && cat.trenchSystems && cat.trenchSystems.length ? cat.trenchSystems[0].name : '';
  gal.systems.push({ id: uniqueId(name, ids), name, position: [Math.round(x * 10) / 10, Math.round(z * 10) / 10], template: tpl, skybox: null, systemTags: null, nodes: [] });
  addedAt = Date.now(); selSys = gal.systems.length - 1; setDirty(); refreshGalaxyPanel(true); draw();
  const nm = $('sName'); nm.focus(); nm.select();
}

function refreshGalaxyPanel(focusForm) {
  $('regName').value = gal.name || ''; $('pageLabel').value = (gal.page && gal.page.label) || ''; $('pageBg').value = (gal.page && gal.page.background) || '';
  const ul = $('sysList'); ul.textContent = '';
  gal.systems.forEach((s, i) => ul.append(el('li', { class: i === selSys ? 'sel' : '', onclick: () => { selSys = i; refreshGalaxyPanel(); draw(); }, ondblclick: () => openSystem(i) },
    s.name, el('small', null, `${(s.nodes || []).length} nodes`))));
  const s = sys();
  $('sysForm').hidden = !s;
  if (!s) return;
  $('sName').value = s.name; $('sId').value = s.id; $('sX').value = s.position[0]; $('sZ').value = s.position[1];
  const w = $('sTemplateWrap'); w.textContent = '';
  w.append(picker(trenchTemplates(), s.template, v => { s.template = v; setDirty(); }, 'sTemplate'));
}
$('regName').addEventListener('input', e => { gal.name = e.target.value; if (!gal.page) gal.page = {}; setDirty(); });
$('pageLabel').addEventListener('input', e => { gal.page = gal.page || {}; gal.page.label = e.target.value; setDirty(); });
$('pageBg').addEventListener('input', e => { gal.page = gal.page || {}; gal.page.background = e.target.value; setDirty(); });
$('showTrench').addEventListener('change', draw);
$('sName').addEventListener('input', e => { sys().name = e.target.value; setDirty(); draw(); });
$('sName').addEventListener('change', refreshGalaxyPanel);
for (const [id, i] of [['sX', 0], ['sZ', 1]]) $(id).addEventListener('input', e => { sys().position[i] = num(e.target.value); setDirty(); draw(); });
$('sDelete').addEventListener('click', deleteSystem);
$('sOpen').addEventListener('click', () => openSystem(selSys));
function deleteSystem() {
  const s = sys(); if (!s) return;
  if ((s.nodes || []).length && !confirm(`Delete ${s.name} and its ${s.nodes.length} nodes?`)) return;
  gal.systems.splice(selSys, 1); selSys = -1; setDirty(); refreshGalaxyPanel(); draw();
}

// ---------- system view
function openSystem(i) {
  selSys = i; selNode = null; view = 'system';
  $('galaxyPanel').hidden = true; $('systemPanel').hidden = false;
  $('sysTitle').textContent = sys().name; fitCam(); buildPrefabPicker(); refreshSystemPanel(); draw();
}
function backToGalaxy() { view = 'galaxy'; $('galaxyPanel').hidden = false; $('systemPanel').hidden = true; $('coords').textContent = ''; refreshGalaxyPanel(); draw(); }
$('backBtn').addEventListener('click', backToGalaxy);

let addPrefab = '';
function prefabOptions() {
  if (!cat) return [];
  const order = ['star', 'planet', 'moon', 'station', 'asteroids', 'nebula', 'other'];
  const ps = (cat.prefabs || []).slice().sort((a, b) => (order.indexOf(a.kind || 'other') - order.indexOf(b.kind || 'other')) || a.name.localeCompare(b.name));
  return ps.map(p => ({ value: p.name, group: p.kind || 'other' }));
}
function buildPrefabPicker() {
  const w = $('prefabWrap'); w.textContent = '';
  const opts = prefabOptions(); addPrefab = opts.length ? opts[0].value : '';
  const p = picker(opts, addPrefab, v => addPrefab = v, 'addPrefabSel'); if (!opts.length) p.placeholder = 'Prefab name'; w.append(p);
}
$('addNode').addEventListener('click', () => {
  const s = sys(); if (!s) return;
  const prefab = ($('addPrefabSel').value || '').trim();
  if (!prefab) return toast('Pick a prefab first');
  const parent = $('underSel').checked && selNode ? selNode : null;
  const base = prefab.replace(/^.*[\\/]/, '');
  const ids = (s.nodes || []).map(n => n.id);
  s.nodes = s.nodes || [];
  const n = { id: uniqueId(base, ids), parent, name: base, prefab, position: [0, 0, 0], unit: parent ? 'Kilometers' : 'AstronomicalUnits', rotation: [0, 0, 0], tags: [], landingRadius: 1000 };
  s.nodes.push(n); selNode = n.id; setDirty(); refreshSystemPanel(); draw();
});

function depthOrder(s) {
  const nodes = s.nodes || [], out = [], seen = new Set();
  const walk = (parent, depth) => { for (const n of nodes) if ((n.parent || null) === parent && !seen.has(n.id)) { seen.add(n.id); out.push({ n, depth }); walk(n.id, depth + 1); } };
  walk(null, 0);
  for (const n of nodes) if (!seen.has(n.id)) { seen.add(n.id); out.push({ n, depth: 0 }); walk(n.id, 1); }
  return out;
}
function isJump(n) { const t = cat && cat.spawnTags && cat.spawnTags[0]; return !!t && nodeTags(n).includes(t); }
function descendants(s, id) { const r = new Set([id]); let g = true; while (g) { g = false; for (const n of s.nodes) if (n.parent && r.has(n.parent) && !r.has(n.id)) { r.add(n.id); g = true; } } return r; }

function refreshSystemPanel() {
  const s = sys(); if (!s) return;
  $('sysTitle').textContent = s.name;
  const ul = $('nodeList'); ul.textContent = '';
  const order = depthOrder(s);
  if (!order.length) ul.append(el('li', { class: 'muted' }, 'No nodes yet: pick a prefab and Add node.'));
  for (const { n, depth } of order)
    ul.append(el('li', { class: n.id === selNode ? 'sel' : '', style: `padding-left:${14 + depth * 14}px`, onclick: () => { selNode = n.id; refreshSystemPanel(); draw(); } },
      (isJump(n) ? '◎ ' : '') + (n.name || n.id), el('small', null, n.prefab)));
  const n = selNode ? nodeById(s, selNode) : null;
  $('nodeForm').hidden = !n;
  if (!n) return;
  $('nName').value = n.name || ''; $('nId').value = n.id;
  const pw = $('nPrefabWrap'); pw.textContent = ''; pw.append(picker(prefabOptions(), n.prefab, v => { n.prefab = v; setDirty(); refreshSystemPanel(); draw(); }, 'nPrefab'));
  const par = $('nParent'); par.textContent = ''; par.append(el('option', { value: '', textContent: '(none)' }));
  const bad = descendants(s, n.id);
  for (const o of s.nodes) if (!bad.has(o.id)) par.append(el('option', { value: o.id, textContent: o.name || o.id }));
  par.value = n.parent || '';
  $('nX').value = n.position[0]; $('nY').value = n.position[1]; $('nZ').value = n.position[2];
  const u = $('nUnit'); u.textContent = ''; for (const x of UNITS) u.append(el('option', { value: x, textContent: x })); u.value = n.unit || 'Kilometers';
  n.rotation = n.rotation || [0, 0, 0];
  $('nRX').value = n.rotation[0]; $('nRY').value = n.rotation[1]; $('nRZ').value = n.rotation[2];
  $('nLand').value = n.landingRadius == null ? 1000 : n.landingRadius;
  refreshPoi(n);
}

function tagAllowed(t) { return !cat || (cat.tags || []).includes(t); }
function refreshPoi(n) {
  const spawn = cat && cat.spawnTags && cat.spawnTags[0];
  $('poiJump').disabled = !spawn; $('jumpTag').textContent = spawn ? `(tag ${spawn})` : '(needs the catalogue)';
  $('poiJump').checked = !!spawn && nodeTags(n).includes(spawn);
  const k = $('poiKinds'); k.textContent = '';
  for (const kd of KINDS) {
    const cb = el('input', { type: 'checkbox', checked: nodeTags(n).includes(kd.tag), disabled: !tagAllowed(kd.tag) });
    cb.addEventListener('change', () => toggleTag(n, kd.tag, cb.checked));
    k.append(el('label', { class: 'check', title: tagAllowed(kd.tag) ? '' : 'The catalogue has no tag ' + kd.tag }, cb, el('span', null, kd.label + ' ', el('small', null, kd.note ? '– ' + kd.note : ''))));
  }
  const chips = $('tagChips'); chips.textContent = '';
  for (const t of nodeTags(n)) chips.append(el('span', { class: 'chip' }, t, el('button', { type: 'button', title: 'Remove', textContent: '×', onclick: () => toggleTag(n, t, false) })));
  const dl = $('tagList'); dl.textContent = ''; for (const t of (cat && cat.tags) || []) dl.append(el('option', { value: t }));
  if (!cat) $('tagInput').removeAttribute('list'); else $('tagInput').setAttribute('list', 'tagList');
}
function toggleTag(n, t, on) {
  const tags = nodeTags(n), i = tags.indexOf(t);
  if (on && i < 0) tags.push(t); else if (!on && i >= 0) tags.splice(i, 1);
  setDirty(); refreshSystemPanel(); draw();
}
$('poiJump').addEventListener('change', e => { const n = nodeById(sys(), selNode), t = cat && cat.spawnTags[0]; if (n && t) toggleTag(n, t, e.target.checked); });
$('tagInput').addEventListener('keydown', e => {
  if (e.key !== 'Enter') return;
  const t = e.target.value.trim(), n = nodeById(sys(), selNode); if (!t || !n) return;
  if (!tagAllowed(t)) return toast(`No tag '${t}' in the game`);
  e.target.value = ''; toggleTag(n, t, true);
});
const nodeEdit = (id, f) => $(id).addEventListener('input', e => { const n = nodeById(sys(), selNode); if (!n) return; f(n, e.target.value); setDirty(); draw(); });
nodeEdit('nName', (n, v) => n.name = v); $('nName').addEventListener('change', refreshSystemPanel);
nodeEdit('nX', (n, v) => n.position[0] = num(v)); nodeEdit('nY', (n, v) => n.position[1] = num(v)); nodeEdit('nZ', (n, v) => n.position[2] = num(v));
nodeEdit('nRX', (n, v) => n.rotation[0] = num(v)); nodeEdit('nRY', (n, v) => n.rotation[1] = num(v)); nodeEdit('nRZ', (n, v) => n.rotation[2] = num(v));
nodeEdit('nLand', (n, v) => n.landingRadius = num(v));
$('nUnit').addEventListener('change', e => { const n = nodeById(sys(), selNode); n.unit = e.target.value; setDirty(); draw(); });
$('nParent').addEventListener('change', e => { const n = nodeById(sys(), selNode); n.parent = e.target.value || null; setDirty(); refreshSystemPanel(); draw(); });
$('nDelete').addEventListener('click', deleteNode);
function deleteNode() {
  const s = sys(), n = s && nodeById(s, selNode); if (!n) return;
  for (const o of s.nodes) if (o.parent === n.id) o.parent = n.parent || null;   // its children move up a level
  s.nodes.splice(s.nodes.indexOf(n), 1); selNode = null; setDirty(); refreshSystemPanel(); draw();
}

addEventListener('keydown', e => {
  const typing = /^(INPUT|SELECT|TEXTAREA)$/.test(document.activeElement.tagName);
  if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 's') { e.preventDefault(); save(); return; }
  if (typing) return;
  if (e.key === 'Delete' || e.key === 'Backspace') { view === 'galaxy' ? deleteSystem() : deleteNode(); }
  if ((e.key === 'f' || e.key === 'F') && view === 'system') { fitCam(); draw(); }
});

// ---------- files
const FILE_OK = /^[a-z0-9-]+$/;
function confirmLeave() { return !dirty || confirm('You have unsaved changes. Throw them away?'); }
$('btnNew').addEventListener('click', () => {
  if (!confirmLeave()) return;
  gal = newGalaxy(); fileName = ''; $('fileName').value = ''; selSys = -1; selNode = null; setDirty(false); closeProblems();
  if (view === 'system') backToGalaxy(); else { refreshGalaxyPanel(); draw(); }
});
$('btnOpen').addEventListener('click', async () => {
  const ul = $('openList'); if (!ul.hidden) { ul.hidden = true; return; }
  ul.textContent = ''; ul.hidden = false;
  try {
    const names = await (await api('/api/galaxies')).json();
    if (!names.length) ul.append(el('li', { class: 'muted' }, 'No galaxy files yet.'));
    for (const n of names) ul.append(el('li', { onclick: () => openFile(n) }, n));
  } catch (e) { ul.append(el('li', { class: 'muted' }, e.message)); }
});
async function openFile(name) {
  if (!confirmLeave()) return;
  try {
    const g = await (await api('/api/galaxy/' + encodeURIComponent(name))).json();
    g.systems = g.systems || []; for (const s of g.systems) { s.nodes = s.nodes || []; for (const n of s.nodes) { n.position = n.position || [0, 0, 0]; n.tags = n.tags || []; n.rotation = n.rotation || [0, 0, 0]; if (n.parent === undefined) n.parent = null; } }
    g.page = g.page || { label: g.name || '', background: 'trench-darkened' };
    gal = g; fileName = name; $('fileName').value = name; selSys = -1; selNode = null; setDirty(false); closeProblems(); $('openList').hidden = true;
    if (view === 'system') backToGalaxy(); else { refreshGalaxyPanel(); draw(); }
    toast('Opened ' + name);
  } catch (e) { toast(e.message, 6000); }
}
$('fileName').addEventListener('input', e => { fileName = e.target.value; });
const body = () => JSON.stringify(gal, null, 2);

$('btnCheck').addEventListener('click', check);
async function check() {
  try {
    const r = await (await api('/api/check', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: body() })).json();
    showProblems(r.problems || []); return r.problems || [];
  } catch (e) { toast(e.message, 6000); return null; }
}
function closeProblems() { $('problems').hidden = true; }
$('pClose').addEventListener('click', closeProblems);
function showProblems(ps) {
  $('problems').hidden = false; $('pTitle').textContent = ps.length ? `${ps.length} problem${ps.length > 1 ? 's' : ''}` : 'Check passed';
  const ul = $('pList'); ul.textContent = '';
  if (!ps.length) ul.append(el('li', { class: 'ok' }, 'No problems found.'));
  for (const p of ps) ul.append(el('li', { class: 'bad', onclick: () => gotoProblem(p) }, p));
}
function gotoProblem(text) {
  // Problems start with "<system id>/<node id>:" or "<system id>:" (or "a and b overlap").
  let best = -1, bestLen = 0;
  gal.systems.forEach((s, i) => { if ((text.startsWith(s.id + ':') || text.startsWith(s.id + '/') || text.startsWith(s.id + ' ')) && s.id.length > bestLen) { best = i; bestLen = s.id.length; } });
  if (best < 0) return;
  const s = gal.systems[best], m = text.match(/^[^/:]+\/([^:]+):/);
  const node = m && nodeById(s, m[1]);
  if (node) { openSystem(best); selNode = node.id; refreshSystemPanel(); draw(); }
  else { if (view === 'system') backToGalaxy(); selSys = best; refreshGalaxyPanel(); draw(); }
}

$('btnSave').addEventListener('click', save);
async function save() {
  let name = ($('fileName').value || '').trim() || slug(gal.name);
  if (!FILE_OK.test(name)) return toast('Give the file a name: lower-case letters, digits and dashes');
  if (name === 'catalogue') return toast("'catalogue' is the game's own file: pick another name");
  const ps = await check();
  if (ps && ps.length && !confirm(`The check found ${ps.length} problem${ps.length > 1 ? 's' : ''} (listed on the left). Save anyway?`)) return;
  try {
    await api('/api/galaxy/' + encodeURIComponent(name), { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: body() });
    fileName = name; $('fileName').value = name; setDirty(false); toast('Saved ' + name);
  } catch (e) { toast(e.message, 6000); }
}
addEventListener('beforeunload', e => { if (dirty) { e.preventDefault(); e.returnValue = ''; } });

// ---------- start
async function start() {
  new EventSource('/api/alive');     // the tool stops itself once no page holds this open
  fetch('/api/version').then(r => r.ok ? r.text() : '').then(v => { $('version').textContent = v ? 'v' + v : ''; document.title = `Galaxy Designer ${v}`; }).catch(() => { });
  try { cat = await (await api('/api/catalogue')).json(); }
  catch (e) { cat = null; const b = $('banner'); b.hidden = false; b.textContent = e.message; }
  const img = new Image();
  img.onload = () => { trenchImg = img; draw(); }; img.onerror = () => { trenchImg = 'none'; draw(); };
  img.src = '/api/trench-map.png';
  resize(); refreshGalaxyPanel();
}
start();
