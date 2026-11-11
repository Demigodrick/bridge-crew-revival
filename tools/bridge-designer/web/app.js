// The bridge viewer: loads a bridge (as .glb, made from the game's files by the program serving this page), lists
// its pieces and lets you look around, pick and hide them. The designer's editing comes next, on top of this.
import * as THREE from 'three';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';

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

let root = null;          // the loaded bridge
let pieces = [];          // { obj, path, name, backdrop, row }
let selected = null;
const outline = new THREE.Box3Helper(new THREE.Box3(), 0xf2c14e);
outline.visible = false;
scene.add(outline);

// ------------------------------------------------------------------------------------------- bridges
const bridges = await (await fetch('/api/bridges')).json();
for (const b of bridges) $('bridge').add(new Option(`${b.name}`, b.level));
$('bridge').addEventListener('change', () => load(+$('bridge').value));
const first = bridges.find(b => b.scene === 'Bridge_scene_02') ?? bridges[0];
if (first) { $('bridge').value = first.level; load(first.level); }
else $('stats').textContent = 'No bridges found in the game\'s files.';

function load(level) {
  if (root) { scene.remove(root); dispose(root); root = null; }
  select(null);
  pieces = [];
  $('list').replaceChildren();
  $('loading').hidden = false;
  const bar = $('loading').querySelector('.bar div');
  bar.style.width = '0';
  $('stats').textContent = 'Reading the bridge from the game…';
  const t0 = performance.now();
  new GLTFLoader().load(`/api/bridge/level${level}.glb`, gltf => {
    root = gltf.scene;
    scene.add(root);
    root.updateMatrixWorld(true);
    // Every piece is a node with its path in the scene (the loader keeps glTF extras as userData).
    root.traverse(o => {
      if (!o.userData.path) return;
      const box = new THREE.Box3().setFromObject(o);
      const size = box.getSize(new THREE.Vector3()).length();
      const path = o.userData.path;
      pieces.push({ obj: o, path, name: path.split('/').slice(-2).join(' / '), backdrop: size > 50, box });
    });
    pieces.sort((a, b) => a.path.localeCompare(b.path));
    for (const p of pieces) if (p.backdrop) p.obj.visible = $('backdrops').checked;
    buildList();
    frame(pieces.filter(p => !p.backdrop));
    const tris = countTriangles(root);
    $('stats').textContent = `${pieces.length} pieces · ${(tris / 1000).toFixed(0)}k triangles · ${((performance.now() - t0) / 1000).toFixed(1)} s`;
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

// ------------------------------------------------------------------------------------------- the list
function buildList() {
  const frag = document.createDocumentFragment();
  for (const p of pieces) {
    const li = document.createElement('li');
    li.title = p.path;
    li.innerHTML = `<input type="checkbox" aria-label="Shown"><span class="name"></span>${p.backdrop ? '<span class="tag">backdrop</span>' : ''}`;
    li.querySelector('.name').textContent = p.name;
    const box = li.querySelector('input');
    box.checked = p.obj.visible;
    box.addEventListener('click', e => { e.stopPropagation(); setShown(p, box.checked); });
    li.addEventListener('click', () => select(p, false));
    li.addEventListener('dblclick', () => focus(p));
    p.row = li;
    li.classList.toggle('off', !p.obj.visible);
    frag.append(li);
  }
  $('list').append(frag);
  filter();
}

function setShown(p, shown) {
  p.obj.visible = shown;
  p.row.querySelector('input').checked = shown;
  p.row.classList.toggle('off', !shown);
}

function filter() {
  const q = $('search').value.trim().toLowerCase();
  for (const p of pieces) p.row.hidden = q !== '' && !p.path.toLowerCase().includes(q);
}
$('search').addEventListener('input', filter);
$('backdrops').addEventListener('change', () => { for (const p of pieces) if (p.backdrop) setShown(p, $('backdrops').checked); });
$('showAll').addEventListener('click', () => { for (const p of pieces) if (!p.backdrop || $('backdrops').checked) setShown(p, true); });
$('frame').addEventListener('click', () => frame(pieces.filter(p => p.obj.visible)));

// ------------------------------------------------------------------------------------------- selection
function select(p, scroll = true) {
  if (selected?.row) selected.row.classList.remove('sel');
  selected = p;
  outline.visible = !!p;
  $('info').hidden = !p;
  if (!p) return;
  p.row.classList.add('sel');
  if (scroll) p.row.scrollIntoView({ block: 'nearest' });
  outline.box.setFromObject(p.obj);
  const size = outline.box.getSize(new THREE.Vector3());
  const mats = new Set();
  p.obj.traverse(m => { if (m.isMesh) for (const mat of [m.material].flat()) mats.add(mat.name || '(unnamed)'); });
  $('infoName').textContent = p.path.split('/').pop();
  $('infoPath').textContent = p.path;
  $('infoMesh').textContent = `${p.obj.userData.mesh ?? '?'} (${p.obj.userData.file ?? '?'})`;
  $('infoSize').textContent = `${size.x.toFixed(2)} × ${size.y.toFixed(2)} × ${size.z.toFixed(2)} m`;
  $('infoMats').textContent = [...mats].join(', ');
  $('infoBatched').textContent = p.obj.userData.batched ? 'yes: part of a combined mesh in the game' : 'no';
}

const pieceOf = o => { for (; o; o = o.parent) { const p = pieces.find(x => x.obj === o); if (p) return p; } return null; };

const ray = new THREE.Raycaster();
function hitAt(e) {
  const r = canvas.getBoundingClientRect();
  ray.setFromCamera(new THREE.Vector2(((e.clientX - r.left) / r.width) * 2 - 1, -((e.clientY - r.top) / r.height) * 2 + 1), camera);
  return root ? ray.intersectObject(root, true).find(h => h.object.visible && visibleUp(h.object)) : null;
}
const visibleUp = o => { for (; o; o = o.parent) if (!o.visible) return false; return true; };

// A click (not a drag) picks; a double-click turns the view around that point.
let down = null;
canvas.addEventListener('pointerdown', e => { down = { x: e.clientX, y: e.clientY }; });
canvas.addEventListener('pointerup', e => {
  if (!down || e.button !== 0 || Math.hypot(e.clientX - down.x, e.clientY - down.y) > 4) return;
  const hit = hitAt(e);
  select(hit ? pieceOf(hit.object) : null);
});
canvas.addEventListener('dblclick', e => {
  const hit = hitAt(e);
  if (hit) { controls.target.copy(hit.point); controls.update(); }
});

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

function focus(p) {
  const box = new THREE.Box3().setFromObject(p.obj);
  const c = box.getCenter(new THREE.Vector3());
  const r = Math.max(box.getSize(new THREE.Vector3()).length(), 0.5);
  const dir = camera.position.clone().sub(controls.target).normalize();
  controls.target.copy(c);
  camera.position.copy(c).addScaledVector(dir, r * 1.4);
  controls.update();
}

// Flying with the keyboard moves the view and the point it turns around together.
const keys = new Set();
addEventListener('keydown', e => {
  if (e.target.matches('input, select')) return;
  const k = e.key.toLowerCase();
  if (k === 'f' && selected) focus(selected);
  else if (k === 'h' && selected) setShown(selected, !selected.obj.visible);
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
  if (selected) outline.box.setFromObject(selected.obj);
  renderer.render(scene, camera);
});
