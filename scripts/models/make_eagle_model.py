#!/usr/bin/env python3
"""Builds the Eagle model (#488): an original, simple low-poly white-headed eagle, the gold Companion of the Hunter's Log (GLOSSARY "Companion"). Geometry is code (numpy),
no third-party mesh or texture. The helpers and the exporter are scripts/lib/birdkit.py's, shared with the phoenix script.

    python3 scripts/models/make_eagle_model.py [out.glb]      default: assets/drycastle-generated-models/eagle.glb (committed, CC0; deterministic)

Frame: metres, +Y up, forward -Z (the game's frame: ModelYawDeg 0). Origin = the body's centre while flying; in `Perched` the clip raises the bird so the origin is the
GROUND under it. Wingspan about 2.3 m, length about 0.85 m (a Companion draws it smaller: companions.json "scale"). Rigid node articulation, no skin:
FlightRoot (bob, pitch) > Head, Tail, WingShoulder_L/R > WingElbow, Foot_L/R.
Clips (names are API; the client sets the loop): Idle (hover, 1.2 s), Cruise (glide with a slow beat, 2.4 s), Fall (a tumbling dive, 0.9 s), Perched (seated, 3 s). All loop.
"""
from pathlib import Path
import math, sys
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'lib'))
from birdkit import MATS, RIG, contour_loft, ellipsoid, export, feather, node, quaternion, spike
ROOT=Path(__file__).resolve().parents[2]
OUT=Path(sys.argv[1]) if len(sys.argv)>1 else ROOT/'assets'/'drycastle-generated-models'/'eagle.glb'
PERCH_Y=.24  # how far the Perched clip raises the body centre over the origin (the ground)
def material(name,color,metal=0,rough=.75):
    MATS.append({'name':name,'pbrMetallicRoughness':{'baseColorFactor':[*color,1],'metallicFactor':metal,'roughnessFactor':rough}});return len(MATS)-1
brown=material('01 Umber feather',(.20,.125,.075))
dark=material('02 Dark flight feather',(.085,.06,.045))
tan=material('03 Tawny covert',(.36,.25,.15))
white=material('04 White head and tail',(.93,.92,.88))
yellow=material('05 Yellow beak and feet',(.95,.70,.10),rough=.45)
black=material('06 Eye and talon',(.03,.03,.035),rough=.4)

RIG.append({'name':'FlightRoot','parent':None,'translation':[0,0,0]});node('Head','FlightRoot',(0,.05,-.33));node('Tail','FlightRoot',(0,0,.33))
for s,side in [(-1,'L'),(1,'R')]:
    node('WingShoulder_'+side,'FlightRoot',(s*.09,.04,-.08))
    node('WingElbow_'+side,'WingShoulder_'+side,(s*.42,0,.02))
    node('Foot_'+side,'FlightRoot',(s*.045,-.08,.10))

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

if __name__=='__main__':export(OUT,pose,CLIPS,'Eagle','Original procedural low-poly eagle')
