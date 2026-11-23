"""MCP server for parcel edits: lets an LLM (Claude Code) change ``pipeline/config/edits.json``.

Run it over stdio (Claude Code starts it)::

    uv run --directory <repo>/pipeline python -m drycastle_pipeline.edits.mcp.server
        [--edits PATH] [--state PATH] [--assets PATH] [--work DIR]

It talks only to files: it reads the map's work files (``parcels.json``, ``buildings.json``,
``roads.json``), the asset manifest and, for "the selected parcel", ``out/editor-state.json`` written
by the viewer; it writes ``edits.json`` (the viewer, started with ``--editor --watch``, notices the
change and re-applies the affected parcels). The schema and the validator are the pipeline's own
(:mod:`drycastle_pipeline.edits.schema`, :mod:`drycastle_pipeline.core.assets`), so a file written here is
exactly what the pipeline and the Godot editor accept.

All writes are reload -> modify -> validate the whole file -> atomic write, under an exclusive
``flock`` on ``<edits>.lock`` so two writers (two MCP calls, or the editor) never lose each other's
changes. Coordinates in tool input and output are absolute S-JTSK (EPSG:5514) metres, x east and
y north, the frame of ``edits.json``; the local map frame is only used internally.

Placement frame (``place_object``): the *front edge* of a parcel is the boundary side nearest a
road. Standing on its midpoint and looking into the parcel, ``along`` grows to the right and
``inward`` grows away from the road. An object's front is its glTF +Z axis (cars face +Z in the
Kenney kit); ``rotation_deg`` is counter-clockwise seen from above, as in ``edits.json``.
"""
