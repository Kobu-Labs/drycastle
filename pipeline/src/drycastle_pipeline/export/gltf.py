"""glTF building blocks of the map files: flat-shaded trimeshes, chunk scenes, GLB export with the tree-base attribute."""

import json
import struct

import numpy as np
import trimesh

from drycastle_pipeline.core.mesh import Mesh
from drycastle_pipeline.core.textures import TexturedMesh, tex_node_name

NO_TREE_UV2 = 1.0e6  # TEXCOORD_1 of a vertex that is no tree
BASE_UV2_ATTRIBUTE = "_UV2"  # trimesh writes custom attributes with a leading underscore; export_glb renames it


def to_gltf_axes(tris: np.ndarray) -> np.ndarray:
    """Local (x east, y north, z up) -> glTF (x east, y up, z -north); a proper rotation."""
    out = np.empty_like(tris, dtype=np.float64)
    out[..., 0] = tris[..., 0]
    out[..., 1] = tris[..., 2]
    out[..., 2] = -tris[..., 1]
    return np.round(out, 4)


def flat_normals(verts: np.ndarray) -> np.ndarray:
    """One unit normal per vertex of unshared triangles (``verts`` is (3n, 3)): the face normal of its
    triangle (counter-clockwise front). A degenerate triangle gets straight up.

    Without NORMAL the game shades every map surface with one constant normal, so a lamp or a
    headlight lights the ground only on one side of itself.
    """
    t = verts.reshape(-1, 3, 3)
    n = np.cross(t[:, 1] - t[:, 0], t[:, 2] - t[:, 0])
    length = np.linalg.norm(n, axis=1, keepdims=True)
    n = np.where(length > 1e-12, n / np.maximum(length, 1e-12), np.array([0.0, 1.0, 0.0]))
    return np.repeat(n, 3, axis=0).astype(np.float32)


def to_trimesh(mesh: Mesh, normals: bool = True) -> trimesh.Trimesh:
    """Flat-shaded trimesh: unshared vertices, one RGBA vertex colour and one normal per triangle.

    ``normals=False`` for a collision mesh (never drawn): no NORMAL attribute, a smaller file.
    """
    verts = to_gltf_axes(mesh.tris).reshape(-1, 3)
    faces = np.arange(len(verts)).reshape(-1, 3)
    rgba = np.concatenate([mesh.colours, np.full((len(mesh.colours), 1), 255, np.uint8)], axis=1)
    tm = trimesh.Trimesh(
        vertices=verts,
        faces=faces,
        vertex_colors=np.repeat(rgba, 3, axis=0),
        vertex_normals=flat_normals(verts) if normals and len(verts) else None,
        process=False,
    )
    if mesh.bases is not None and not np.isnan(mesh.bases).all():
        # (POSITION x, POSITION z) of the tree base: glTF axes of (x, y, z) are (x, z, -y); same rounding as the positions
        uv2 = np.round(np.stack([mesh.bases[:, 0], -mesh.bases[:, 1]], axis=1), 4)
        uv2[np.isnan(uv2).any(axis=1)] = NO_TREE_UV2
        tm.vertex_attributes[BASE_UV2_ATTRIBUTE] = np.repeat(uv2, 3, axis=0).astype(np.float32)
    return tm


def to_trimesh_textured(mesh: TexturedMesh, tid: str) -> trimesh.Trimesh:
    """Flat-shaded trimesh with UVs (TEXCOORD_0) and the tint as vertex colour (COLOR_0).

    The material only carries the name ``tex:<id>``; no image is embedded (see ``core/textures.py``).
    """
    verts = to_gltf_axes(mesh.tris).reshape(-1, 3)
    rgba = np.concatenate([mesh.colours, np.full((len(mesh.colours), 1), 255, np.uint8)], axis=1)
    visual = trimesh.visual.TextureVisuals(
        uv=mesh.uvs.reshape(-1, 2),
        material=trimesh.visual.material.PBRMaterial(
            name=f"tex:{tid}", roughnessFactor=1.0, metallicFactor=0.0
        ),
    )
    visual.vertex_attributes["color"] = np.repeat(rgba, 3, axis=0)
    return trimesh.Trimesh(
        vertices=verts, faces=np.arange(len(verts)).reshape(-1, 3), visual=visual,
        vertex_normals=flat_normals(verts) if len(verts) else None, process=False,
    )


def node_trimesh(name: str, mesh: Mesh | TexturedMesh) -> trimesh.Trimesh:
    if isinstance(mesh, TexturedMesh):
        return to_trimesh_textured(mesh, name.partition("_tex_")[2])
    return to_trimesh(mesh, normals=not name.endswith("collision"))


def export_glb(scene: trimesh.Scene) -> bytes:
    """``scene`` as GLB bytes, with the base attribute (``_UV2``, see ``to_trimesh``) renamed to ``TEXCOORD_1``.

    trimesh can only write TEXCOORD_0 itself. Every GLB that may carry tree meshes goes through here.
    """
    data = scene.export(file_type="glb")
    jlen, jtype = struct.unpack_from("<II", data, 12)
    assert jtype == 0x4E4F534A  # "JSON"
    doc = json.loads(data[20 : 20 + jlen])
    found = False
    for mesh in doc.get("meshes", []):
        for prim in mesh["primitives"]:
            if BASE_UV2_ATTRIBUTE in prim["attributes"]:
                prim["attributes"]["TEXCOORD_1"] = prim["attributes"].pop(BASE_UV2_ATTRIBUTE)
                found = True
    if not found:
        return data
    blob = json.dumps(doc, separators=(",", ":")).encode()
    blob += b" " * (-len(blob) % 4)
    rest = data[20 + jlen :]
    return struct.pack("<4sIIII", b"glTF", 2, 20 + len(blob) + len(rest), len(blob), jtype) + blob + rest


def parcel_props_nodes(
    items: list[tuple[str, Mesh, Mesh, dict[str, TexturedMesh]]],
) -> dict[str, Mesh | TexturedMesh]:
    """``parcel_<ID>_props`` (visual), ``parcel_<ID>_props_collision`` and one
    ``parcel_<ID>_props_tex_<texture id>`` per texture used; empty ones omitted."""
    out: dict[str, Mesh | TexturedMesh] = {}
    for pid, visual, collision, textured in sorted(items, key=lambda t: t[0]):
        for suffix, mesh in (("props", visual), ("props_collision", collision)):
            if mesh.face_count:
                out[f"parcel_{pid}_{suffix}"] = mesh
        for tid, mesh in sorted(textured.items()):
            if mesh.face_count:
                out[tex_node_name(f"parcel_{pid}_props", tid)] = mesh
    return out


def chunk_name(ix: int, iy: int) -> str:
    return f"chunk_{ix}_{iy}"


def build_chunk_scene(
    parcel_meshes: list[tuple[str, Mesh, Mesh]],
    extra: dict[str, Mesh | TexturedMesh] | None = None,
) -> trimesh.Scene:
    """Nodes of one chunk: parcel nodes plus terrain, terrain_collision, roads, props, ..."""
    scene = trimesh.Scene()
    for pid, visual, collision in sorted(parcel_meshes, key=lambda t: t[0]):
        for suffix, mesh in (("visual", visual), ("collision", collision)):
            name = f"parcel_{pid}_{suffix}"
            scene.add_geometry(to_trimesh(mesh, normals=suffix == "visual"), node_name=name, geom_name=name)
    for name, mesh in sorted((extra or {}).items()):
        scene.add_geometry(node_trimesh(name, mesh), node_name=name, geom_name=name)
    return scene
