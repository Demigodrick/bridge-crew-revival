// The bridge designer's page: loads a bridge (as .glb, made from the game's files by the program serving this page),
// lists its pieces, and lets you move, turn and hide pieces or whole groups. The changes are saved as the bridge's
// layout (BepInEx/bridges/<scene>.json in the game folder), which the mod puts on whenever that bridge loads.
//
// A change belongs to a path in the bridge's hierarchy (a piece, or a group of them: U picks the group) and moves
// everything under it, as the game's transforms would. Pieces inside a changed group follow it and can't be changed
// on their own (and the other way round), so the game and this page always agree on what moved.
//
// Coordinates: this page works in glTF's (right-handed); the file has the game's (Unity's, left-handed: z flipped,
// turns the other way).
import * as THREE from 'three';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { TransformControls } from 'three/addons/controls/TransformControls.js';

const $ = id => document.getElementById(id);
const canvas = $('canvas');
const renderer = new THREE.WebGLRenderer({ canvas, antialias: true });
renderer.setPixelRatio(devicePixelRatio);
const scene = new THREE.Scene();
scene.background = new THREE.Color(0x0e1116);
// The game's own light is baked into its bridges and isn't brought over: an even light to see by instead.
scene.add(new THREE.HemisphereLight(0xffffff, 0x3a4050, 1.5));
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
let selPath = null;
let dirty = false;
const undo = [], redo = [];

const outline = new THREE.Box3Helper(new THREE.Box3(), 0xf2c14e);
outline.visible = false;
scene.add(outline);

// The handle the move/turn gizmo drags: sits at the selection's middle, plus its change.
const handle = new THREE.Object3D();
scene.add(handle);
const gizmo = new TransformControls(camera, canvas);
gizmo.setSize(0.8);
scene.add(gizmo);
let gizmoPivot = null;

// ------------------------------------------------------------------------------------------- bridges
const bridges = await (await fetch('/api/bridges')).json();
for (const b of bridges) $('bridge').add(new Option(b.name, b.level));
$('bridge').addEventListener('change', () => {
  if (dirty && !confirm('This bridge has unsaved changes. Switch anyway?')) { $('bridge').value = bridge.level; return; }
  load(bridges.find(b => b.level === +$('bridge').value));
});
const first = bridges.find(b => b.scene === 'Bridge_scene_02') ?? bridges[0];
if (first) { $('bridge').value = first.level; load(first); }
else $('stats').textContent = 'No bridges found in the game\'s files.';

function load(b) {
  bridge = b;
  if (root) { scene.remove(root); dispose(root); root = null; }
  select(null);
  pieces = [];
  edits = new Map();
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
      pieces.push({ obj: o, path, name: path.split('/').slice(-2).join(' / '), backdrop: box.getSize(new THREE.Vector3()).length() > 50, box, base: o.matrix.clone() });
    });
    pieces.sort((a, b) => a.path.localeCompare(b.path));
    buildList();
    await loadLayout();
    applyAll();
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
    tmp.b.makeRotationY(turn).multiply(tmp.a);
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
    tag.textContent = hidden ? 'hidden' : o ? (o.k === p.path ? 'changed' : 'in ' + short(o.k)) : p.backdrop ? 'backdrop' : '';
    tag.classList.toggle('edit', !!o);
  }
}
const applyAll = () => { for (const p of pieces) applyPiece(p); refreshSelection(); };
const applyUnder = path => { for (const p of members(path)) applyPiece(p); refreshSelection(); };

function editFor(path) {
  let e = edits.get(path);
  if (!e) edits.set(path, e = { pivot: middle(path), move: new THREE.Vector3(), turn: 0, hidden: false });
  return e;
}

function tidy(path) {
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
const snapshot = () => JSON.stringify([...edits].map(([k, e]) => [k, e.pivot.toArray(), e.move.toArray(), e.turn, e.hidden]));
function restore(s) {
  edits = new Map(JSON.parse(s).map(([k, p, m, t, h]) => [k, { pivot: new THREE.Vector3(...p), move: new THREE.Vector3(...m), turn: t, hidden: h }]));
  applyAll();
  changed();
}
function remember() { undo.push(snapshot()); redo.length = 0; if (undo.length > 200) undo.shift(); }
function doUndo() { if (undo.length) { redo.push(snapshot()); restore(undo.pop()); } }
function doRedo() { if (redo.length) { undo.push(snapshot()); restore(redo.pop()); } }

function toggleHidden(path) {
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
  for (const c of layout.pieces ?? [])
    edits.set(c.path, { pivot: fromGame(c.pivot), move: fromGame(c.move), turn: -c.turn * Math.PI / 180, hidden: !!c.hidden });
  say(`Layout loaded: ${edits.size} change${edits.size === 1 ? '' : 's'}.`);
}

async function save() {
  for (const k of [...edits.keys()]) tidy(k);
  const layout = {
    format: 1,
    scene: bridge.scene,
    pieces: [...edits].map(([path, e]) => ({ path, pivot: toGame(e.pivot), move: toGame(e.move), turn: r4(-e.turn * 180 / Math.PI), hidden: e.hidden })),
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
  for (const p of pieces) {
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
    li.addEventListener('click', () => select(p.path, false));
    li.addEventListener('dblclick', () => focus(p.path));
    p.row = li;
    frag.append(li);
  }
  $('list').append(frag);
  filter();
}

function filter() {
  const q = $('search').value.trim().toLowerCase();
  for (const p of pieces) p.row.hidden = q !== '' && !p.path.toLowerCase().includes(q);
}
$('search').addEventListener('input', filter);
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
function select(path, scroll = true) {
  for (const p of pieces) p.row?.classList.remove('sel');
  // Inside a changed group: the group.
  if (path) { const o = ownerOf(path); if (o && o.k !== path && under(path, o.k)) path = o.k; }
  selPath = path;
  $('info').hidden = !path;
  if (!path) { gizmo.detach(); outline.visible = false; return; }
  const list = members(path);
  for (const p of list) p.row?.classList.add('sel');
  if (scroll && list[0]?.row) list[0].row.scrollIntoView({ block: 'nearest' });
  refreshSelection();
}

function refreshSelection() {
  if (!selPath) return;
  const list = members(selPath);
  const box = new THREE.Box3();
  for (const p of list) box.union(new THREE.Box3().setFromObject(p.obj));
  outline.box.copy(box);
  outline.visible = !box.isEmpty();
  const size = box.getSize(new THREE.Vector3());
  const mats = new Set();
  for (const p of list) p.obj.traverse(m => { if (m.isMesh) for (const mat of [m.material].flat()) mats.add(mat.name || '(unnamed)'); });
  const one = list.length === 1 && list[0].path === selPath ? list[0] : null;
  $('infoName').textContent = short(selPath) + (one ? '' : ` (group of ${list.length})`);
  $('infoPath').textContent = selPath;
  $('infoMesh').textContent = one ? `${one.obj.userData.mesh ?? '?'} (${one.obj.userData.file ?? '?'})` : '—';
  $('infoSize').textContent = `${size.x.toFixed(2)} × ${size.y.toFixed(2)} × ${size.z.toFixed(2)} m`;
  $('infoMats').textContent = [...mats].slice(0, 8).join(', ') + (mats.size > 8 ? ` +${mats.size - 8}` : '');
  $('infoBatched').textContent = list.some(p => p.obj.userData.batched) ? 'yes: the game copies it to move it' : 'no';
  const e = edits.get(selPath);
  const why = blocked(selPath);
  $('infoChange').textContent = why ?? (!e ? 'none' : e.hidden ? 'hidden' :
    `moved ${e.move.x.toFixed(2)}, ${e.move.y.toFixed(2)}, ${e.move.z.toFixed(2)} m · turned ${(e.turn * 180 / Math.PI).toFixed(0)}°`);
  // The gizmo: on what can be changed and isn't hidden.
  if (why || e?.hidden) { gizmo.detach(); return; }
  if (gizmo.dragging) return;
  gizmoPivot = e ? e.pivot.clone() : middle(selPath);
  handle.position.copy(gizmoPivot).add(e ? e.move : new THREE.Vector3());
  handle.rotation.set(0, e ? e.turn : 0, 0);
  handle.updateMatrixWorld();
  if (gizmo.object !== handle) gizmo.attach(handle);
}

gizmo.addEventListener('mouseDown', () => remember());
gizmo.addEventListener('dragging-changed', e => { controls.enabled = !e.value; if (!e.value) { lastDrag = performance.now(); if (selPath) { tidy(selPath); refreshSelection(); } } });
gizmo.addEventListener('objectChange', () => {
  if (!selPath || !gizmoPivot) return;
  const e = editFor(selPath);
  e.move.copy(handle.position).sub(e.pivot);
  e.turn = handle.rotation.y;
  applyUnder(selPath);
  changed();
});
let lastDrag = 0;

function setMode(mode) {
  gizmo.setMode(mode);
  // Turning is only about up: bridges stay level.
  gizmo.showX = gizmo.showZ = mode === 'translate';
  $('modeMove').classList.toggle('on', mode === 'translate');
  $('modeTurn').classList.toggle('on', mode === 'rotate');
}
function setSnap(on) {
  gizmo.setTranslationSnap(on ? 0.05 : null);
  gizmo.setRotationSnap(on ? THREE.MathUtils.degToRad(15) : null);
}
$('modeMove').addEventListener('click', () => setMode('translate'));
$('modeTurn').addEventListener('click', () => setMode('rotate'));
$('snap').addEventListener('change', () => setSnap($('snap').checked));
$('undo').addEventListener('click', doUndo);
$('group').addEventListener('click', group);
setMode('translate');
setSnap($('snap').checked);

function group() {
  if (!selPath) return;
  const parts = selPath.split('/');
  // Not the whole bridge (its top object, or the scene's roots).
  if (parts.length <= 3) { say('That would be the whole bridge.'); return; }
  select(parts.slice(0, -1).join('/'));
}

const pieceOf = o => { for (; o; o = o.parent) { const p = pieces.find(x => x.obj === o); if (p) return p; } return null; };
const ray = new THREE.Raycaster();
function hitAt(e) {
  const r = canvas.getBoundingClientRect();
  ray.setFromCamera(new THREE.Vector2(((e.clientX - r.left) / r.width) * 2 - 1, -((e.clientY - r.top) / r.height) * 2 + 1), camera);
  return root ? ray.intersectObject(root, true).find(h => visibleUp(h.object)) : null;
}
const visibleUp = o => { for (; o; o = o.parent) if (!o.visible) return false; return true; };

// A click (not a drag, and not on the gizmo) picks; a double-click turns the view around that point.
let down = null;
canvas.addEventListener('pointerdown', e => { down = { x: e.clientX, y: e.clientY, onGizmo: gizmo.axis !== null }; });
canvas.addEventListener('pointerup', e => {
  if (!down || down.onGizmo || e.button !== 0 || performance.now() - lastDrag < 150 || Math.hypot(e.clientX - down.x, e.clientY - down.y) > 4) return;
  const hit = hitAt(e);
  select(hit ? pieceOf(hit.object)?.path ?? null : null);
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
  if (e.target.matches('input[type=search], select')) return;
  const k = e.key.toLowerCase();
  if ((e.ctrlKey || e.metaKey) && k === 's') { e.preventDefault(); if (dirty) save(); return; }
  if ((e.ctrlKey || e.metaKey) && k === 'z') { e.preventDefault(); e.shiftKey ? doRedo() : doUndo(); return; }
  if ((e.ctrlKey || e.metaKey) && k === 'y') { e.preventDefault(); doRedo(); return; }
  if (e.ctrlKey || e.metaKey || e.altKey) return;
  if (k === 'f' && selPath) focus(selPath);
  else if (k === 'h' && selPath) toggleHidden(selPath);
  else if (k === 'u') group();
  else if (k === 't') setMode('translate');
  else if (k === 'r') setMode('rotate');
  else if ((k === 'backspace' || k === 'delete') && selPath) putBack(selPath);
  else if (k === 'escape') select(null);
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

// For looking into the page from the browser's console.
window.bridgeDesigner = { get edits() { return edits; }, get pieces() { return pieces; }, outline, members, get selPath() { return selPath; } };
