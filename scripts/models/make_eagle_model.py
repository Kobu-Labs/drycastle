#!/usr/bin/env python3
"""Builds the Eagle model (#488): an original, simple low-poly white-headed eagle, the gold Companion of the Hunter's Log (GLOSSARY "Companion"). Geometry is code (numpy),
no third-party mesh or texture. The helpers and the exporter are those of scripts/make_phoenix_model.py.

    python3 scripts/make_eagle_model.py [out.glb]      default: assets/drycastle-generated-models/eagle.glb (committed, CC0; deterministic)

Frame: metres, +Y up, forward -Z (the game's frame: ModelYawDeg 0). Origin = the body's centre while flying; in `Perched` the clip raises the bird so the origin is the
GROUND under it. Wingspan about 2.3 m, length about 0.85 m (a Companion draws it smaller: companions.json "scale"). Rigid node articulation, no skin:
FlightRoot (bob, pitch) > Head, Tail, WingShoulder_L/R > WingElbow, Foot_L/R.
Clips (names are API; the client sets the loop): Idle (hover, 1.2 s), Cruise (glide with a slow beat, 2.4 s), Fall (a tumbling dive, 0.9 s), Perched (seated, 3 s). All loop.
"""
from pathlib import Path
import json, math, struct, sys
import numpy as np
ROOT=Path(__file__).resolve().parent.parent
OUT=Path(sys.argv[1]) if len(sys.argv)>1 else ROOT/'assets'/'drycastle-generated-models'/'eagle.glb'
PERCH_Y=.24  # how far the Perched clip raises the body centre over the origin (the ground)
MATS=[];PARTS=[];RIG=[]
def material(name,color,metal=0,rough=.75):
    MATS.append({'name':name,'pbrMetallicRoughness':{'baseColorFactor':[*color,1],'metallicFactor':metal,'roughnessFactor':rough}});return len(MATS)-1
brown=material('01 Umber feather',(.20,.125,.075))
dark=material('02 Dark flight feather',(.085,.06,.045))
tan=material('03 Tawny covert',(.36,.25,.15))
white=material('04 White head and tail',(.93,.92,.88))
yellow=material('05 Yellow beak and feet',(.95,.70,.10),rough=.45)
black=material('06 Eye and talon',(.03,.03,.035),rough=.4)

def node(name,parent=None,pos=(0,0,0)):
    RIG.append({'name':name,'parent':parent,'translation':list(pos)})
RIG.append({'name':'FlightRoot','parent':None,'translation':[0,0,0]});node('Head','FlightRoot',(0,.05,-.33));node('Tail','FlightRoot',(0,0,.33))
for s,side in [(-1,'L'),(1,'R')]:
    node('WingShoulder_'+side,'FlightRoot',(s*.09,.04,-.08))
    node('WingElbow_'+side,'WingShoulder_'+side,(s*.42,0,.02))
    node('Foot_'+side,'FlightRoot',(s*.045,-.08,.10))

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

# body: a tapered keel, brown above and darker below; a white neck into the head
contour_loft('Torso',[(-.30,.02,.05,.055),(-.18,0,.10,.10),(0,-.01,.12,.11),(.20,-.01,.09,.08),(.36,0,.035,.03)],brown,ventral=dark)
contour_loft('Neck',[(-.36,.05,.05,.05),(-.26,.03,.078,.078),(-.17,0,.098,.098)],white)
feather('Back_ridge',[(0,.10,-.16),(0,.11,0),(0,.085,.16),(0,.04,.32)],.06,tan,brown,thickness=.02)

# head: white skull, a hooked yellow beak, dark eyes under a brow
contour_loft('Skull',[(-.11,0,.035,.04),(-.04,.01,.062,.06),(.05,0,.06,.055),(.12,-.01,.05,.05)],white,'Head')
spike('Beak',(0,-.005,-.10),(0,-.03,-.205),.034,yellow,'Head')
spike('Beak_hook',(0,-.03,-.19),(0,-.075,-.188),.017,yellow,'Head')
for s in [-1,1]:
    ellipsoid('Eye',(s*.052,.02,-.06),(.011,.013,.019),black,'Head',6,4)
    spike('Brow',(s*.05,.04,-.10),(s*.062,.05,-.01),.012,white,'Head')

# tail: a white fan
for i in [-2,-1,0,1,2]:
    feather('Tail_feather',[(i*.012,0,0),(i*.03,0,.10),(i*.055,-.005,.20),(i*.07,-.01,.30)],.045,white,white,'Tail',.012)
feather('Tail_cover',[(0,.03,-.04),(0,.035,.03),(0,.02,.10),(0,.005,.16)],.06,brown,brown,'Tail',.014)

# wings: an inner and an outer section; brown coverts over dark flight feathers, the primaries spread like fingers
for s,side in [(-1,'L'),(1,'R')]:
    shoulder='WingShoulder_'+side;elbow='WingElbow_'+side
    feather('Arm',[(0,0,-.06),(s*.14,.02,-.07),(s*.30,.015,-.05),(s*.44,0,-.02)],.09,brown,brown,shoulder,.035)
    for i in range(6):
        x=.03+i*.07
        feather('Secondary',[(s*x,0,-.05),(s*(x+.01),.01,.08),(s*(x+.03),0,.20),(s*(x+.04),-.01,.30)],.052,dark,dark,shoulder,.014)
    for i in range(5):
        x=.04+i*.08
        feather('Covert',[(s*x,.025,-.07),(s*(x+.02),.035,0),(s*(x+.04),.02,.09),(s*(x+.05),.005,.15)],.047,tan,brown,shoulder,.012)
    feather('Forearm',[(0,0,-.02),(s*.14,.015,-.03),(s*.30,.01,-.02),(s*.45,0,.02)],.075,brown,brown,elbow,.03)
    for i in range(7):
        x=.03+i*.055;endx=.20+i*.075;endz=.40-i*.035
        feather('Primary',[(s*x,0,0),(s*(x+.06),.005,.12),(s*(endx-.04),0,endz*.8),(s*endx,-.01,endz)],.043,dark,dark,elbow,.013)
    for i in range(4):
        x=.04+i*.09
        feather('Forearm_covert',[(s*x,.022,-.04),(s*(x+.03),.03,.02),(s*(x+.055),.018,.09),(s*(x+.07),.004,.14)],.045,tan,brown,elbow,.011)
    # legs: feathered thigh, yellow shin and toes, dark talons
    foot='Foot_'+side
    ellipsoid('Thigh',(0,0,.01),(.03,.055,.05),brown,foot,8,4)
    spike('Shin',(0,-.03,0),(s*.01,-.13,.04),.018,yellow,foot)
    for j in [-1,0,1]:
        a=(s*.01,-.125,.04);b=(s*.01+j*.028,-.155,-.03)
        spike('Toe',a,b,.011,yellow,foot)
        spike('Talon',b,(b[0],-.18,-.005),.008,black,foot)

# Export flat-shaded GLB with node animation, without external dependencies.
def quaternion(x=0,y=0,z=0):
    # Euler XYZ composed qz*qy*qx.
    cx,sx=math.cos(x/2),math.sin(x/2);cy,sy=math.cos(y/2),math.sin(y/2);cz,sz=math.cos(z/2),math.sin(z/2)
    return [sx*cy*cz-cx*sy*sz,cx*sy*cz+sx*cy*sz,cx*cy*sz-sx*sy*cz,cx*cy*cz+sx*sy*sz]

def pose(t,mode):
    """One frame of a clip at t in 0..1 (every term is a whole number of cycles, so the clip loops). Rotations are node-local; wing z: + lifts the right wing (s=+1), - lets it droop."""
    phase=math.tau*t;p={}
    if mode=='Fall':
        p['FlightRoot']={'translation':[0,0,0],'rotation':quaternion(x=-1.05+.08*math.sin(phase*2),z=.30*math.sin(phase))}
        p['Head']={'rotation':quaternion(x=.40+.10*math.sin(phase*2),y=.18*math.sin(phase))}
        p['Tail']={'rotation':quaternion(x=.25*math.sin(phase*2-.5),y=.30*math.cos(phase))}
        for s,side in [(-1,'L'),(1,'R')]:
            o=0 if s>0 else 1.9
            p['WingShoulder_'+side]={'rotation':quaternion(y=s*.05,z=s*(.95+.40*math.sin(phase*2+o)))}
            p['WingElbow_'+side]={'rotation':quaternion(z=s*(-.75+.30*math.sin(phase*2+o-.7)))}
            p['Foot_'+side]={'rotation':quaternion(x=.55+.10*math.sin(phase*2+o))}
        return p
    if mode=='Perched':
        # seated on the ground: chest up, wings folded down along the body, slow breathing, the head looks about
        p['FlightRoot']={'translation':[0,PERCH_Y+.008*math.sin(phase),0],'rotation':quaternion(x=.55+.01*math.sin(phase))}
        p['Head']={'rotation':quaternion(x=-.55+.03*math.sin(phase-.4),y=.35*math.sin(phase))}
        p['Tail']={'rotation':quaternion(x=-.30+.02*math.sin(phase-.8))}
        for s,side in [(-1,'L'),(1,'R')]:
            p['WingShoulder_'+side]={'rotation':quaternion(y=s*1.15,z=s*(-.55+.02*math.sin(phase)))}
            p['WingElbow_'+side]={'rotation':quaternion(y=s*-1.9,z=s*.10)}
            p['Foot_'+side]={'rotation':quaternion(x=-.55)}
        return p
    hover=mode=='Idle'
    p['FlightRoot']={'translation':[0,(.04*math.sin(phase*2) if hover else .015*math.sin(phase)),0],'rotation':quaternion(x=(.22+.03*math.cos(phase)) if hover else -.05)}
    p['Head']={'rotation':quaternion(x=(-.22 if hover else 0)-.04*math.cos(phase),y=.06*math.sin(phase))}
    p['Tail']={'rotation':quaternion(x=(-.25 if hover else 0)+.08*math.sin(phase-.6),y=.04*math.cos(phase))}
    for s,side in [(-1,'L'),(1,'R')]:
        p['WingShoulder_'+side]={'rotation':quaternion(x=.05*math.sin(phase),y=s*.045,z=s*(.08+(.62 if hover else .14)*math.sin(phase)))}
        p['WingElbow_'+side]={'rotation':quaternion(y=s*(-.06 if hover else -.015),z=s*(-.08+(.34 if hover else .13)*math.sin(phase-.75)))}
        p['Foot_'+side]={'rotation':quaternion(x=(-.20 if hover else .55)+.04*math.sin(phase))}
    return p

CLIPS=[('Idle',1.2),('Cruise',2.4),('Fall',.9),('Perched',3.0)]

def export():
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
    for mode,duration in CLIPS:
        frames=65;times=np.linspace(0,duration,frames);poses=[pose(i/(frames-1),mode) for i in range(frames)];time_id=acc(times,'SCALAR');channels=[];samplers=[]
        for name,tracks in poses[0].items():
            for path in tracks:
                values=[p[name][path] for p in poses]
                samplers.append({'input':time_id,'output':acc(values,'VEC4' if path=='rotation' else 'VEC3'),'interpolation':'LINEAR'})
                channels.append({'sampler':len(samplers)-1,'target':{'node':ids[name],'path':path}})
        animations.append({'name':mode,'samplers':samplers,'channels':channels})
    doc={'asset':{'version':'2.0','generator':'Original procedural low-poly eagle'},'scene':0,'scenes':[{'name':'Eagle','nodes':[0]}],'nodes':nodes,'meshes':meshes,'materials':MATS,'animations':animations,'buffers':[{'byteLength':len(blob)}],'bufferViews':views,'accessors':access}
    js=json.dumps(doc,separators=(',',':')).encode();js+=b' '*((-len(js))%4);blob+=b'\0'*((-len(blob))%4)
    data=struct.pack('<III',0x46546c67,2,28+len(js)+len(blob))+struct.pack('<II',len(js),0x4e4f534a)+js+struct.pack('<II',len(blob),0x004e4942)+blob
    OUT.parent.mkdir(parents=True,exist_ok=True);OUT.write_bytes(data)
    print(json.dumps({'triangles':triangles,'mesh_parts':len(meshes),'materials':len(MATS),'bytes':len(data),'clips':dict(CLIPS),'out':str(OUT)}))

if __name__=='__main__':export()
