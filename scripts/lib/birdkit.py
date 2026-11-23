"""Shared parts of the two rigid-node bird models (make_eagle_model.py, make_phoenix_model.py): numpy, no skin.

A bird is a node rig (RIG: name, parent, translation), flat-shaded parts hung on rig nodes (PARTS), glTF materials (MATS) and clips of node
rotations that pose() returns per frame. These lists are filled by the importing script (one bird per process); export() writes the .glb.
"""
import json, math, struct
import numpy as np

MATS=[];PARTS=[];RIG=[]

def node(name,parent=None,pos=(0,0,0)):
    RIG.append({'name':name,'parent':parent,'translation':list(pos)})

def mesh(name,verts,faces,mat,parent='FlightRoot',center=None):
    vv=np.array(verts,float)
    if center is not None:
        center=np.array(center)
        for i,f in enumerate(faces):
            a,b,c=vv[f[:3]]
            if np.dot(np.cross(b-a,c-a),vv[f].mean(axis=0)-center)<0:faces[i]=list(reversed(f))
    PARTS.append({'name':name,'v':vv,'f':faces,'mat':mat,'parent':parent})

def ellipsoid(name,c,r,mat,parent='FlightRoot',segments=10,rings=6):
    v=[np.array(c)+[0,r[1],0]]
    for j in range(1,rings):
        a=math.pi*j/rings
        for i in range(segments):
            b=math.tau*i/segments
            v.append(np.array(c)+[r[0]*math.sin(a)*math.cos(b),r[1]*math.cos(a),r[2]*math.sin(a)*math.sin(b)])
    v.append(np.array(c)-[0,r[1],0]);bottom=len(v)-1;f=[]
    for i in range(segments):f.append([0,1+i,1+(i+1)%segments])
    for j in range(rings-2):
        for i in range(segments):
            a=1+j*segments+i;b=1+j*segments+(i+1)%segments;f.append([a,b,b+segments,a+segments])
    for i in range(segments):f.append([bottom,bottom-segments+i,bottom-segments+(i+1)%segments])
    mesh(name,v,f,mat,parent,c)

def feather(name,points,width,base=None,tip=None,parent='FlightRoot',thickness=.025):
    points=np.array(points,float);v=[]
    widths=[.20,1,.70,.008] if len(points)==4 else [math.sin(math.pi*i/(len(points)-1))*.85+.15 for i in range(len(points))]
    widths[-1]=.008
    for i,p in enumerate(points):
        d=points[min(i+1,len(points)-1)]-points[max(i-1,0)]
        u=np.cross(d,[0,1,0]);u/=max(np.linalg.norm(u),1e-9)
        w=width*widths[i];h=thickness*widths[i]
        v.extend([p+u*w,p+[0,h,0],p-u*w,p-[0,h,0]])
    center=points.mean(axis=0)
    for j in range(len(points)-1):
        mat=tip if j==len(points)-2 else base
        faces=[]
        for k in range(4):faces.append([j*4+k,j*4+(k+1)%4,(j+1)*4+(k+1)%4,(j+1)*4+k])
        mesh(name,v,faces,mat,parent,(points[j]+points[j+1])/2)
    mesh(name+'_ends',v,[[3,2,1,0],list(range(len(v)-4,len(v)))],base,parent,center)

def spike(name,a,b,width,mat,parent='FlightRoot'):
    a=np.array(a,float);b=np.array(b,float);d=b-a;d/=np.linalg.norm(d)
    u=np.cross(d,[0,1,0] if abs(d[1])<.95 else [1,0,0]);u/=np.linalg.norm(u)
    w=np.cross(d,u);v=[a+u*width,a+w*width*.7,a-u*width,a-w*width*.7,b]
    mesh(name,v,[[0,3,2,1],[0,1,4],[1,2,4],[2,3,4],[3,0,4]],mat,parent,(a+b)/2)

def contour_loft(name,sections,mat,parent='FlightRoot',ventral=None,segments=8):
    # Explicit cross-sections, rather than overlapping spheres, control silhouette.
    v=[]
    for z,y,rx,ry in sections:
        for i in range(segments):
            angle=math.tau*i/segments
            v.append((rx*math.cos(angle),y+ry*math.sin(angle),z))
    for j in range(len(sections)-1):
        center=[0,(sections[j][1]+sections[j+1][1])/2,(sections[j][0]+sections[j+1][0])/2]
        for i in range(segments):
            k=(i+1)%segments
            face=[j*segments+i,j*segments+k,(j+1)*segments+k,(j+1)*segments+i]
            mesh(name,v,[face],ventral if ventral is not None and i>=segments//2 else mat,parent,center)
    mesh(name+'_caps',v,[list(range(segments)),list(range((len(sections)-1)*segments,len(v)))],mat,parent,[0,np.mean([s[1] for s in sections]),np.mean([s[0] for s in sections])])

# Flat-shaded GLB with node animation, without external dependencies.
def quaternion(x=0,y=0,z=0):
    # Euler XYZ composed qz*qy*qx.
    cx,sx=math.cos(x/2),math.sin(x/2);cy,sy=math.cos(y/2),math.sin(y/2);cz,sz=math.cos(z/2),math.sin(z/2)
    return [sx*cy*cz-cx*sy*sz,cx*sy*cz+sx*cy*sz,cx*cy*sz-sx*sy*cz,cx*cy*cz+sx*sy*sz]

def export(out,pose,clips,scene,generator,extensions=None):
    """Writes the bird to `out`; clips: [(name, seconds)], pose(t, clip name) -> {node: {path: value}}."""
    blob=bytearray();views=[];access=[];meshes=[];nodes=[];ids={}
    def acc(values,kind,target=None):
        values=np.array(values,dtype='<f4');blob.extend(b'\0'*((-len(blob))%4));offset=len(blob);blob.extend(values.tobytes());view={'buffer':0,'byteOffset':offset,'byteLength':values.nbytes}
        if target:view['target']=target
        views.append(view);a={'bufferView':len(views)-1,'componentType':5126,'count':len(values),'type':kind}
        if kind in ['VEC3','SCALAR']:a.update(min=np.atleast_1d(values.min(axis=0)).tolist(),max=np.atleast_1d(values.max(axis=0)).tolist())
        access.append(a);return len(access)-1
    for r in RIG:
        ids[r['name']]=len(nodes);nodes.append({'name':r['name'],'translation':r['translation'],'children':[]})
    for r in RIG:
        if r['parent']:nodes[ids[r['parent']]]['children'].append(ids[r['name']])
    grouped={}
    for p in PARTS:
        key=(p['parent'],p['mat']);grouped.setdefault(key,[]).append(p)
    triangles=0
    for (parent,mat),parts in grouped.items():
        positions=[];normals=[]
        for p in parts:
            for f in p['f']:
                for j in range(1,len(f)-1):
                    tri=p['v'][[f[0],f[j],f[j+1]]];n=np.cross(tri[1]-tri[0],tri[2]-tri[0]);length=np.linalg.norm(n)
                    if length<1e-9:continue
                    positions.extend(tri);normals.extend([n/length]*3);triangles+=1
        name=parent+'__'+MATS[mat]['name'][3:].replace(' ','_')
        meshes.append({'name':name,'primitives':[{'attributes':{'POSITION':acc(positions,'VEC3',34962),'NORMAL':acc(normals,'VEC3',34962)},'material':mat}]})
        nodes[ids[parent]]['children'].append(len(nodes));nodes.append({'name':name,'mesh':len(meshes)-1})
    animations=[]
    for mode,duration in clips:
        frames=65;times=np.linspace(0,duration,frames);poses=[pose(i/(frames-1),mode) for i in range(frames)];time_id=acc(times,'SCALAR');channels=[];samplers=[]
        for name,tracks in poses[0].items():
            for path in tracks:
                values=[p[name][path] for p in poses]
                samplers.append({'input':time_id,'output':acc(values,'VEC4' if path=='rotation' else 'VEC3'),'interpolation':'LINEAR'})
                channels.append({'sampler':len(samplers)-1,'target':{'node':ids[name],'path':path}})
        animations.append({'name':mode,'samplers':samplers,'channels':channels})
    doc={'asset':{'version':'2.0','generator':generator},'scene':0,'scenes':[{'name':scene,'nodes':[0]}],'nodes':nodes,'meshes':meshes,'materials':MATS,'animations':animations,'buffers':[{'byteLength':len(blob)}],'bufferViews':views,'accessors':access}
    if extensions:doc={'extensionsUsed':extensions,**doc}
    js=json.dumps(doc,separators=(',',':')).encode();js+=b' '*((-len(js))%4);blob+=b'\0'*((-len(blob))%4)
    data=struct.pack('<III',0x46546c67,2,28+len(js)+len(blob))+struct.pack('<II',len(js),0x4e4f534a)+js+struct.pack('<II',len(blob),0x004e4942)+blob
    out.parent.mkdir(parents=True,exist_ok=True);out.write_bytes(data)
    print(json.dumps({'triangles':triangles,'mesh_parts':len(meshes),'materials':len(MATS),'bytes':len(data),'clips':dict(clips),'out':str(out)}))
