// The bridge designer's page: loads a bridge (as .glb, made from the game's files by the program serving this page),
// lists its pieces, and lets you move, turn and hide pieces or whole groups. The changes are saved as the bridge's
// layout (BepInEx/bridges/<scene>.json in the game folder), which the mod puts on whenever that bridge loads.
//
// A change belongs to a path in the bridge's hierarchy (a piece, or a group of them: U picks the group) and moves
// everything under it, as the game's transforms would. Pieces inside a changed group follow it and can't be changed
// on their own (and the other way round), so the game and this page always agree on what moved.
//
// Moving: drag a picked piece itself across the floor (Shift: up and down), or the gizmo's arrows, or its ring to
// turn; arrows and Page Up/Down nudge, [ and ] turn; or type the values. Shift+click picks several to move together.
//
// Adding parts: a piece (or group) of any bridge, or a model (.obj) from BepInEx/bridges/models, placed where the
// view is looking. An added part is a piece with the path "+id": moved, turned and scaled like any other (Delete
// takes it away); the layout lists it under "added", and the mod builds it in the game.
//
// Coordinates: this page works in glTF's (right-handed); the file has the game's (Unity's, left-handed: z flipped,
// turns the other way).
import * as THREE from 'three';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { TransformControls } from 'three/addons/controls/TransformControls.js';
import { OBJLoader } from 'three/addons/loaders/OBJLoader.js';
import { MTLLoader } from 'three/addons/loaders/MTLLoader.js';

const $ = id => document.getElementById(id);
const canvas = $('canvas');
const renderer = new THREE.WebGLRenderer({ canvas, antialias: true });
renderer.setPixelRatio(devicePixelRatio);
const scene = new THREE.Scene();
scene.background = new THREE.Color(0x0e1116);
// The game's own light is baked into its bridges and isn't brought over: an even light to see by instead.
const hemi = new THREE.HemisphereLight(0xffffff, 0x3a4050, 1.5);
scene.add(hemi);
const key = new THREE.DirectionalLight(0xffffff, 1.1);
key.position.set(4, 12, 6);
scene.add(key);
const camera = new THREE.PerspectiveCamera(60, 1, 0.03, 3000);
const controls = new OrbitControls(camera, canvas);
controls.enableDamping = true;
controls.dampingFactor = 0.15;

let bridge = null;        // { level, scene, name }
let root = null;          // the loaded bridge
let pieces = [];          // { obj, path, name, backdrop, box (as the game has it), base (its matrix), row }
let edits = new Map();    // path -> { pivot, move (Vector3), turn (radians about up), hidden }
let added = new Map();    // id -> { id, from: { scene, level, path } | { model }, scale, piece }
let nextId = 1;
const extras = new THREE.Group(); // the added parts
// Stations (where players sit): { unit (the station's own group: moves only as a whole), seat (Vector3), chair (the
// path of a chair standing apart from it at its seat, the captain's: moving it moves the station), marker }.
let stations = [];
const seats = new THREE.Group(); // a marker at each station a chair takes with it
scene.add(extras);
scene.add(seats);
let dirty = false;
const undo = [], redo = [];

const outline = new THREE.Box3Helper(new THREE.Box3(), 0xf2c14e);
outline.visible = false;
scene.add(outline);

// The handle the gizmo drags: sits at the middle of what's picked.
const handle = new THREE.Object3D();
scene.add(handle);

// ------------------------------------------------------------------------------------------- bridges

function load(b) {
  bridge = b;
  if (root) { scene.remove(root); dispose(root); root = null; }
  select(null);
  pieces = [];
  edits = new Map();
  for (const a of added.values()) extras.remove(a.piece.obj);
  for (const s of stations) { seats.remove(s.marker); s.marker?.geometry.dispose(); }
  stations = [];
  for (const t of lightmapTextures.values()) t.dispose();
  for (const m of bakedMaterials.values()) m.dispose();
  lightmapTextures.clear();
  bakedMaterials.clear();
  added = new Map();
  nextId = 1;
  undo.length = redo.length = 0;
  setDirty(false);
  $('list').replaceChildren();
  $('loading').hidden = false;
  const bar = $('loading').querySelector('.bar div');
  bar.style.width = '0';
  $('stats').textContent = 'Reading the bridge from the game…';
  const t0 = performance.now();
  new GLTFLoader().load(`/api/bridge/level${b.level}.glb`, async gltf => {
    root = gltf.scene;
    scene.add(root);
    root.updateMatrixWorld(true);
    // Every piece is a node with its path in the scene (the loader keeps glTF extras as userData).
    root.traverse(o => {
      if (!o.userData.path) return;
      const box = new THREE.Box3().setFromObject(o);
      const path = o.userData.path;
      o.matrixAutoUpdate = false;
      pieces.push({ obj: o, path, name: path.split('/').slice(-2).join(' / '), backdrop: box.getSize(new THREE.Vector3()).length() > 50, box, base: o.matrix.clone(), station: o.userData.station });
    });
    findStations(gltf.scene.userData.stations ?? []);
    pieces.sort((a, b) => a.path.localeCompare(b.path));
    buildList();
    await loadLayout();
    applyAll();
    if ($('baked').checked) setBaked(true);
    frame(pieces.filter(p => !p.backdrop));
    const tris = countTriangles(root);
    $('stats').dataset.base = `${pieces.length} pieces · ${(tris / 1000).toFixed(0)}k triangles · ${((performance.now() - t0) / 1000).toFixed(1)} s`;
    showStats();
    $('loading').hidden = true;
  }, e => { if (e.total) bar.style.width = `${(100 * e.loaded / e.total).toFixed(0)}%`; },
  err => { $('stats').textContent = 'Couldn\'t load it: ' + err.message; $('loading').hidden = true; });
}

function dispose(o) {
  o.traverse(m => {
    if (!m.isMesh) return;
    m.geometry.dispose();
    for (const mat of [m.material].flat()) { for (const v of Object.values(mat)) if (v?.isTexture) v.dispose(); mat.dispose(); }
  });
}

function countTriangles(o) {
  let n = 0;
  o.traverse(m => { if (m.isMesh) n += (m.geometry.index ? m.geometry.index.count : m.geometry.attributes.position.count) / 3; });
  return n;
}

// ------------------------------------------------------------------------------------------- stations
// The chair at a station's seat that isn't part of the station (the captain's stands apart from its station, which
// holds the captain's consoles and where the captain sits): a piece named as a chair, within 30 cm of the seat.
function findStations(list) {
  for (const s of list) {
    const seat = new THREE.Vector3(...s.seat);
    let chair = null, best = 0.3;
    for (const p of pieces) {
      if (p.station || !/chair/i.test(p.path) || p.box.min.y > seat.y + 0.5 || p.box.max.y < seat.y - 1.2) continue;
      const dx = Math.max(0, p.box.min.x - seat.x, seat.x - p.box.max.x), dz = Math.max(0, p.box.min.z - seat.z, seat.z - p.box.max.z);
      const d = Math.hypot(dx, dz);
      if (d < best || (d === best && chair && p.path.length < chair.length)) { best = d; chair = p.path; }
    }
    const st = { unit: s.unit, seat, chair };
    if (chair) {
      // A ring on the floor where the station's player sits (and its consoles are): it goes where the chair goes.
      st.marker = new THREE.Mesh(new THREE.RingGeometry(0.28, 0.34, 40).rotateX(-Math.PI / 2),
        new THREE.MeshBasicMaterial({ color: 0x4fc3f7, transparent: true, opacity: 0.8, depthTest: false }));
      st.marker.renderOrder = 10;
      seats.add(st.marker);
    }
    stations.push(st);
  }
}
const stationOf = path => stations.find(s => s.unit === path);
const chairStation = path => stations.find(s => s.chair && under(s.chair, path));

// Where each chair's station is now: moved with the change its chair is in.
function placeSeats() {
  for (const s of stations) {
    if (!s.marker) continue;
    const o = ownerOf(s.chair);
    const at = s.seat.clone();
    if (o) at.sub(o.e.pivot).applyAxisAngle(new THREE.Vector3(0, 1, 0), o.e.turn).add(o.e.pivot).add(o.e.move);
    s.marker.position.set(at.x, s.seat.y - 0.45, at.z);
  }
}

// ------------------------------------------------------------------------------------------- changes
const under = (path, of) => path === of || path.startsWith(of + '/');
const members = path => pieces.filter(p => under(p.path, path));

/** The change a piece is moved by: its own or a group's it's in. */
function ownerOf(path) {
  let best = null;
  for (const [k, e] of edits) if (under(path, k) && (!best || k.length > best.k.length)) best = { k, e };
  return best;
}

/** Why a path can't be changed (a group it's in, or something in it, is changed already), or null. */
function blocked(path) {
  for (const k of edits.keys()) {
    if (k === path) continue;
    if (under(path, k)) return `It's in ${short(k)}, which is changed: change that, or put it back first.`;
    if (under(k, path)) return `${short(k)} in it is changed: put that back first.`;
  }
  return null;
}
const short = path => path.split('/').pop();
const pieceAt = path => pieces.find(p => p.path === path);

function middle(path) {
  const box = new THREE.Box3();
  for (const p of members(path)) box.union(p.box);
  return box.isEmpty() ? new THREE.Vector3() : box.getCenter(new THREE.Vector3());
}

const tmp = { a: new THREE.Matrix4(), b: new THREE.Matrix4(), c: new THREE.Matrix4() };
function applyPiece(p) {
  const o = ownerOf(p.path);
  if (o) {
    const { pivot, move, turn } = o.e;
    // Turned about up around the middle, then moved.
    tmp.a.makeTranslation(-pivot.x, -pivot.y, -pivot.z);
    tmp.b.makeRotationY(turn);
    if (p.added) tmp.b.multiply(new THREE.Matrix4().makeScale(p.added.scale, p.added.scale, p.added.scale));
    tmp.b.multiply(tmp.a);
    tmp.c.makeTranslation(pivot.x + move.x, pivot.y + move.y, pivot.z + move.z).multiply(tmp.b);
    p.obj.matrix.multiplyMatrices(tmp.c, p.base);
  } else p.obj.matrix.copy(p.base);
  p.obj.updateMatrixWorld(true);
  const hidden = !!o?.e.hidden;
  p.obj.visible = !hidden && (!p.backdrop || $('backdrops').checked);
  if (p.row) {
    p.row.querySelector('input').checked = !hidden;
    p.row.classList.toggle('off', hidden);
    const tag = p.row.querySelector('.tag');
    tag.textContent = p.added ? 'added' : hidden ? 'hidden' : o ? (o.k === p.path ? 'changed' : 'in ' + short(o.k)) : p.backdrop ? 'backdrop' : '';
    tag.classList.toggle('edit', !!o);
  }
}
const applyAll = () => { for (const p of pieces) applyPiece(p); placeSeats(); refreshSelection(); };
const applyUnder = (path, refresh = true) => { for (const p of members(path)) applyPiece(p); placeSeats(); if (refresh) refreshSelection(); };

function editFor(path) {
  let e = edits.get(path);
  if (!e) edits.set(path, e = { pivot: middle(path), move: new THREE.Vector3(), turn: 0, hidden: false });
  return e;
}

function tidy(path) {
  if (path.startsWith('+')) return; // an added part's change is where it is: it stays
  const e = edits.get(path);
  if (e && !e.hidden && e.move.lengthSq() < 1e-10 && Math.abs(e.turn) < 1e-6) edits.delete(path);
}

function changed() { setDirty(true); showStats(); }

function setDirty(d) {
  dirty = d;
  $('save').disabled = !d;
  showStats();
}

function showStats() {
  const base = $('stats').dataset.base ?? '';
  $('stats').textContent = `${base}${base ? ' · ' : ''}${edits.size} change${edits.size === 1 ? '' : 's'}${dirty ? ' · unsaved' : ''}`;
}

// Undo and redo: the whole set of changes, before each one.
const snapshot = () => JSON.stringify({
  edits: [...edits].map(([k, e]) => [k, e.pivot.toArray(), e.move.toArray(), e.turn, e.hidden]),
  added: [...added.values()].map(a => [a.id, a.from, a.scale]),
});
async function restore(s) {
  const snap = JSON.parse(s);
  edits = new Map(snap.edits.map(([k, p, m, t, h]) => [k, { pivot: new THREE.Vector3(...p), move: new THREE.Vector3(...m), turn: t, hidden: h }]));
  // Added parts: the ones gone go, the ones back are made again.
  const want = new Map(snap.added.map(([id, from, scale]) => [id, { from, scale }]));
  for (const id of [...added.keys()]) if (!want.has(id)) dropAdded(id);
  for (const [id, w] of want) {
    if (added.has(id)) added.get(id).scale = w.scale;
    else await makeAdded(id, w.from, w.scale);
  }
  select(null);
  applyAll();
  changed();
}
function remember() { undo.push(snapshot()); redo.length = 0; if (undo.length > 200) undo.shift(); }
function doUndo() { if (undo.length) { redo.push(snapshot()); restore(undo.pop()); } }
function doRedo() { if (redo.length) { undo.push(snapshot()); restore(redo.pop()); } }

function toggleHidden(path) {
  if (path.startsWith('+')) { say('An added part is taken away with Delete, not hidden.'); return; }
  if (stationOf(path)) { say('A station can be moved, not hidden: players sit there.'); return; }
  const why = blocked(path);
  if (why) { say(why); return; }
  remember();
  const e = editFor(path);
  e.hidden = !e.hidden;
  tidy(path);
  applyUnder(path);
  changed();
}

function putBack(path) {
  if (path.startsWith('+')) { remember(); dropAdded(path.slice(1)); edits.delete(path); applyAll(); changed(); say('Taken away.'); return; }
  if (!edits.has(path)) return;
  remember();
  edits.delete(path);
  applyUnder(path);
  changed();
  say('Put back as the game has it.');
}

// ------------------------------------------------------------------------------------------- the file
// Unity's space: z flipped, and so turns the other way.
const toGame = v => [r4(v.x), r4(v.y), r4(-v.z)];
const fromGame = a => new THREE.Vector3(a[0], a[1], -a[2]);
const r4 = x => Math.round(x * 10000) / 10000 + 0;

async function loadLayout() {
  const res = await fetch(`/api/layout/${bridge.scene}`);
  if (!res.ok) return;
  const layout = await res.json();
  for (const c of layout.pieces ?? []) {
    // A chair's station: written from its chair's change, not one of its own.
    if (stations.some(s => s.chair && s.unit === c.path)) continue;
    edits.set(c.path, { pivot: fromGame(c.pivot), move: fromGame(c.move), turn: -c.turn * Math.PI / 180, hidden: !!c.hidden });
  }
  for (const a of layout.added ?? []) {
    const from = a.model ? { model: a.model } : { scene: a.scene, level: a.level, path: a.path };
    try { await makeAdded(a.id, from, a.scale ?? 1); } catch (err) { say(`Couldn't add ${a.model ?? a.path}: ${err.message}`); continue; }
    edits.set('+' + a.id, { pivot: fromGame(a.pivot), move: fromGame(a.move), turn: -a.turn * Math.PI / 180, hidden: false });
    nextId = Math.max(nextId, (parseInt(a.id) || 0) + 1);
  }
  say(`Layout loaded: ${edits.size} change${edits.size === 1 ? '' : 's'}${added.size ? `, ${added.size} added` : ''}.`);
}

async function save() {
  for (const k of [...edits.keys()]) tidy(k);
  const layout = {
    format: 1,
    scene: bridge.scene,
    pieces: [...edits].filter(([path]) => !path.startsWith('+'))
      .map(([path, e]) => ({ path, pivot: toGame(e.pivot), move: toGame(e.move), turn: r4(-e.turn * 180 / Math.PI), hidden: e.hidden }))
      // A chair's station goes with it, moved the same (where its player sits and its consoles stand).
      .concat(stations.filter(s => s.chair).map(s => [s, ownerOf(s.chair)]).filter(([, o]) => o && (o.e.move.lengthSq() > 1e-10 || Math.abs(o.e.turn) > 1e-6))
        .map(([s, { e }]) => ({ path: s.unit, pivot: toGame(e.pivot), move: toGame(e.move), turn: r4(-e.turn * 180 / Math.PI), hidden: false }))),
    added: [...added.values()].map(a => {
      const e = edits.get('+' + a.id);
      return { id: a.id, ...a.from, pivot: toGame(e.pivot), move: toGame(e.move), turn: r4(-e.turn * 180 / Math.PI), scale: r4(a.scale) };
    }),
  };
  const res = await fetch(`/api/layout/${bridge.scene}`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(layout, null, 2) });
  const text = await res.text();
  if (!res.ok) { say('Not saved: ' + text); return; }
  setDirty(false);
  say('Saved to ' + text);
}
$('save').addEventListener('click', save);
addEventListener('beforeunload', e => { if (dirty) { e.preventDefault(); e.returnValue = ''; } });

// ------------------------------------------------------------------------------------------- the list
function buildList() {
  const frag = document.createDocumentFragment();
  for (const p of pieces) frag.append(makeRow(p));
  $('list').append(frag);
  filter();
}
function addRow(p) { $('list').prepend(makeRow(p)); }
function makeRow(p) {
  {
    const li = document.createElement('li');
    li.title = p.path;
    const box = document.createElement('input');
    box.type = 'checkbox';
    box.setAttribute('aria-label', 'Shown');
    const name = document.createElement('span');
    name.className = 'name';
    name.textContent = p.name;
    const tag = document.createElement('span');
    tag.className = 'tag';
    li.append(box, name, tag);
    box.addEventListener('click', e => { e.stopPropagation(); e.preventDefault(); toggleHidden(ownerOf(p.path)?.e.hidden ? ownerOf(p.path).k : p.path); });
    li.addEventListener('click', e => select(p.path, false, e.shiftKey));
    li.addEventListener('dblclick', () => focus(p.path));
    p.row = li;
    return li;
  }
}

// Effects and helpers (debris, sparks, viewscreen reference points, UI): drawn in the game, but rarely what you're
// after; listed only with Helpers ticked.
const helper = /\/FX\/|Particle|Debris|Spark|Burst|HACK|ReferencePoint|Canvas|GUI|WaveForm|Hologram|Highlight/i;
function filter() {
  const q = $('search').value.trim().toLowerCase();
  const helpers = $('helpers').checked;
  for (const p of pieces) p.row.hidden = (q !== '' && !p.path.toLowerCase().includes(q)) || (!helpers && helper.test(p.path) && !edits.has(p.path));
}
$('search').addEventListener('input', filter);
$('helpers').addEventListener('change', filter);
$('backdrops').addEventListener('change', applyAll);
$('frame').addEventListener('click', () => frame(pieces.filter(p => p.obj.visible)));
$('resetAll').addEventListener('click', () => {
  if (!edits.size || !confirm(`Put all ${edits.size} changes back as the game has them?`)) return;
  remember();
  edits.clear();
  applyAll();
  changed();
});

// ------------------------------------------------------------------------------------------- selection
// What's picked: one or more paths (Shift+click adds or takes away), the last one picked first.
let sel = [];

function select(path, scroll = true, add = false) {
  // Inside a changed group: the group.
  if (path) { const o = ownerOf(path); if (o && o.k !== path && under(path, o.k)) path = o.k; }
  // A part of a station: the station (it moves only as a whole).
  if (path) { const unit = pieceAt(path)?.station; if (unit && path !== unit && under(path, unit)) path = unit; }
  if (add && path) sel = sel.includes(path) ? sel.filter(x => x !== path) : [path, ...sel.filter(x => !under(x, path) && !under(path, x))];
  else sel = path ? [path] : [];
  for (const p of pieces) p.row?.classList.toggle('sel', sel.some(s => under(p.path, s)));
  $('info').hidden = sel.length === 0;
  if (!sel.length) { attach(false); outline.visible = false; return; }
  const first = members(sel[0])[0];
  if (scroll && first?.row) first.row.scrollIntoView({ block: 'nearest' });
  refreshSelection();
}
const selPath = () => sel[0] ?? null;
const selMembers = () => pieces.filter(p => sel.some(s => under(p.path, s)));

function selBox() {
  const box = new THREE.Box3();
  for (const p of selMembers()) box.union(new THREE.Box3().setFromObject(p.obj));
  return box;
}

function refreshSelection() {
  if (!sel.length) return;
  const list = selMembers();
  const box = selBox();
  outline.box.copy(box);
  outline.visible = !box.isEmpty();
  const size = box.getSize(new THREE.Vector3());
  const mats = new Set();
  for (const p of list) p.obj.traverse(m => { if (m.isMesh) for (const mat of [m.material].flat()) mats.add(mat.name || '(unnamed)'); });
  const path = sel[0];
  const one = sel.length === 1 && list.length === 1 && list[0].path === path ? list[0] : null;
  $('infoName').textContent = sel.length > 1 ? `${sel.length} picked` : short(path) + (one ? '' : ` (group of ${list.length})`);
  $('infoPath').textContent = sel.length > 1 ? sel.map(x => x.split('/').slice(-2).join('/')).join(', ') : path;
  $('infoMesh').textContent = one?.added ? (one.added.from.model ? `model ${one.added.from.model}` : `${short(one.added.from.path)} from ${bridges.find(b => b.level === one.added.from.level)?.name ?? one.added.from.scene}`)
    : one ? `${one.obj.userData.mesh ?? '?'} (${one.obj.userData.file ?? '?'})` : '—';
  $('infoSize').textContent = `${size.x.toFixed(2)} × ${size.y.toFixed(2)} × ${size.z.toFixed(2)} m`;
  $('infoMats').textContent = [...mats].slice(0, 8).join(', ') + (mats.size > 8 ? ` +${mats.size - 8}` : '');
  $('infoBatched').textContent = list.some(p => p.obj.userData.batched) ? 'yes: the game copies it to move it' : 'no';
  const why = sel.map(blocked).find(x => x);
  const hidden = sel.some(s => edits.get(s)?.hidden);
  const takes = sel.map(chairStation).find(x => x);
  $('infoChange').textContent = why ?? (hidden ? 'hidden' : stationOf(path) ? 'a station: its console and its player go with it'
    : takes ? `the ${short(takes.unit).replace(/_?root$/i, '').toLowerCase()} station (its console and seat, the blue ring) goes with it` : '');
  // Typed values: for one pick, its move (metres) and turn (degrees).
  const e = edits.get(path);
  const typing = sel.length === 1 && !why && !hidden;
  $('fields').hidden = !typing;
  if (typing && document.activeElement?.closest('#fields') == null) {
    const m = e?.move ?? new THREE.Vector3();
    $('fx').value = m.x.toFixed(2); $('fy').value = m.y.toFixed(2); $('fz').value = m.z.toFixed(2);
    $('fturn').value = (e ? e.turn * 180 / Math.PI : 0).toFixed(0);
    if (one?.added) $('fscale').value = one.added.scale.toFixed(2);
  }
  $('fscaleWrap').hidden = !one?.added;
  // The gizmo: on what can be changed and isn't hidden, at its middle as it is now.
  if (why || hidden) { attach(false); return; }
  if (dragging) return;
  handle.position.copy(box.getCenter(new THREE.Vector3()));
  handle.rotation.set(0, sel.length === 1 ? (e?.turn ?? 0) : 0, 0);
  handle.updateMatrixWorld();
  attach(true);
}

// ------------------------------------------------------------------------------------------- moving
// One gizmo for both: arrows to move along an axis (the squares: in a plane), the ring around it to turn.
const gizmoMove = new TransformControls(camera, canvas);
gizmoMove.setMode('translate');
gizmoMove.setSize(0.75);
scene.add(gizmoMove);
const gizmoTurn = new TransformControls(camera, canvas);
gizmoTurn.setMode('rotate');
gizmoTurn.showX = gizmoTurn.showZ = gizmoTurn.showE = false; // turning only about up: bridges stay level
gizmoTurn.setSize(1.5);
scene.add(gizmoTurn);
const gizmos = [gizmoMove, gizmoTurn];
function attach(on) { for (const g of gizmos) { if (on && g.object !== handle) g.attach(handle); if (!on) g.detach(); } }

function setSnap(on) {
  gizmoMove.setTranslationSnap(on ? 0.05 : null);
  gizmoTurn.setRotationSnap(on ? THREE.MathUtils.degToRad(15) : null);
}
$('snap').addEventListener('change', () => setSnap($('snap').checked));
setSnap($('snap').checked);

// A move or turn of everything picked starts from how it was (start) and puts the change on top: each change keeps
// its own pivot (its middle, which the game uses to find it), so turning several together around their common
// middle C becomes, for each, move' = C + R(θ)(pivot + move - C) - pivot + d, turn' = turn + θ.
let start = null, dragging = false;
function begin() {
  if (!sel.length || sel.some(blocked)) return false;
  remember();
  const center = selBox().getCenter(new THREE.Vector3());
  start = { center, handle: handle.position.clone(), turn: handle.rotation.y,
    items: sel.map(path => { const e = editFor(path); return { path, e, move: e.move.clone(), turn: e.turn }; }) };
  return true;
}
const rot = new THREE.Matrix4();
function shift(d, theta) {
  if (!start) return;
  rot.makeRotationY(theta);
  for (const it of start.items) {
    const v = it.e.pivot.clone().add(it.move).sub(start.center).applyMatrix4(rot).add(start.center).sub(it.e.pivot).add(d);
    it.e.move.copy(v);
    it.e.turn = it.turn + theta;
  }
  for (const it of start.items) applyUnder(it.path, false);
  refreshSelection();
  changed();
}
function end() {
  if (!start) return;
  for (const it of start.items) tidy(it.path);
  start = null;
  refreshSelection();
}

for (const g of gizmos) {
  g.addEventListener('mouseDown', () => { if (!begin()) g.detach(); });
  g.addEventListener('dragging-changed', e => {
    dragging = e.value;
    controls.enabled = !e.value;
    if (!e.value) { lastDrag = performance.now(); end(); }
  });
  g.addEventListener('objectChange', () => {
    if (!start) return;
    shift(handle.position.clone().sub(start.handle), handle.rotation.y - start.turn);
  });
}
let lastDrag = 0;

// Dragging a picked piece itself: across the floor (with Shift: up and down), from where it was grabbed.
let grab = null;
const floorPlane = new THREE.Plane(), hitPoint = new THREE.Vector3();
function pointerRay(e) {
  const r = canvas.getBoundingClientRect();
  ray.setFromCamera(new THREE.Vector2(((e.clientX - r.left) / r.width) * 2 - 1, -((e.clientY - r.top) / r.height) * 2 + 1), camera);
}
function startGrab(e, hit) {
  if (!begin()) return false;
  const vertical = e.shiftKey;
  if (vertical) {
    // A plane through the grab point facing the camera, upright.
    const n = camera.getWorldDirection(new THREE.Vector3()).setY(0).normalize().negate();
    floorPlane.setFromNormalAndCoplanarPoint(n, hit.point);
  } else floorPlane.setFromNormalAndCoplanarPoint(new THREE.Vector3(0, 1, 0), hit.point);
  grab = { from: hit.point.clone(), vertical, id: e.pointerId };
  dragging = true;
  controls.enabled = false;
  canvas.setPointerCapture(e.pointerId);
  return true;
}
function moveGrab(e) {
  pointerRay(e);
  if (!ray.ray.intersectPlane(floorPlane, hitPoint)) return;
  const d = hitPoint.clone().sub(grab.from);
  if (grab.vertical) d.set(0, d.y, 0); else d.y = 0;
  if ($('snap').checked) d.set(Math.round(d.x / 0.05) * 0.05, Math.round(d.y / 0.05) * 0.05, Math.round(d.z / 0.05) * 0.05);
  shift(d, 0);
}
function endGrab(e) {
  canvas.releasePointerCapture(grab.id);
  grab = null;
  dragging = false;
  controls.enabled = true;
  lastDrag = performance.now();
  end();
}

// Nudging with the keyboard: arrows move it (5 cm, Shift 1 cm; Page Up/Down up and down) the way you look,
// [ and ] turn it (15°, Shift 1°).
function nudge(d, theta) {
  if (!sel.length || !begin()) return;
  shift(d, theta);
  end();
}
function viewAxes() {
  const f = camera.getWorldDirection(new THREE.Vector3()).setY(0).normalize();
  // The nearest of the floor's axes, so arrows go straight along the bridge's lines.
  const fwd = Math.abs(f.x) > Math.abs(f.z) ? new THREE.Vector3(Math.sign(f.x), 0, 0) : new THREE.Vector3(0, 0, Math.sign(f.z));
  return { fwd, right: new THREE.Vector3(-fwd.z, 0, fwd.x) };
}

// Typed values (one pick): its move from where the game has it, and its turn.
for (const id of ['fx', 'fy', 'fz', 'fturn', 'fscale'])
  $(id).addEventListener('change', () => {
    const path = selPath();
    if (!path || blocked(path)) return;
    remember();
    const e = editFor(path);
    const n = id => { const v = parseFloat($(id).value); return isFinite(v) ? v : 0; };
    e.move.set(n('fx'), n('fy'), n('fz'));
    e.turn = n('fturn') * Math.PI / 180;
    const a = pieceAt(path)?.added;
    if (a && id === 'fscale') a.scale = Math.min(20, Math.max(0.05, n('fscale') || 1));
    tidy(path);
    applyUnder(path);
    changed();
  });

$('undo').addEventListener('click', doUndo);
$('group').addEventListener('click', group);

function group() {
  const path = selPath();
  if (!path) return;
  const parts = path.split('/');
  // Not the whole bridge (its top object, or the scene's roots).
  if (parts.length <= 3) { say('That would be the whole bridge.'); return; }
  const up = parts.slice(0, -1).join('/');
  if (members(up).some(p => p.station)) { say('A station moves only on its own, as a whole.'); return; }
  select(up);
}

const pieceOf = o => { for (; o; o = o.parent) { const p = pieces.find(x => x.obj === o); if (p) return p; } return null; };
const ray = new THREE.Raycaster();
function hitAt(e) {
  pointerRay(e);
  return root ? ray.intersectObjects([root, extras], true).find(h => visibleUp(h.object)) : null;
}
const visibleUp = o => { for (; o; o = o.parent) if (!o.visible) return false; return true; };
const onGizmo = () => gizmos.some(g => g.object && g.axis !== null);

// Pressing on a picked piece and dragging moves it; a click (no drag) picks (Shift: adds); a drag elsewhere turns
// the view; a double-click turns the view around that point.
let down = null;
canvas.addEventListener('pointerdown', e => {
  down = { x: e.clientX, y: e.clientY, gizmo: onGizmo() };
  if (e.button !== 0 || down.gizmo || !sel.length) return;
  const hit = hitAt(e);
  const p = hit && pieceOf(hit.object);
  if (p && sel.some(s => under(p.path, s))) { down.armed = { e, hit }; controls.enabled = false; }
});
canvas.addEventListener('pointermove', e => {
  if (grab) { moveGrab(e); return; }
  if (down?.armed && Math.hypot(e.clientX - down.x, e.clientY - down.y) > 4) {
    if (!startGrab(down.armed.e, down.armed.hit)) { down.armed = null; controls.enabled = true; }
  }
});
canvas.addEventListener('pointerup', e => {
  if (grab) { endGrab(e); down = null; return; }
  if (down?.armed) controls.enabled = true;
  if (!down || down.gizmo || e.button !== 0 || performance.now() - lastDrag < 150 || Math.hypot(e.clientX - down.x, e.clientY - down.y) > 4) { down = null; return; }
  down = null;
  const hit = hitAt(e);
  select(hit ? pieceOf(hit.object)?.path ?? null : null, true, e.shiftKey);
});
canvas.addEventListener('dblclick', e => {
  const hit = hitAt(e);
  if (hit) { controls.target.copy(hit.point); controls.update(); }
});

let sayTimer = 0;
function say(text) {
  $('toast').textContent = text;
  $('toast').hidden = false;
  clearTimeout(sayTimer);
  sayTimer = setTimeout(() => { $('toast').hidden = true; }, 4000);
}

// ------------------------------------------------------------------------------------------- adding parts
// Sources: the bridges (each read once, kept) and the models folder.
const sourceCache = new Map(); // level -> Promise of its scene
function sourceScene(level) {
  if (!sourceCache.has(level))
    sourceCache.set(level, new Promise((ok, fail) => new GLTFLoader().load(`/api/bridge/level${level}.glb`, g => { g.scene.updateMatrixWorld(true); ok(g.scene); }, undefined, fail)));
  return sourceCache.get(level);
}
let partPick = null; // { from, label }

async function fillParts() {
  const src = $('source').value;
  $('parts').replaceChildren();
  partPick = null;
  $('addPart').disabled = $('addGroup').disabled = true;
  const rows = [];
  if (src === 'models') {
    const models = await (await fetch('/api/models')).json();
    if (!models.length) { const li = document.createElement('li'); li.className = 'muted'; li.textContent = 'No models yet: put .obj files (with their .mtl and textures) in BepInEx/bridges/models.'; $('parts').append(li); return; }
    for (const m of models) rows.push({ label: m, from: { model: m } });
  } else {
    const b = bridges.find(x => x.level === +src);
    const sc = await sourceScene(b.level);
    sc.traverse(o => { if (o.userData.path && !helper.test(o.userData.path)) rows.push({ label: o.userData.path.split('/').slice(-2).join(' / '), from: { scene: b.scene, level: b.level, path: o.userData.path } }); });
    rows.sort((a, c) => a.label.localeCompare(c.label));
  }
  if (src !== $('source').value) return; // changed while reading
  const frag = document.createDocumentFragment();
  for (const r of rows) {
    const li = document.createElement('li');
    li.textContent = r.label;
    li.title = r.from.path ?? r.from.model;
    li.addEventListener('click', () => {
      for (const x of $('parts').children) x.classList.remove('sel');
      li.classList.add('sel');
      partPick = r;
      $('addPart').disabled = false;
      $('addGroup').disabled = !r.from.path || r.from.path.split('/').length <= 3;
    });
    li.addEventListener('dblclick', () => addPart(r.from));
    frag.append(li);
  }
  $('parts').append(frag);
  filterParts();
}
function filterParts() {
  const q = $('partSearch').value.trim().toLowerCase();
  for (const li of $('parts').children) li.hidden = q !== '' && !li.textContent.toLowerCase().includes(q) && !(li.title ?? '').toLowerCase().includes(q);
}

/** Makes an added part's objects (from its bridge's copy, or its model file) and its piece; resolves when it's there. */
async function makeAdded(id, from, scale) {
  const group = new THREE.Group();
  if (from.model) {
    const base = '/api/models/' + from.model.split('/').slice(0, -1).map(encodeURIComponent).join('/');
    const file = from.model.split('/').pop();
    const objText = await (await fetch('/api/models/' + from.model.split('/').map(encodeURIComponent).join('/'))).text();
    const mtlName = /^mtllib\s+(.+)$/m.exec(objText)?.[1]?.trim() ?? file.replace(/\.obj$/i, '.mtl');
    const loader = new OBJLoader();
    try {
      const mtl = await new MTLLoader().setPath(base + (base.endsWith('/') ? '' : '/')).loadAsync(encodeURIComponent(mtlName));
      mtl.preload();
      loader.setMaterials(mtl);
    } catch { /* no .mtl: plain grey */ }
    group.add(loader.parse(objText));
  } else {
    const sc = await sourceScene(from.level);
    sc.traverse(o => {
      if (!o.userData.path || !under(o.userData.path, from.path)) return;
      const c = o.clone();
      c.matrixAutoUpdate = false;
      c.matrix.copy(o.matrixWorld); // where its own bridge has it
      group.add(c);
    });
    if (!group.children.length) throw new Error(`${from.path} isn't in that bridge`);
  }
  group.matrixAutoUpdate = false;
  group.updateMatrixWorld(true);
  extras.add(group);
  const box = new THREE.Box3().setFromObject(group);
  const label = from.model ?? from.path.split('/').slice(-2).join(' / ');
  const piece = { obj: group, path: '+' + id, name: '+ ' + label, backdrop: false, box, base: new THREE.Matrix4(), added: null };
  const a = { id: String(id), from, scale, piece };
  piece.added = a;
  added.set(a.id, a);
  pieces.unshift(piece);
  addRow(piece);
  return a;
}

function dropAdded(id) {
  const a = added.get(String(id));
  if (!a) return;
  extras.remove(a.piece.obj);
  a.piece.row?.remove();
  pieces = pieces.filter(p => p !== a.piece);
  added.delete(String(id));
  edits.delete('+' + id);
  if (sel.includes('+' + id)) select(null);
}

/** Adds a part where the view is looking (its middle there, at its own height), and picks it. */
async function addPart(from) {
  remember();
  const id = String(nextId++);
  let a;
  try { a = await makeAdded(id, from, 1); }
  catch (err) { undo.pop(); say(`Couldn't add it: ${err.message}`); return; }
  const pivot = a.piece.box.getCenter(new THREE.Vector3());
  const at = controls.target.clone();
  // A model stands on the floor under where the view is looking; a bridge's part keeps its own height (the bridges'
  // floors are at about the same height).
  let dy = 0;
  if (from.model) {
    const down = new THREE.Raycaster(at.clone().add(new THREE.Vector3(0, 2, 0)), new THREE.Vector3(0, -1, 0), 0, 10);
    const floor = root && down.intersectObject(root, true).find(h => visibleUp(h.object) && h.face && h.face.normal.y > 0.5);
    dy = (floor ? floor.point.y : at.y) - a.piece.box.min.y;
  }
  edits.set('+' + id, { pivot, move: new THREE.Vector3(at.x - pivot.x, dy, at.z - pivot.z), turn: 0, hidden: false });
  applyPiece(a.piece);
  changed();
  select('+' + id);
  say(`Added ${from.model ?? short(from.path)}: drag it where you want it.`);
}

$('source').addEventListener('change', fillParts);
$('partSearch').addEventListener('input', filterParts);
$('addPart').addEventListener('click', () => partPick && addPart(partPick.from));
$('addGroup').addEventListener('click', () => {
  if (!partPick?.from.path) return;
  addPart({ ...partPick.from, path: partPick.from.path.split('/').slice(0, -1).join('/') });
});
$('addParts').addEventListener('toggle', () => { if ($('addParts').open && !$('parts').children.length) fillParts(); });

// ------------------------------------------------------------------------------------------- baked light
// The bridge's own lightmaps, as the game draws its fixed pieces: Unity's RGBM (the PNG holds colour times alpha;
// times 5 in gamma space is about 34.5 in linear). Pieces without one are lit by light probes in the game: an even
// fill light stands in. Added parts too (the game lights them by its probes).
// three.js divides indirect light by π (its lighting is physically based); Unity's lightmaps aren't.
const LightmapIntensity = 34.5 * Math.PI;
const lightmapTextures = new Map(); // `${level}/${index}` -> texture
const bakedMaterials = new Map();   // `${material.uuid}/${index}` -> material with that lightmap
function lightmapTexture(level, index) {
  const k = `${level}/${index}`;
  if (!lightmapTextures.has(k)) {
    const t = new THREE.TextureLoader().load(`/api/lightmap/level${level}/${index}.png`);
    t.flipY = false;            // rows as stored, like the .glb's textures
    t.colorSpace = THREE.SRGBColorSpace;
    t.channel = 1;              // the second UVs
    lightmapTextures.set(k, t);
  }
  return lightmapTextures.get(k);
}
function setBaked(on) {
  hemi.intensity = on ? 0.45 : 1.5;
  key.intensity = on ? 0 : 1.1;
  for (const p of pieces) {
    if (p.added) continue;
    const index = p.obj.userData.lightmap;
    p.obj.traverse(m => {
      if (!m.isMesh) return;
      if (!m.userData.unbaked) m.userData.unbaked = m.material;
      if (!on || index == null || !m.geometry.attributes.uv1) { m.material = m.userData.unbaked; return; }
      m.material = [m.userData.unbaked].flat().map(mat => {
        const k = `${mat.uuid}/${index}`;
        if (!bakedMaterials.has(k)) {
          const b = mat.clone();
          b.lightMap = lightmapTexture(bridge.level, index);
          b.lightMapIntensity = LightmapIntensity;
          b.needsUpdate = true;
          bakedMaterials.set(k, b);
        }
        return bakedMaterials.get(k);
      });
      if (!Array.isArray(m.userData.unbaked)) m.material = m.material[0];
    });
  }
}
$('baked').addEventListener('change', () => setBaked($('baked').checked));

// ------------------------------------------------------------------------------------------- camera
function frame(list) {
  const box = new THREE.Box3();
  for (const p of list) box.union(p.box);
  if (box.isEmpty()) return;
  const c = box.getCenter(new THREE.Vector3()), s = box.getSize(new THREE.Vector3());
  const r = Math.max(s.x, s.y, s.z);
  camera.position.set(c.x + r * 0.55, c.y + r * 0.55, c.z + r * 0.75);
  controls.target.copy(c);
  controls.update();
}

function focus(path) {
  const box = new THREE.Box3();
  for (const p of members(path)) box.union(new THREE.Box3().setFromObject(p.obj));
  if (box.isEmpty()) return;
  const c = box.getCenter(new THREE.Vector3());
  const r = Math.max(box.getSize(new THREE.Vector3()).length(), 0.5);
  const dir = camera.position.clone().sub(controls.target).normalize();
  controls.target.copy(c);
  camera.position.copy(c).addScaledVector(dir, r * 1.4);
  controls.update();
}

// ------------------------------------------------------------------------------------------- keys
// Flying with the keyboard moves the view and the point it turns around together.
const keys = new Set();
addEventListener('keydown', e => {
  if (e.target.matches?.('input[type=search], input[type=number], select')) return;
  const k = e.key.toLowerCase();
  if ((e.ctrlKey || e.metaKey) && k === 's') { e.preventDefault(); if (dirty) save(); return; }
  if ((e.ctrlKey || e.metaKey) && k === 'z') { e.preventDefault(); e.shiftKey ? doRedo() : doUndo(); return; }
  if ((e.ctrlKey || e.metaKey) && k === 'y') { e.preventDefault(); doRedo(); return; }
  if (e.ctrlKey || e.metaKey || e.altKey) return;
  const path = selPath();
  const step = e.shiftKey ? 0.01 : 0.05, turn = (e.shiftKey ? 1 : 15) * Math.PI / 180;
  if (e.target.closest?.('#fields')) return; // typing a value
  if (k === 'f' && path) focus(path);
  else if (k === 'h' && path) { for (const s of sel) toggleHidden(s); }
  else if (k === 'u') group();
  else if ((k === 'backspace' || k === 'delete') && path) { e.preventDefault(); for (const s of [...sel]) putBack(s); }
  else if (k === 'escape') select(null);
  else if (path && k.startsWith('arrow')) {
    e.preventDefault();
    const { fwd, right } = viewAxes();
    const d = k === 'arrowup' ? fwd : k === 'arrowdown' ? fwd.clone().negate() : k === 'arrowright' ? right : right.clone().negate();
    nudge(d.multiplyScalar(step), 0);
  }
  else if (path && (k === 'pageup' || k === 'pagedown')) { e.preventDefault(); nudge(new THREE.Vector3(0, k === 'pageup' ? step : -step, 0), 0); }
  else if (path && (k === '[' || k === ']' || k === '{' || k === '}')) nudge(new THREE.Vector3(), (k === '[' || k === '{') ? turn : -turn);
  else keys.add(k);
  if (e.shiftKey) keys.add('shift');
});
addEventListener('keyup', e => { keys.delete(e.key.toLowerCase()); if (!e.shiftKey) keys.delete('shift'); });
addEventListener('blur', () => keys.clear());

const clock = new THREE.Clock();
function fly(dt) {
  const v = new THREE.Vector3();
  const fwd = new THREE.Vector3(); camera.getWorldDirection(fwd);
  const right = new THREE.Vector3().crossVectors(fwd, camera.up).normalize();
  if (keys.has('w')) v.add(fwd);
  if (keys.has('s')) v.sub(fwd);
  if (keys.has('d')) v.add(right);
  if (keys.has('a')) v.sub(right);
  if (keys.has('e')) v.y += 1;
  if (keys.has('q')) v.y -= 1;
  if (v.lengthSq() === 0) return;
  v.normalize().multiplyScalar((keys.has('shift') ? 8 : 2.5) * dt);
  camera.position.add(v);
  controls.target.add(v);
}

function resize() {
  const w = canvas.clientWidth, h = canvas.clientHeight;
  if (canvas.width !== Math.floor(w * devicePixelRatio) || canvas.height !== Math.floor(h * devicePixelRatio)) {
    renderer.setSize(w, h, false);
    camera.aspect = w / h;
    camera.updateProjectionMatrix();
  }
}

renderer.setAnimationLoop(() => {
  resize();
  fly(Math.min(clock.getDelta(), 0.1));
  controls.update();
  renderer.render(scene, camera);
});

// ------------------------------------------------------------------------------------------- start
// While this page is open the designer keeps running; it stops by itself once it's closed.
new EventSource('/api/alive');
fetch('/api/version').then(r => r.ok ? r.text() : '').then(v => { $('version').textContent = v ? 'v' + v : ''; document.title = `Bridge Designer ${v}`; });
const bridges = await (await fetch('/api/bridges')).json();
for (const b of bridges) $('bridge').add(new Option(b.name, b.level));
$('bridge').addEventListener('change', () => {
  if (dirty && !confirm('This bridge has unsaved changes. Switch anyway?')) { $('bridge').value = bridge.level; return; }
  load(bridges.find(b => b.level === +$('bridge').value));
});
for (const b of bridges) $('source').add(new Option(b.name, b.level));
$('source').add(new Option('Models (BepInEx/bridges/models)', 'models'));
const first = bridges.find(b => b.scene === 'Bridge_scene_02') ?? bridges[0];
if (first) { $('bridge').value = first.level; load(first); }
else $('stats').textContent = 'No bridges found in the game\'s files.';

// For looking into the page from the browser's console.
window.bridgeDesigner = { get edits() { return edits; }, get pieces() { return pieces; }, outline, members, get sel() { return sel; }, get added() { return added; }, addPart, camera, controls };
