#!/usr/bin/env python3
"""Builds assets/drycastle-generated-models/golf_mk2.glb: a low-poly three-door Golf Mk2 (vehicles.json id golf_mk2), black.

Original geometry written as code (no third-party model, no texture); flat shading, explicit normals. Needs numpy (the other model
scripts are standard library only); it uses scripts/lib/glass.py's GlassSet:

    python3 scripts/models/make_golf_mk2_model.py [out.glb]       (no -I: numpy may live in the user site)

Without an output path it also rewrites the `glass` list of golf_mk2 in godot/shooter/data/vehicles.json (ADR 0008).

Lamp materials carry glTF emissiveFactor ('Emissive warm headlight lens', 'Emissive red tail lamps', 'Emissive amber indicators', 'Emissive reverse lamps');
the client finds them by name and drives their emission from the lamp state (CarLights.BindModelLamps).

SCALE (below) makes the car bigger than a real Golf so a seated rider fits; the numbers in this file are at 1:1 and vehicles.json carries the factor.
Frame: the game's vehicle frame, metres. x right, y up, z back (forward is -Z), origin on the ground halfway between the axles.
So vehicles.json uses model_scale 1, model_yaw_deg 0, model_offset [0, 0, 0]. Left-hand drive: the driver sits at x -0.39.

Node tree (the names the client looks for, DriveNodes.cs and GlassClient.cs):

    golf_mk2
      hull                         body, interior, glazing (the body leans on it over the wheels)
        glass_0 .. glass_5         the breakable panes, one node each, numbered as the `glass` list of vehicles.json (the script
                                   rewrites that list): windscreen, rear, then door and rear quarter light of the left and the right
        seat_driver, seat_passenger_1 .. _3   empties: the four seats of vehicles.json (feet)
        light_head_l/r, light_tail_l/r        empties: the lamp positions of vehicles.json
        steering_wheel_rim, steering_wheel_spokes   (meshes, in hull) the wheel: the cockpit view hides them and draws its own wheel that turns (CockpitRig)
        cargo_door                 the hatch (rear window with its pane glass_1, the lower deck, the rear panel, handle, badge, wiper), its OWN node: origin on the hinge line
                                   (the roof's rear edge, x 0, y 1.40, z 1.03 at 1:1), so a turn about its local -X lifts the lower edge back and up (vehicles.json cargo_volume.doors,
                                   CargoDoorsView). Closed it sits exactly where it sat when it was part of the body; the boot behind it (Boot_floor, Boot_side_closure, the bulkhead) is real.
      wheel_fl_steer, _fr_steer    front steering pivots (turn about Y), origin at the wheel centre; SIBLINGS of the hull: the wheels follow the ground, the body leans over them
        wheel_fl, wheel_fr         front wheels (l = -x), origin at the wheel centre, roll about X
      wheel_rl, wheel_rr           rear wheels, siblings of the hull
"""
import json, math, struct, sys
from pathlib import Path
import numpy as np
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'lib'))
from glass import GlassSet, vehicles_json_path
from meshkit import Node
ROOT=Path(__file__).resolve().parents[2]
OUT=ROOT/'assets'/'drycastle-generated-models'/'golf_mk2.glb'
GLASS=GlassSet('golf_mk2')
_PANES=Node('panes')    # GlassSet hangs its (empty) pane nodes here; the real ones are made by export()
# The whole model is built at 1:1 Golf size below and scaled by SCALE on the way out (vertices, pivots, seats, lamps, glass): the game's
# characters are 1.2 m tall seated, taller than a real Golf's cabin, so the car is made bigger until a seated rider's head is inside the roof.
# vehicles.json carries the same factor in its vehicle-frame numbers (hood, half_*, height, wheelbase, track, seats, lights, camera).
SCALE=1.25
MATS=[]; PARTS=[]
def mat(name,color,metal=0,rough=.7,alpha=1,emission=None):
    m={'name':name,'pbrMetallicRoughness':{'baseColorFactor':[*color,alpha],'metallicFactor':metal,'roughnessFactor':rough},'doubleSided':True}
    if alpha<1:m['alphaMode']='BLEND'
    if emission is not None:m['emissiveFactor']=list(emission)
    MATS.append(m);return len(MATS)-1
paint=mat('Black paint',(.025,.025,.03),.12,.4)  # near-black, a little gloss so flat shading still reads
trim=mat('Black rubber trim',(.022,.027,.032))
tire=mat('Tire rubber',(.016,.019,.023),0,.95)
alloy=mat('Brushed alloy',(.58,.64,.69),.72,.3)
glass=mat('Transparent blue glass',(.38,.62,.70),.05,.16,.19)
seat=mat('Charcoal cloth',(.085,.105,.12),0,.97)
seam=mat('Cloth inserts',(.15,.18,.20))
dash=mat('Dashboard graphite',(.045,.055,.065))
light=mat('Emissive warm headlight lens',(.75,.85,.87),.25,.23,emission=(1,.85,.62))
red=mat('Non-emissive red grille trim',(.62,.025,.035),.15,.3)
white=mat('Plate ivory',(.86,.89,.85))
tail_light=mat('Emissive red tail lamps',(.62,.025,.035),.15,.3,emission=(.5,.008,.012))
indicator_light=mat('Emissive amber indicators',(.95,.39,.045),.15,.35,emission=(.8,.18,.008))
reverse_light=mat('Emissive reverse lamps',(.86,.89,.85),.1,.3,emission=(.65,.68,.6))
blue=mat('Plate blue band',(.025,.12,.48))
black=mat('Dark grille',(.008,.012,.016))
gauge=mat('Vintage instrument markings',(.54,.65,.49))
def mesh(name,verts,faces,material,parent=None,gnode=None,gname=None):
    PARTS.append(dict(name=name,v=np.array(verts,dtype=float),f=faces,mat=material,parent=parent,gnode=gnode,gname=gname));return len(PARTS)-1
def poly(name,pts,m):return mesh(name,pts,[list(range(len(pts)))],m)
def glass_pane(pane_id,pts):
    """A breakable pane: its own node glass_N (N = its place in the glass list of vehicles.json), outline from its corners."""
    n=len(GLASS.panes);GLASS.pane(_PANES,pane_id,[],[tuple(float(v)*SCALE for v in p) for p in pts])
    mesh(f'glass_{n}',pts,[list(range(len(pts)))],glass,gnode=n)
def box(name,c,size,m,gname=None):
    c=np.array(c); s=np.array(size)/2
    v=[c+s*np.array(p) for p in [(-1,-1,-1),(1,-1,-1),(1,1,-1),(-1,1,-1),(-1,-1,1),(1,-1,1),(1,1,1),(-1,1,1)]]
    return mesh(name,v,[[0,3,2,1],[4,5,6,7],[0,1,5,4],[3,7,6,2],[0,4,7,3],[1,2,6,5]],m,gname=gname)
def beam(name,a,b,width,m,depth=None,gname=None):
    a=np.array(a,float);b=np.array(b,float); d=b-a;d/=np.linalg.norm(d)
    u=np.cross(d,[0,1,0] if abs(d[1])<.95 else [1,0,0]);u/=np.linalg.norm(u);u*=width/2
    v=np.cross(d,u);v=v/np.linalg.norm(v)*(depth or width)/2
    return mesh(name,[p+su*u+sv*v for p in (a,b) for su,sv in [(-1,-1),(1,-1),(1,1),(-1,1)]],[[0,3,2,1],[4,5,6,7],[0,1,5,4],[1,2,6,5],[2,3,7,6],[3,0,4,7]],m,gname=gname)
def ring(name,c,ro,ri,depth,m,n=16,axis='x',gname=None):
    v=[]
    for d,r in [(-depth/2,ro),(depth/2,ro),(-depth/2,ri),(depth/2,ri)]:
        for i in range(n):
            a=2*math.pi*i/n; p=[d,r*math.cos(a),r*math.sin(a)]
            if axis=='z':p=[p[1],p[2],p[0]]
            v.append(np.array(c)+p)
    f=[]
    for i in range(n):
        j=(i+1)%n
        for a,b in [(0,n),(2*n,0),(n,3*n),(3*n,2*n)]:f.append([a+i,a+j,b+j,b+i])
    return mesh(name,v,f,m,gname=gname)
# Mk2-specific three-door shell, short nose and upright hatch.
stations=[(-1.99,.755,.90),(-1.82,.80,.915),(-1.55,.82,.925),(-.78,.82,.94),(.70,.82,.94),(1.63,.81,.94),(1.99,.76,.89)]
def shape(z):
    for (za,xa,ya),(zb,xb,yb) in zip(stations,stations[1:]):
        if za<=z<=zb:
            t=(z-za)/(zb-za);return xa+(xb-xa)*t,ya+(yb-ya)*t
    return stations[-1][1:]
def disk(name,c,r,m,n=20,axis='z'):
    pts=[]
    for i in range(n):
        a=math.tau*i/n
        d=[0,r*math.cos(a),r*math.sin(a)] if axis=='x' else [r*math.cos(a),r*math.sin(a),0]
        pts.append(np.array(c)+d)
    return poly(name,pts,m)
zs=sorted(set([s[0] for s in stations]+[round(w+.345*math.cos(math.pi*i/12),6) for w in [-1.235,1.235] for i in range(13)]))
for s in [-1,1]:
    for za,zb in zip(zs,zs[1:]):
        row=[]
        for z in [za,zb]:
            x,y=shape(z);low=.235
            for w in [-1.235,1.235]:
                if abs(z-w)<=.345001:low=max(low,.315+math.sqrt(max(0,.345**2-(z-w)**2)))
            row.append([(s*(x-.015),low,z),(s*x,.74,z),(s*(x-.018),y,z)])
        for k in range(2):poly('Body_side',[row[0][k],row[1][k],row[1][k+1],row[0][k+1]],paint)
    for w in [-1.235,1.235]:
        for i in range(12):
            a=math.pi*i/12;b=math.pi*(i+1)/12
            pts=[]
            for r,t in [(.345,a),(.345,b),(.392,b),(.392,a)]:
                z=w+r*math.cos(t);x,_=shape(z);pts.append((s*(x+.018),.315+r*math.sin(t),z))
            poly('Black_arch_extension',pts,trim)
        # Closed inner wall plus curved barrel: the tire opening stays open
        # to the road, while views into the cabin/engine bay are blocked.
        inner=.43 if w<0 else .58  # Front wheels need room at full steering lock.
        arc=[(.315+.345*math.sin(math.pi*i/12),w+.345*math.cos(math.pi*i/12)) for i in range(13)]
        poly('Wheelhouse_inner_wall',[(s*inner,.20,w-.345),(s*inner,.20,w+.345)]+[(s*inner,y,z) for y,z in arc],black)
        for (ya,za),(yb,zb) in zip(arc,arc[1:]):
            xa=shape(za)[0]+.018;xb=shape(zb)[0]+.018
            poly('Curved_wheelhouse_liner',[(s*inner,ya,za),(s*xa,ya,za),(s*xb,yb,zb),(s*inner,yb,zb)],black)
        for z in [w-.345,w+.345]:
            x=shape(z)[0]+.018
            poly('Wheelhouse_end_closure',[(s*inner,.20,z),(s*x,.20,z),(s*x,.315,z),(s*inner,.315,z)],black)
    for a,b in [(-1.94,-1.59),(-.88,.88),(1.59,1.94)]:
        xa,_=shape(a);xb,_=shape(b)
        beam('Side_moulding',(s*(xa+.015),.63,a),(s*(xb+.015),.63,b),.075,trim,.028)
    beam('Black_sill',(s*.81,.25,-.88),(s*.81,.25,.88),.065,trim)
    beam('Shoulder_crease',(s*.824,.88,-1.85),(s*.824,.88,1.66),.009,trim)
# A shallow creased hood and real hollow cabin.
for a,b in [(-1.99,-1.82),(-1.82,-1.55),(-1.55,-.78),(1.63,1.99)]:
    xa,ya=shape(a);xb,yb=shape(b)
    mesh('Bonnet' if a<0 else 'Hatch_lower_deck',[(-xa+.018,ya,a),(0,ya+.014,a),(xa-.018,ya,a),(-xb+.018,yb,b),(0,yb+.014,b),(xb-.018,yb,b)],[[0,1,4,3],[1,2,5,4]],paint)
# Continuous center undertray and a wider passenger floor between the wheels.
# Rear corners are occupied by wheel housings rather than a floor cutting tires.
box('Continuous_undertray',(0,.215,0),(.86,.05,3.98),black)
box('Front_undertray_crossmember',(0,.215,-1.79),(1.51,.05,.40),black)
box('Rear_undertray',(0,.215,1.27),(1.16,.05,1.44),black)
box('Cabin_floor',(0,.265,.025),(1.62,.065,1.69),dash)
box('Rear_center_floor',(0,.265,1.265),(1.16,.065,.85),dash)
box('Boot_floor',(0,.265,1.795),(1.54,.065,.39),dash)
box('Engine_bay_bulkhead',(0,.6025,-.79),(1.60,.695,.045),dash)
box('Rear_cabin_bulkhead',(0,.60,1.65),(1.54,.70,.045),dash)
for s in [-1,1]:
    box('Inner_sill_and_door_shell',(s*.795,.595,.025),(.04,.70,1.69),dash)
    box('Rear_inner_quarter_upper',(s*.785,.80,1.235),(.045,.32,.84),dash)
    box('Boot_side_closure',(s*.755,.59,1.81),(.045,.68,.36),dash)
for z in [-1.99,1.99]:
    x,y=shape(z)
    poly('End_panel',[(-x,.24,z),(x,.24,z),(x,y,z),(0,y+.014,z),(-x,y,z)],paint)
    box('Black_big_bumper',(0,.445,z+(-.04 if z<0 else .04)),(1.65,.255,.18),trim)
    box('Bumper_edge',(0,.578,z+(-.04 if z<0 else .04)),(1.65,.025,.185),black)
    box('Lower_valance',(0,.275,z),(1.49,.11,.09),trim)
# Low, flush pillar frames. The three-door front windows are noticeably longer.
mesh('Roof',[(-.66,1.405,-.29),(0,1.42,-.29),(.66,1.405,-.29),(-.66,1.405,1.02),(0,1.42,1.02),(.66,1.405,1.02)],[[0,1,4,3],[1,2,5,4]],paint)
front=[(-.785,.95,-.77),(.785,.95,-.77),(.65,1.39,-.29),(-.65,1.39,-.29)]
rear=[(-.65,1.39,1.04),(.65,1.39,1.04),(.78,.98,1.72),(-.78,.98,1.72)]
glass_pane('windscreen',front);glass_pane('rear',rear)
poly('Rear_window_lower_surround',[rear[3],rear[2],(.79,.925,1.76),(-.79,.925,1.76)],paint)
poly('Rear_roof_window_join',[(-.66,1.405,1.02),(.66,1.405,1.02),rear[1],rear[0]],trim)
for name,pts in [('Windshield',front),('Rear_window',rear)]:
    for a,b in zip(pts,pts[1:]+pts[:1]):beam(name+'_rubber',a,b,.027,trim)
for s in [-1,1]:
    def sx(y):return s*(.80-(y-.94)*.14/.465)
    p=[(s*.80,.94,-.78),(s*.66,1.405,-.29),(s*.66,1.405,1.02),(s*.80,.94,1.76)]
    for a,b in [(p[0],p[1]),(p[2],p[3])]:beam('Flush_pillar',a,b,.066 if a==p[0] else .115,paint)
    beam('Roof_gutter',p[1],p[2],.023,trim)
    beam('Window_belt',p[0],p[3],.035,trim)
    win=[(sx(.965),.965,-.72),(sx(1.365),1.365,-.26),(sx(1.365),1.365,.62),(sx(.965),.965,.62)]
    back=[(sx(.965),.965,.70),(sx(1.365),1.365,.70),(sx(1.365),1.365,1.00),(sx(.965),.965,1.63)]
    side='left' if s<0 else 'right'
    glass_pane(f'door_{side}',win);glass_pane(f'quarter_{side}',back)
    # Fill all four edges of each aperture; thin edge beams alone leave slits.
    front_outer=[(sx(.94),.94,-.78),(sx(1.405),1.405,-.29),(sx(1.405),1.405,.66),(sx(.94),.94,.66)]
    rear_outer=[(sx(.94),.94,.66),(sx(1.405),1.405,.66),(sx(1.405),1.405,1.02),(sx(.94),.94,1.76)]
    for inner,outer in [(win,front_outer),(back,rear_outer)]:
        for i in range(4):
            j=(i+1)%4
            poly('Solid_window_aperture_surround',[outer[i],outer[j],inner[j],inner[i]],trim)
    beam('B_pillar',(sx(.94),.94,.66),(sx(1.40),1.40,.66),.075,trim)
    # Both gaps belong to a single long front door; no rear door handles.
    for z in [-.75,.66]:beam('Door_seam',(s*.827,.29,z),(s*.805,.93,z),.008,black)
    box('Single_door_handle',(s*.837,.845,.49),(.034,.032,.145),trim)
    beam('Mirror_mount',(s*.78,1.00,-.61),(s*.90,1.01,-.62),.036,trim)
    box('Square_black_mirror',(s*.93,1.04,-.61),(.19,.125,.19),trim)
    box('Mirror_reflector',(s*.93,1.04,-.509),(.15,.085,.007),alloy)
    box('Inner_door_card',(s*.735,.64,.06),(.03,.38,1.34),dash)
    box('Inner_armrest',(s*.70,.68,.20),(.09,.06,.37),seat)
    box('Side_indicator',(s*.837,.79,-.99),(.012,.038,.057),indicator_light)
# Silver steel-style wheels, separate roll and steering nodes.
PIVOTS={}
for s in [-1,1]:
    for z,axle in [(-1.235,'Front'),(1.235,'Rear')]:
        center=np.array([s*.71,.307,z]);start=len(PARTS)
        ring('Tire',center,.30,.184,.19,tire,20)
        ring('Sidewall',center+[s*.099,0,0],.281,.184,.014,tire,20)
        ring('Silver_outer_lip',center+[s*.109,0,0],.189,.17,.018,alloy,24)
        ring('Steel_wheel_face',center+[s*.106,0,0],.172,.055,.016,alloy,24)
        for i in range(12):
            a=math.tau*i/12
            disk('Rim_vent',(center[0]+s*.117,.307+.132*math.cos(a),z+.132*math.sin(a)),.014,black,6,'x')
        ring('Hubcap_rim',center+[s*.122,0,0],.085,.066,.015,alloy,20)
        disk('Hubcap_face',center+[s*.132,0,0],.066,alloy,16,'x')
        ring('Hub_stamp',center+[s*.134,0,0],.023,.017,.003,trim,12)
        pivot=f'wheel_{"f" if axle=="Front" else "r"}{"l" if s<0 else "r"}'
        for part in PARTS[start:]:part['v']-=center;part['parent']=pivot
        PIVOTS[pivot]=center.tolist()
# Full-width quad-round GTI grille with a narrow red surround.
box('Grille_backing',(0,.75,-2.006),(1.55,.302,.033),black)
for y in [.635,.675,.715,.755,.795,.835,.875]:box('Horizontal_grille_slat',(0,y,-2.030),(1.50,.009,.01),trim)
for a,b in [((-.775,.60),(.775,.60)),((-.775,.90),(.775,.90)),((-.775,.60),(-.775,.90)),((.775,.60),(.775,.90))]:
    beam('GTI_red_grille_outline',(a[0],a[1],-2.040),(b[0],b[1],-2.040),.012,red)
for s in [-1,1]:
    for x,r in [(s*.602,.130),(s*.312,.095)]:
        disk('Headlamp_dark_seal',(x,.75,-2.042),r+.012,black,24)
        ring('Round_headlamp_chrome',(x,.75,-2.051),r,r*.86,.018,alloy,24,'z')
        disk('Round_glass_lens',(x,.75,-2.064),r*.85,light,24)
        ring('Inner_reflector_ring',(x,.75,-2.068),r*.67,r*.62,.004,alloy,20,'z')
        for fraction in [-.55,-.28,0,.28,.55]:
            xx=x+fraction*r;h=math.sqrt((r*.78)**2-(fraction*r)**2)
            beam('Headlamp_lens_fluting',(xx,.75-h,-2.072),(xx,.75+h,-2.072),.003,alloy)
    box('Bumper_indicator',(s*.60,.485,-2.128),(.23,.068,.012),indicator_light)
    box('Indicator_inner',(s*.60,.485,-2.137),(.15,.038,.005),indicator_light)
    box('Fog_lamp_surround',(s*.54,.298,-2.047),(.19,.087,.014),black)
    box('Fog_lamp',(s*.54,.298,-2.058),(.135,.055,.009),light)
    box('Rear_lamp_seal',(s*.555,.76,2.006),(.41,.17,.025),trim)
    for xx,mm in [(s*.69,indicator_light),(s*.56,tail_light),(s*.43,reverse_light)]:box('Rear_lamp_segment',(xx,.76,2.025),(.122,.135,.012),mm)
box('Lower_front_intake',(0,.286,-2.049),(.73,.075,.018),black)
# Badges remain separated geometric V/W strokes on a dark circular backing.
for z,y,r in [(-2.058,.75,.069),(2.027,.856,.047)]:
    facing=-1 if z<0 else 1
    disk('Badge_backing',(0,y,z-facing*.006),r*.94,black,32)
    ring('Badge_ring',(0,y,z),r,r*.86,.008,alloy,32,'z')
    for a,b in [((-.43,.53),(0,.02)),((0,.02),(.43,.53)),((-.63,.08),(-.33,-.57)),((-.33,-.57),(0,-.20)),((0,-.20),(.33,-.57)),((.33,-.57),(.63,.08))]:
        beam('VW_monogram',(a[0]*r,y+a[1]*r,z+facing*.002),(b[0]*r,y+b[1]*r,z+facing*.002),r*.095,alloy,r*.095)
for z,y in [(-2.139,.463),(2.131,.455)]:box('Plain_cream_plate',(0,y,z),(.48,.115,.014),white)
box('Hatch_handle',(0,.67,2.027),(.18,.028,.025),trim)
beam('Rear_wiper',(.07,1.01,1.68),(-.35,1.08,1.57),.016,trim)
for x in [-.40,.30]:beam('Front_wiper',(x,.98,-.75),(x+.27,1.025,-.69),.013,trim)
# Small roof aerial follows the period style.
beam('Aerial',(-.59,1.40,-.21),(-.59,1.64,-.37),.010,trim)
# Cabin: seats, stitched inserts, head restraints, rear bench.
for s in [-1,1]:
    x=s*.39
    box('Front_seat_base',(x,.48,.23),(.51,.15,.53),seat)
    box('Seat_cushion_insert',(x,.562,.23),(.33,.022,.40),seam)
    box('Front_seat_back',(x,.78,.50),(.50,.53,.13),seat)
    box('Seat_back_insert',(x,.80,.425),(.32,.37,.022),seam)
    for xx in [x-.13,x+.13]:beam('Headrest_post',(xx,1.02,.50),(xx,1.105,.50),.018,alloy)
    box('Front_headrest',(x,1.10,.50),(.30,.17,.13),seat)
box('Rear_bench',(0,.48,1.05),(1.12,.15,.48),seat)
box('Rear_backrest',(0,.76,1.30),(1.12,.48,.13),seat)
for x in [-.36,0,.36]:
    box('Rear_seat_insert',(x,.76,1.225),(.28,.34,.025),seam)
box('Parcel_shelf',(0,.86,1.59),(1.37,.05,.36),dash)
box('Dashboard',(0,.875,-.56),(1.47,.20,.37),dash)
box('Dash_top',(0,.986,-.58),(1.42,.035,.33),seat)
for x in [-.61,-.13,.13,.61]:
    box('Air_vent',(x,.895,-.364),(.15,.075,.012),black)
    for yy in [.875,.897,.918]:box('Vent_slat',(x,yy,-.354),(.13,.006,.006),seam)
box('Center_stack',(0,.70,-.39),(.25,.34,.13),dash)
box('Radio_face',(0,.79,-.315),(.19,.06,.009),black)
box('Radio_display',(0,.797,-.308),(.105,.021,.006),gauge)
for x in [-.065,0,.065]:ring('Climate_dial',(x,.69,-.315),.023,.014,.012,alloy,10,'z')
box('Center_tunnel',(0,.43,.20),(.21,.20,1.10),dash)
box('Shift_boot',(0,.55,-.05),(.12,.045,.13),trim)
beam('Gear_stick',(0,.56,-.05),(0,.68,-.075),.021,alloy)
box('Gear_knob',(0,.69,-.075),(.055,.045,.055),trim)
beam('Handbrake',(.075,.55,.31),(.075,.60,.12),.033,trim)
# Left-hand drive steering wheel, circular face points toward driver (+Z).
beam('Steering_column',(-.39,.81,-.40),(-.39,.91,-.19),.065,dash)
ring('Steering_wheel',(-.39,.94,-.18),.165,.137,.026,trim,20,'z',gname='steering_wheel_rim')
for a in [.65,2.49,3.79,5.63]:
    beam('Steering_spoke',(-.39,.94,-.18),(-.39+.145*math.sin(a),.94+.145*math.cos(a),-.18),.035,dash,gname='steering_wheel_spokes')
box('Steering_hub',(-.39,.94,-.17),(.115,.09,.04),dash,gname='steering_wheel_spokes')
box('Instrument_binnacle',(-.39,.995,-.40),(.39,.11,.08),dash)
for x in [-.49,-.29]:ring('Vintage_analog_dial',(x,1.00,-.352),.055,.045,.005,gauge,16,'z')
# The hatch: every part of the tailgate leaves the body for the node cargo_door (the rear half of the names below: the front has a badge too). The glass contract (docs/glass.md): the pane
# glass_1 is a child of the door like any door leaf's pane, it keeps its closed-pose outline in vehicles.json (the server tests panes closed) and the client hides the node when it shatters;
# the crack overlay and the shards stay where the pane is closed while the hatch is open (the known door-leaf gap).
HINGE=np.array([0.0,1.40,1.03])   # 1:1 metres: the roof's rear edge (Roof ends z 1.02, y 1.405; the window frame starts at 1.04)
HATCH_PARTS={'Hatch_lower_deck','End_panel','Hatch_handle','Rear_wiper','Rear_window_rubber','Rear_window_lower_surround','Rear_roof_window_join','Badge_backing','Badge_ring','VW_monogram'}
for p in PARTS:
    if p['gnode']==1 or (p['name'] in HATCH_PARTS and p['v'][:,2].mean()>1.0):
        p['parent']='cargo_door';p['v']=p['v']-HINGE
DOORS={'cargo_door':HINGE}
# Seats (feet, vehicles.json "seats": x right, y up, z back) and lamps ([x, y, z] of vehicles.json "lights"); empties in the glb.
SEATS={'seat_driver':(-.39,.36,.17),'seat_passenger_1':(.39,.36,.17),'seat_passenger_2':(-.36,.36,1.0),'seat_passenger_3':(.36,.36,1.0)}
LAMPS={'light_head_l':(-.46,.75,-2.07),'light_head_r':(.46,.75,-2.07),'light_tail_l':(-.56,.76,2.03),'light_tail_r':(.56,.76,2.03)}
# Export GLB: explicit normals, meters, +Y up, -Z forward, wheel-local pivots.
def export(path):
    blob=bytearray();views=[];access=[];meshes=[];nodes=[]
    def acc(arr,typ):
        arr=np.array(arr,dtype='<f4');blob.extend(b'\0'*((-len(blob))%4));off=len(blob);blob.extend(arr.tobytes());vi=len(views);views.append({'buffer':0,'byteOffset':off,'byteLength':arr.nbytes,'target':34962})
        a={'bufferView':vi,'componentType':5126,'count':len(arr),'type':typ}
        if typ=='VEC3':a.update(min=arr.min(axis=0).tolist(),max=arr.max(axis=0).tolist())
        access.append(a);return len(access)-1
    def node(name,parent,translation=None):
        nodes.append({'name':name});i=len(nodes)-1
        if translation is not None:nodes[i]['translation']=[round(float(v)*SCALE,5)+0.0 for v in translation]
        if parent is not None:nodes[parent].setdefault('children',[]).append(i)
        return i
    root=node('golf_mk2',None);hull=node('hull',root)
    pids={None:hull}
    for name,pos in PIVOTS.items():
        if name[-2]=='f':  # front: a steering pivot (turns about Y) holding the wheel (rolls about X)
            steer=node(name+'_steer',root,pos);pids[name]=node(name,steer)
        else:pids[name]=node(name,root,pos)   # the wheels are siblings of the hull: they follow the ground, the body leans over them
    for name,pos in {**SEATS,**LAMPS}.items():node(name,hull,pos)
    for name,pos in DOORS.items():pids[name]=node(name,hull,pos)
    tris=0
    grouped={}
    for p in PARTS:
        key=(p['parent'],p['gnode'],p['mat'],p['gname'])
        if key not in grouped:
            gname=p['gname'] or (f"glass_{p['gnode']}" if p['gnode'] is not None else (p['parent'] or 'body')+'_'+MATS[p['mat']]['name'].replace(' ','_'))
            grouped[key]=dict(name=gname,v=[],f=[],mat=p['mat'],parent=p['parent'])
        g=grouped[key];offset=len(g['v']);g['v'].extend(p['v']);g['f'].extend([[i+offset for i in face] for face in p['f']])
    for part in grouped.values():
        part['v']=np.array(part['v'])*SCALE
        vv=[];nn=[]
        for face in part['f']:
            for k in range(1,len(face)-1):
                a,b,c=part['v'][[face[0],face[k],face[k+1]]];normal=np.cross(b-a,c-a);l=np.linalg.norm(normal)
                if l<1e-9:continue
                normal/=l;vv.extend([a,b,c]);nn.extend([normal]*3);tris+=1
        if not vv:continue
        meshes.append({'name':part['name'],'primitives':[{'attributes':{'POSITION':acc(vv,'VEC3'),'NORMAL':acc(nn,'VEC3')},'material':part['mat']}]})
        nodes[node(part['name'],pids[part['parent']])]['mesh']=len(meshes)-1
    doc={'asset':{'version':'2.0','generator':'scripts/make_golf_mk2_model.py (Drycastle)','copyright':"CC0 1.0, generated by the project's own script"},'scene':0,'scenes':[{'name':'golf_mk2','nodes':[root]}],'nodes':nodes,'meshes':meshes,'materials':MATS,'buffers':[{'byteLength':len(blob)}],'bufferViews':views,'accessors':access}
    js=json.dumps(doc,separators=(',',':')).encode();js+=b' '*((-len(js))%4);blob+=b'\0'*((-len(blob))%4)
    data=struct.pack('<III',0x46546c67,2,12+8+len(js)+8+len(blob))+struct.pack('<II',len(js),0x4e4f534a)+js+struct.pack('<II',len(blob),0x004e4942)+blob
    Path(path).parent.mkdir(parents=True,exist_ok=True);Path(path).write_bytes(data)
    print(f'{tris} triangles, {len(meshes)} mesh parts, {len(MATS)} materials, {len(data)} bytes -> {path}')
    return tris
def main():
    out=sys.argv[1] if len(sys.argv)>1 else str(OUT)
    export(out)
    pts=[p['v']+np.array({**PIVOTS,**DOORS}.get(p['parent'],[0,0,0])) for p in PARTS]
    lo=SCALE*np.min([v.min(axis=0) for v in pts],axis=0);hi=SCALE*np.max([v.max(axis=0) for v in pts],axis=0)
    print('bounds: x %.3f..%.3f  y %.3f..%.3f  z %.3f..%.3f'%(lo[0],hi[0],lo[1],hi[1],lo[2],hi[2]))
    GLASS.report()
    if len(sys.argv)<=1:
        GLASS.write_vehicles_json(vehicles_json_path());print('wrote the glass list of vehicles.json')
if __name__=='__main__':main()
