import bpy, os, json, re
from mathutils import Vector

root = os.path.dirname(os.path.abspath(__file__))
out = os.path.join(root, 'converted')
os.makedirs(out, exist_ok=True)
scene = bpy.context.scene
image_files = {}
for index, image in enumerate(bpy.data.images):
    if image.size[0] == 0: continue
    name = 'tex_%02d_%s.png' % (index, re.sub(r'[^A-Za-z0-9_-]', '_', image.name))
    image.filepath_raw = os.path.join(out, name)
    image.file_format = 'PNG'
    image.save()
    image_files[image.name] = name

def orient(p): return Vector((p.x, p.z, -p.y))

materials = {}
stats = []
all_min = Vector((1e10, 1e10, 1e10))
all_max = -all_min
vertex_base = 1
loop_base = 1
normal_base = 1
triangles = 0
with open(os.path.join(out, 'AbramsEnterprise.obj'), 'w') as f:
    f.write('# Local conversion of Ricky MadMan1701A Wallace Enterprise V6\nmtllib AbramsEnterprise.mtl\n')
    # Revival caps collision input: feed the complete principal hull before tiny details.
    hull_order=['SaucerTop','SaucerBottom','SaucerMiddle','SecondaryHull','NacelleBody','NacelleBody.001','Neckbase']
    for obj in sorted(bpy.data.objects, key=lambda o: (hull_order.index(o.name) if o.name in hull_order else 100, o.name)):
        if obj.type != 'MESH': continue
        tri_mod = obj.modifiers.new('ExportTriangulate', 'TRIANGULATE')
        mesh = obj.to_mesh(scene, True, 'PREVIEW')
        obj.modifiers.remove(tri_mod)
        if not mesh.polygons:
            bpy.data.meshes.remove(mesh)
            continue
        mesh.calc_normals_split()

        world = obj.matrix_world
        normal_matrix = world.to_3x3().inverted().transposed()
        points = [orient(world * v.co) for v in mesh.vertices]
        lo = Vector(tuple(min(p[a] for p in points) for a in range(3)))
        hi = Vector(tuple(max(p[a] for p in points) for a in range(3)))
        for a in range(3):
            all_min[a] = min(all_min[a], lo[a]); all_max[a] = max(all_max[a], hi[a])
        stats.append({'name': obj.name, 'min': list(lo), 'max': list(hi), 'triangles': len(mesh.polygons)})
        f.write('o %s\n' % obj.name.replace(' ', '_'))
        for p in points: f.write('v %.7g %.7g %.7g\n' % tuple(p))
        uv = mesh.uv_layers.active
        face_images = mesh.uv_textures.active
        uv_map, normal_map, uv_values, normal_values = {}, {}, [], []
        loop_uv, loop_normal = [], []
        for loop in mesh.loops:
            xy = tuple(round(v,7) for v in (uv.data[loop.index].uv if uv else (0,0)))
            n = tuple(round(v,6) for v in orient(normal_matrix * loop.normal).normalized())
            if xy not in uv_map: uv_map[xy]=len(uv_values); uv_values.append(xy)
            if n not in normal_map: normal_map[n]=len(normal_values); normal_values.append(n)
            loop_uv.append(uv_map[xy]); loop_normal.append(normal_map[n])
        for xy in uv_values: f.write('vt %.7g %.7g\n' % xy)
        for n in normal_values: f.write('vn %.7g %.7g %.7g\n' % n)
        active = None
        for poly in mesh.polygons:

            mat = mesh.materials[poly.material_index] if poly.material_index < len(mesh.materials) else None
            image = face_images.data[poly.index].image if face_images else None
            if image is None and mat:
                image = next((s.texture.image for s in mat.texture_slots if s and s.texture and s.texture.type == 'IMAGE' and s.use_map_color_diffuse and s.texture.image), None)
            key = (mat.name if mat else 'Hull', image.name if image else '')
            if key not in materials:
                name = 'm%03d_%s' % (len(materials), re.sub(r'[^A-Za-z0-9_-]', '_', key[0]))
                materials[key] = {'id':name, 'image':image_files.get(key[1]), 'colour':list(mat.diffuse_color) if mat else [0.8]*3, 'emit': mat.emit if mat else 0, 'alpha': mat.alpha if mat else 1}
            name = materials[key]['id']
            if name != active: f.write('usemtl %s\n' % name); active = name
            loops = list(poly.loop_indices)
            if world.to_3x3().determinant() < 0: loops.reverse()
            f.write('f '+' '.join('%d/%d/%d' % (mesh.loops[i].vertex_index+vertex_base,loop_uv[i]+loop_base,loop_normal[i]+normal_base) for i in loops)+'\n')
            triangles += 1
        vertex_base += len(mesh.vertices)
        loop_base += len(uv_values)
        normal_base += len(normal_values)
        bpy.data.meshes.remove(mesh)

with open(os.path.join(out,'AbramsEnterprise.mtl'),'w') as f:
    for m in materials.values():
        f.write('newmtl %s\nKd %s\nd %.5f\n' % (m['id'],' '.join(str(c) for c in m['colour']),m['alpha']))
        if m['image']: f.write('map_Kd %s\n' % m['image'])
        if m['emit'] > 0:
            if m['image']: f.write('map_Ke %s\n' % m['image'])
            else: f.write('Ke %s\n' % ' '.join(str(c*m['emit']) for c in m['colour']))
        f.write('\n')

with open(os.path.join(root,'conversion-report.json'),'w') as f:
    json.dump({'vertices':vertex_base-1,'triangles':triangles,'bounds':[list(all_min),list(all_max)],'materials':list(materials.values()),'objects':stats},f,indent=2)
print('EXPORT_COMPLETE vertices=%d triangles=%d materials=%d' % (vertex_base-1,triangles,len(materials)))
