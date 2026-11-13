import os, collections, json, sys
root=os.path.dirname(os.path.abspath(__file__)); folder=os.path.abspath(sys.argv[1]) if len(sys.argv)>1 else os.path.join(root,'converted')
positions=[]; uv=0; normals=0; faces=collections.OrderedDict(); material=None
with open(os.path.join(folder,'AbramsEnterprise.obj')) as stream:
    for line in stream:
        s=line.split()
        if not s:continue
        if s[0]=='v':positions.append(tuple(map(float,s[1:4])))
        elif s[0]=='vt':uv+=1
        elif s[0]=='vn':normals+=1
        elif s[0]=='usemtl':material=s[1];faces.setdefault(material,[])
        elif s[0]=='f':
            assert len(s)==4,'Not triangulated'
            corners=[tuple(map(int,c.split('/'))) for c in s[1:]]
            for v,t,n in corners:assert 0<v<=len(positions) and 0<t<=uv and 0<n<=normals,'Invalid index'
            faces[material].append(corners)
assert len(positions)<2000000
assert sum(len(f) for f in faces.values())<4000000
materials=set(); textures=set()
for line in open(os.path.join(folder,'AbramsEnterprise.mtl')):
    s=line.split()
    if not s:continue
    if s[0]=='newmtl':materials.add(s[1])
    elif s[0].startswith('map_'):
        assert os.path.isfile(os.path.join(folder,s[1])), 'Missing texture '+s[1]
        textures.add(s[1])
assert set(faces)<=materials
# Every texture DXT-compressible (a multiple of four on each side) and at most 2048 (tools/kelvin/fit_textures.py).
import struct
for name in textures:
    with open(os.path.join(folder,name),'rb') as png:
        head=png.read(24)
    assert head[:8]==b'\x89PNG\r\n\x1a\n','Not a PNG: '+name
    w,h=struct.unpack('>II',head[16:24])
    assert w%4==0 and h%4==0 and max(w,h)<=2048,'Texture not fitted (run fit_textures.py): %s %dx%d'%(name,w,h)
# The in-game updater only fetches ships/<ship>/<file>: nothing in subfolders.
assert not any(os.path.isdir(os.path.join(folder,n)) for n in os.listdir(folder)),'Subfolders in the ship folder are never fetched by the updater'
# Mirror the loader's 65000-vertex chunking and 260000-vertex collision-input cap.
parts=[]
for group in faces.values():
    vertices={}
    for triangle in group:
        if len(vertices)+3>65000:parts.append(list(vertices));vertices={}
        for c in triangle:vertices[c]=True
    if vertices:parts.append(list(vertices))
collision=[]
for part in parts:
    if len(collision)+len(part)<=260000:collision.extend(positions[c[0]-1] for c in part)
bounds=lambda pts:[[min(p[a] for p in pts) for a in range(3)],[max(p[a] for p in pts) for a in range(3)]]
all_bounds=bounds(positions); collision_bounds=bounds(collision)
coverage=[(collision_bounds[1][a]-collision_bounds[0][a])/(all_bounds[1][a]-all_bounds[0][a]) for a in range(3)]
assert min(coverage)>.95,'Collision input misses a major hull extent: '+str(coverage)
report={'positions':len(positions),'triangles':sum(len(g) for g in faces.values()),'render_vertices':sum(len(p) for p in parts),'chunks':len(parts),'textures':len(textures),'collision_input_vertices':len(collision),'collision_extent_coverage':coverage}
with open(os.path.join(root,'validation.json'),'w') as out:json.dump(report,out,indent=2)
print(json.dumps(report))
