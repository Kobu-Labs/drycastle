"""Reading a Unity .unitypackage (a gzipped tar of <guid>/asset + <guid>/pathname) for the pack extractors. The package is untrusted data: it is
unpacked with the tarfile "data" filter into a fresh temp directory that is deleted again; nothing in it is executed; the YAML is only
regex-read as text."""
import contextlib
import os
import re
import shutil
import tarfile
import tempfile


@contextlib.contextmanager
def unpack(pkg, prefix):
    """Yields (tmp dir, {guid: Unity path}, {Unity path: guid}, read(guid) -> the asset's text)."""
    tmp = tempfile.mkdtemp(prefix=prefix)
    try:
        with tarfile.open(pkg, "r:gz") as t:
            t.extractall(tmp, filter="data")
        path_of, guid_for = {}, {}
        for g in os.listdir(tmp):
            p = os.path.join(tmp, g, "pathname")
            if os.path.isfile(p):
                up = open(p, encoding="utf-8").read().split("\n")[0].strip()
                path_of[g] = up
                guid_for[up] = g

        def read(g):
            return open(os.path.join(tmp, g, "asset"), encoding="utf-8", errors="replace").read()

        yield tmp, path_of, guid_for, read
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


def guid_of(s):
    m = re.search(r"guid: (\w+)", s)
    return m.group(1) if m else None


def objects(text):
    """(class id, file id, body) of each object of a .prefab / .mat YAML text."""
    for m in re.finditer(r"^--- !u!(\d+) &(-?\d+)[^\n]*\n(.*?)(?=^--- !u!|\Z)", text, re.S | re.M):
        yield int(m.group(1)), m.group(2), m.group(3)


def write_godot_project(proj, name):
    """project.godot of the small Godot project the FBX are imported in (only when missing)."""
    gp = os.path.join(proj, "project.godot")
    if not os.path.exists(gp):
        open(gp, "w").write(f'config_version=5\n\n[application]\n\nconfig/name="{name}"\nconfig/features=PackedStringArray("4.7", "Forward Plus")\n')
