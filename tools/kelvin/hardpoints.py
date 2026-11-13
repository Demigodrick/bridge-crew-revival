import bpy,json,os
from mathutils import Vector
o=bpy.data.objects['Phaserbanks']; mesh=o.to_mesh(bpy.context.scene,True,'PREVIEW')
parent=list(range(len(mesh.vertices)))
def find(a):
    while parent[a]!=a:
        parent[a]=parent[parent[a]]; a=parent[a]
    return a
for e in mesh.edges:
    a,b=e.vertices;parent[find(a)]=find(b)
parts={}
for v in mesh.vertices:
    p=o.matrix_world*v.co
    parts.setdefault(find(v.index),[]).append(Vector((p.x,p.z,-p.y)))
result=[]
for points in parts.values():
    if len(points)<20:continue
    lo=[min(p[a] for p in points) for a in range(3)];hi=[max(p[a] for p in points) for a in range(3)]
    result.append({'vertices':len(points),'min':lo,'max':hi,'center':[(a+b)/2 for a,b in zip(lo,hi)]})
with open(os.path.join(os.path.dirname(os.path.abspath(__file__)),'hardpoints.json'),'w') as f:json.dump(result,f,indent=2)
