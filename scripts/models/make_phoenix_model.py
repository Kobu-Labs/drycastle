#!/usr/bin/env python3
"""Builds the Phoenix model (#396): an original low-poly firebird, a rare night flyer (GLOSSARY "Phoenix"). Geometry is code (numpy), no third-party mesh or texture.

    python3 scripts/make_phoenix_model.py [out.glb]      default: assets/drycastle-generated-models/phoenix.glb (committed, CC0; deterministic)

Frame: metres, +Y up, forward -Z (the game's frame: ModelYawDeg 0). Origin = the body's centre while flying; in `Perched` the clip lowers the bird so the origin is the
GROUND under it (belly ~0.1 m up). Rigid node articulation, no skin: FlightRoot (bob, pitch) > Head, Tail, WingShoulder_L/R > WingElbow > WingTip, Foot_L/R.
Clips (names are API; the client sets the loop): Idle (hover, 1.35 s, loops), Cruise (glide, 2.4 s, loops), Fall (shot down, a tumbling dive, 0.9 s, loops),
Perched (crashed, seated and beaten but breathing, 3 s, loops).
Emissive: KHR_materials_emissive_strength. The game's bloom threshold is HDR 2 (ADR 0030): the feathers are authored at 3-5 so the whole bird blooms like a coal in the night sky, the flame and eyes at 8-10.
"""
from pathlib import Path
import json, math, struct, sys
import numpy as np
ROOT=Path(__file__).resolve().parent.parent
OUT=Path(sys.argv[1]) if len(sys.argv)>1 else ROOT/'assets'/'drycastle-generated-models'/'phoenix.glb'
BODY_Y=1.65  # the authored body centre height; FlightRoot sits at -BODY_Y so the origin is the body centre
MATS=[];PARTS=[];RIG=[]
def material(name,color,emission=None,metal=0,rough=.65,strength=1):
    d={'name':name,'pbrMetallicRoughness':{'baseColorFactor':[*color,1],'metallicFactor':metal,'roughnessFactor':rough}}
    if emission:
        d['emissiveFactor']=emission
        if strength!=1:d['extensions']={'KHR_materials_emissive_strength':{'emissiveStrength':strength}}
    MATS.append(d);return len(MATS)-1
wine=material('01 Obsidian plum',(.065,.018,.045),metal=.15)
red=material('02 Smoldering crimson',(.28,.018,.032),emission=[.28,.018,.032],strength=12)
orange=material('03 Vermilion feather',(.92,.115,.025),emission=[.92,.115,.025],strength=5)
copper=material('04 Copper shadow',(.42,.095,.018),emission=[.42,.095,.018],strength=8,metal=.3)
gold=material('05 Marigold feather',(.78,.30,.018),emission=[.78,.30,.018],strength=5,metal=.12)
sun=material('06 Sunlit feather',(.95,.52,.055),emission=[.95,.52,.055],strength=6)
ivory=material('07 Living flame',(1,.75,.25),emission=[1,.4,.06],strength=8)
eye=material('08 White gold eyes',(1,.98,.7),emission=[1,1,.6],strength=10)
beak=material('09 Burnished beak',(.86,.48,.09),metal=.55,rough=.3)
coal=material('10 Basalt talons',(.055,.045,.07),metal=.2)

def node(name,parent=None,pos=(0,0,0)):
    RIG.append({'name':name,'parent':parent,'translation':list(pos)})
RIG.append({'name':'FlightRoot','parent':None,'translation':[0,-BODY_Y,0]});node('Head','FlightRoot',(0,2.06,-.82));node('Tail','FlightRoot',(0,1.57,.55))
for s,side in [(-1,'L'),(1,'R')]:
    node('WingShoulder_'+side,'FlightRoot',(s*.18,1.72,-.30))
    node('WingElbow_'+side,'WingShoulder_'+side,(s*.66,.025,.10))
    node('WingTip_'+side,'WingElbow_'+side,(s*.59,0,.10))
    node('Foot_'+side,'FlightRoot',(s*.105,1.46,.15))

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

def feather(name,points,width,base=gold,tip=red,parent='FlightRoot',thickness=.025):
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

# A shallow, long, angular keel replaces the spherical belly. Neck arcs forward.
contour_loft('Tapered_raptor_torso',[(-.66,1.76,.075,.09),(-.40,1.70,.175,.15),(-.05,1.61,.195,.16),(.29,1.58,.145,.115),(.60,1.59,.055,.055)],red,ventral=wine)
contour_loft('Swept_neck',[(-.91,2.10,.065,.070),(-.78,1.98,.075,.09),(-.64,1.82,.085,.12),(-.44,1.73,.12,.105)],orange,ventral=copper)
# Narrow overlapping vanes trace the keel and swept nape rather than a round bib.
for s in [-1,1]:
    for i in range(5):
        y=1.98-i*.055;z=-.77+i*.09
        feather('Nape_lance',[(s*.055,y,z),(s*.095,y+.025,z+.10),(s*.12,y+.04,z+.23),(s*.095,y+.08,z+.39)],.032,gold,red,thickness=.012)
    feather('Keel_flame_inlay',[(s*.055,1.82,-.63),(s*.09,1.71,-.44),(s*.115,1.56,-.13),(s*.08,1.51,.13)],.023,sun,orange,thickness=.01)
    for i in range(4):
        z=-.30+i*.17
        feather('Scapular_lance',[(s*.08,1.80-i*.035,z),(s*.14,1.81-i*.035,z+.11),(s*.12,1.75-i*.035,z+.27),(s*.055,1.65-i*.025,z+.43)],.05,copper,red,thickness=.018)
feather('Dorsal_gold_ridge',[(0,1.86,-.40),(0,1.82,-.12),(0,1.73,.25),(0,1.66,.60)],.035,gold,orange,thickness=.024)

# Narrow falcon skull, low brow and a compact hooked beak. No upright comb.
contour_loft('Falcon_skull',[(-.29,.075,.052,.055),(-.17,.105,.115,.095),(.01,.11,.13,.095),(.20,.13,.045,.05)],gold,'Head',ventral=copper)
spike('Hooked_beak',(0,.055,-.28),(0,.005,-.445),.063,beak,'Head')
spike('Beak_tip',(0,.005,-.405),(0,-.085,-.410),.033,coal,'Head')
for s in [-1,1]:
    ellipsoid('Eye_mask',(s*.117,.115,-.135),(.027,.034,.095),wine,'Head',8,4)
    ellipsoid('Eye_slit',(s*.143,.119,-.15),(.009,.012,.047),eye,'Head',8,4)
    spike('Raptor_brow',(s*.135,.132,-.22),(s*.153,.165,-.035),.018,sun,'Head')
    feather('Swept_cheek',[(s*.09,.035,-.12),(s*.14,.025,.04),(s*.18,.07,.22),(s*.15,.17,.48)],.033,orange,red,'Head',.012)
    feather('Cheek_gold_edge',[(s*.12,.05,-.10),(s*.16,.05,.05),(s*.19,.10,.23),(s*.15,.17,.44)],.010,sun,gold,'Head',.006)
for i in [-1,0,1]:
    x=i*.045;length=.76-.12*abs(i)
    feather('Swept_flame_crown',[(x,.19,.02),(x*1.4,.245,.19),(x*1.7,.29,.43),(x*1.9,.39-.035*abs(i),length)],.030,orange,red,'Head',.017)
    feather('Crown_gold_inlay',[(x,.208,.02),(x*1.4,.263,.19),(x*1.7,.308,.43),(x*1.9,.39-.035*abs(i),length-.045)],.011,sun,gold,'Head',.006)
# Ember-heart: a small luminous centerpiece on the breast.
ellipsoid('Heart_ember',(0,1.80,-.678),(.022,.045,.018),ivory,segments=6,rings=4)

# Three articulated sections per wing. Every feather is a closed thick solid.
for s,side in [(-1,'L'),(1,'R')]:
    shoulder='WingShoulder_'+side;elbow='WingElbow_'+side;tipnode='WingTip_'+side
    ellipsoid('Shoulder_mantle',(s*.10,.015,.05),(.22,.075,.18),red,shoulder,10,4)
    feather('Arm_web',[(0,0,-.04),(s*.26,.045,.03),(s*.55,.02,.10),(s*.75,0,.22)],.22,orange,red,shoulder,.055)
    # Rounded secondary feathers make a scalloped trailing edge.
    for i in range(8):
        x=.09+i*.078;length=.54+.14*math.sin(i*.40)
        feather('Secondary',[(s*x,-.018,.05),(s*(x+.02),.015,.24),(s*(x+.12),-.035,length*.80),(s*(x+.14),-.09,length+.12)],.079,orange,red,shoulder,.025)
    for i in range(6):
        x=.04+i*.105
        feather('Shoulder_covert',[(s*x,.075,-.055),(s*(x+.06),.105,.08),(s*(x+.11),.065,.25),(s*(x+.13),.02,.35)],.065,gold,orange,shoulder,.018)
    feather('Forearm_web',[(0,0,0),(s*.22,.03,.01),(s*.47,.02,.06),(s*.68,-.015,.13)],.19,orange,red,elbow,.045)
    # Strong fingered primaries in a swept fan; crimson terminal section.
    for i in range(7):
        x=.055+i*.08;endx=.28+i*.115;endz=.94-i*.077
        feather('Primary',[(s*x,-.02,.07),(s*(x+.13),.005,.29),(s*(endx-.08),-.025,endz*.82),(s*endx,-.07,endz)],.088,orange,red,elbow,.025)
        feather('Golden_primary_inlay',[(s*x,.017,.08),(s*(x+.12),.040,.25),(s*(endx-.10),.012,endz*.64),(s*(endx-.055),-.012,endz*.78)],.020,sun,orange,elbow,.009)
    for i in range(4):
        x=.06+i*.13
        feather('Forearm_covert',[(s*x,.07,-.05),(s*(x+.055),.10,.04),(s*(x+.10),.07,.19),(s*(x+.14),.025,.29)],.073,gold,orange,elbow,.02)
    for i in range(4):
        feather('Wingtip_flame',[(0,0,.03),(s*(.13+i*.025),.035,.05+i*.10),(s*(.30+i*.045),.055,.11+i*.17),(s*(.41+i*.025),.12,.10+i*.23)],.065,gold,red,tipnode,.025)
    # Bright leading edge, which remains visible in silhouette.
    feather('Leading_edge',[(0,.085,-.075),(s*.24,.115,-.08),(s*.48,.08,-.045),(s*.65,.03,.005)],.025,ivory,gold,shoulder,.02)
    feather('Leading_edge',[(0,.065,-.055),(s*.22,.085,-.065),(s*.45,.06,-.03),(s*.62,.015,.05)],.042,sun,gold,elbow,.018)
    # Folded legs and curled, dark claws visible below the belly.
    foot='Foot_'+side
    ellipsoid('Thigh',(0,.01,.025),(.045,.09,.085),red,foot,8,4)
    spike('Shin',(0,-.05,0),(s*.025,-.25,.08),.032,beak,foot)
    for j in [-1,0,1]:
        a=(s*.025,-.24,.08);b=(s*.025+j*.043,-.29,-.03)
        spike('Toe',a,b,.018,beak,foot)
        spike('Curled_claw',b,(b[0],-.32,.022),.014,coal,foot)

# Long ornamental tail: five layered, curved fire ribbons with bright cores.
for i in [-2,-1,0,1,2]:
    length=2.40-.22*abs(i)
    points=[(i*.032,0,0),(i*.10,-.03,.35),(i*.18,-.17,.85),(i*.22,-.12,1.35),(i*.21,.06,length*.85),(i*.18,.20+.04*abs(i),length)]
    feather('Tail_streamer',points,.058 if i==0 else .043,orange,red,'Tail',.018)
    inlay=[(p[0],p[1]+.021,p[2]) for p in points]
    feather('Tail_gold_core',inlay,.019 if i==0 else .014,sun,orange,'Tail',.008)
    end=points[-1]
    spike('Tail_ember_tip',(end[0],end[1],end[2]-.10),(end[0],end[1]+.07,end[2]+.13),.022,ivory,'Tail')
for i in range(3):
    feather('Tail_cover',[(0,.08,i*.08),(0,.08,.16+i*.09),(0,.02,.35+i*.12),(0,-.025,.48+i*.13)],.07,copper,orange,'Tail',.015)

# Export flat-shaded GLB with node animation, without external dependencies.
def quaternion(x=0,y=0,z=0):
    # Euler XYZ composed qz*qy*qx.
    cx,sx=math.cos(x/2),math.sin(x/2);cy,sy=math.cos(y/2),math.sin(y/2);cz,sz=math.cos(z/2),math.sin(z/2)
    return [sx*cy*cz-cx*sy*sz,cx*sy*cz+sx*cy*sz,cx*cy*sz-sx*sy*cz,cx*cy*cz+sx*sy*sz]

def pose(t,mode):
    """One frame of a clip at t in 0..1 (every term is a whole number of cycles, so the clip loops). Rotations are node-local; wing z: + lifts the right wing (s=+1), - lets it droop."""
    phase=math.tau*t;p={}
    if mode=='Fall':
        # shot down: steep nose-down dive, rolling, wings half-folded and beating wildly and out of step, feet reaching, tail streaming
        p['FlightRoot']={'translation':[0,-BODY_Y,0],'rotation':quaternion(x=-1.05+.08*math.sin(phase*2),z=.30*math.sin(phase))}
        p['Head']={'rotation':quaternion(x=.40+.10*math.sin(phase*2),y=.18*math.sin(phase))}
        p['Tail']={'rotation':quaternion(x=.25*math.sin(phase*2-.5),y=.30*math.cos(phase))}
        for s,side in [(-1,'L'),(1,'R')]:
            o=0 if s>0 else 1.9
            p['WingShoulder_'+side]={'rotation':quaternion(y=s*.05,z=s*(.95+.40*math.sin(phase*2+o)))}
            p['WingElbow_'+side]={'rotation':quaternion(z=s*(-.75+.30*math.sin(phase*2+o-.7)))}
            p['WingTip_'+side]={'rotation':quaternion(z=s*(.35+.25*math.sin(phase*2+o-1.3)))}
            p['Foot_'+side]={'rotation':quaternion(x=.55+.10*math.sin(phase*2+o))}
        return p
    if mode=='Perched':
        # crashed and seated, beaten but alive: belly near the ground, chest up, head hung low and swaying, wings drooped, feet tucked, slow breathing, now and then a twitch
        p['FlightRoot']={'translation':[0,-BODY_Y+.30+.018*math.sin(phase),0],'rotation':quaternion(x=.42+.012*math.sin(phase))}
        p['Head']={'rotation':quaternion(x=-.90+.06*math.sin(phase-.4),y=.12*math.sin(phase))}
        p['Tail']={'rotation':quaternion(x=-.34+.02*math.sin(phase-.8),y=.03*math.sin(phase))}
        for s,side in [(-1,'L'),(1,'R')]:
            p['WingShoulder_'+side]={'rotation':quaternion(y=s*.35,z=s*(-.32+.025*math.sin(phase+(0 if s>0 else .8))))}
            p['WingElbow_'+side]={'rotation':quaternion(z=s*(-.22+.03*math.sin(phase-.5)))}
            p['WingTip_'+side]={'rotation':quaternion(z=s*(.30+.04*math.sin(phase-.9)))}
            p['Foot_'+side]={'rotation':quaternion(x=1.15)}
        return p
    hover=mode=='Idle'
    p['FlightRoot']={'translation':[0,-BODY_Y+(.065*math.sin(phase*2) if hover else .025*math.sin(phase)),0],'rotation':quaternion(x=.035*math.cos(phase) if hover else -.075)}
    p['Head']={'rotation':quaternion(x=-.04*math.cos(phase),y=.055*math.sin(phase))}
    p['Tail']={'rotation':quaternion(x=.09*math.sin(phase-.6),y=.055*math.cos(phase))}
    for s,side in [(-1,'L'),(1,'R')]:
        p['WingShoulder_'+side]={'rotation':quaternion(x=.05*math.sin(phase),y=s*.045,z=s*(.08+(.67 if hover else .17)*math.sin(phase)))}
        p['WingElbow_'+side]={'rotation':quaternion(y=s*(-.06 if hover else -.015),z=s*(-.08+(.24 if hover else .11)*math.sin(phase-.65)))}
        p['WingTip_'+side]={'rotation':quaternion(z=s*(.12+(.19 if hover else .09)*math.sin(phase-1.0)))}
        p['Foot_'+side]={'rotation':quaternion(x=(-.22 if hover else .40)+.045*math.sin(phase))}
    return p

CLIPS=[('Idle',1.35),('Cruise',2.4),('Fall',.9),('Perched',3.0)]

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
    doc={'extensionsUsed':['KHR_materials_emissive_strength'],'asset':{'version':'2.0','generator':'Original procedural low-poly phoenix'},'scene':0,'scenes':[{'name':'Phoenix','nodes':[0]}],'nodes':nodes,'meshes':meshes,'materials':MATS,'animations':animations,'buffers':[{'byteLength':len(blob)}],'bufferViews':views,'accessors':access}
    js=json.dumps(doc,separators=(',',':')).encode();js+=b' '*((-len(js))%4);blob+=b'\0'*((-len(blob))%4)
    data=struct.pack('<III',0x46546c67,2,28+len(js)+len(blob))+struct.pack('<II',len(js),0x4e4f534a)+js+struct.pack('<II',len(blob),0x004e4942)+blob
    OUT.parent.mkdir(parents=True,exist_ok=True);OUT.write_bytes(data)
    print(json.dumps({'triangles':triangles,'mesh_parts':len(meshes),'materials':len(MATS),'bytes':len(data),'clips':dict(CLIPS),'out':str(OUT)}))

if __name__=='__main__':export()
