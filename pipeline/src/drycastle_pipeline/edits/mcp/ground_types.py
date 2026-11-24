"""Tools on ground types: the catalog, and editing the types of edits.json."""

from __future__ import annotations

import json
from typing import Any

from mcp.server.mcpserver.exceptions import ToolError

from drycastle_pipeline.core.rules import _merge
from drycastle_pipeline.edits.mcp.base import read_edits
from drycastle_pipeline.edits.schema import Edits, protected_ground_types
from drycastle_pipeline.ground.areas import (
    ground_type_source,
    ground_types,
    merge_ground_types,
    rules_ground_types,
)
from drycastle_pipeline.ground.attributes import ATTRIBUTES, GROUPS, RECIPES, attributes_of


class GroundTypeTools:
    """list_ground_types, set_ground_type, reset_ground_type, delete_ground_type."""

    def _ground_types(self) -> dict[str, dict]:
        """The effective ground types: rules.yaml with the overrides and additions of edits.json."""
        read_edits(self.settings.edits_path)  # an invalid edits file is reported as such, not as a crash
        return ground_types(self.settings.rules_path, edits_path=self.settings.edits_path)

    def _ground_type(self, name: object) -> str:
        types = self._ground_types()
        if not isinstance(name, str) or name not in types:
            raise ToolError(f"unknown ground type {name!r}; choose one of {', '.join(types)} (see list_ground_types)")
        return name

    def _is_water_type(self, name: str) -> bool:
        return bool(attributes_of(self._ground_types()[name]).get("water"))

    @staticmethod
    def _catalog() -> dict:
        """The attribute catalog for the model: the groups and what values each attribute takes."""
        values = {
            "colours": "list of 1-6 \"#rrggbb\"",
            "recipe": " | ".join(RECIPES),
            "detail": "object laid over the recipe's section of rules.yaml detail",
            "water": "true",
        }
        return {
            "groups": [
                {
                    "group": g.key,
                    "note": g.note,
                    "attributes": [
                        {"attribute": a.key, "label": a.label, "value": values[a.key]}
                        for a in ATTRIBUTES.values()
                        if a.group == g.key
                    ],
                }
                for g in GROUPS
            ],
        }

    def list_ground_types(self) -> dict:
        """The ground types, effective attributes and where each comes from (see the registered tool description)."""
        rules = rules_ground_types(self.settings.rules_path)
        edited = read_edits(self.settings.edits_path).ground_types
        rows = []
        for name, spec in merge_ground_types(rules, edited).items():
            rows.append({
                "type": name,
                "source": ground_type_source(name, rules, edited),
                "attributes": attributes_of(spec),
                "osm": spec.get("osm") or {},
                "from": "rule: " + spec["rule"] if spec.get("rule") else ("osm: " + json.dumps(spec["osm"], sort_keys=True) if spec.get("osm") else "hand-drawn or default only"),
            })
        deleted = sorted(n for n in edited if ground_type_source(n, rules, edited) == "deleted")
        out: dict = {
            "summary": f"{len(rows)} ground types" + (f" ({len(deleted)} deleted by you)" if deleted else ""),
            "types": rows,
            "catalog": self._catalog(),
        }
        if deleted:
            out["deleted"] = deleted
        return out

    @staticmethod
    def _ground_type_patches(name: str, edits: Edits) -> list[str]:
        return sorted(pid for pid, p in edits.ground_patches.items() if p.get("type") == name)

    def set_ground_type(
        self,
        name: str,
        attributes: dict | None = None,
        osm: dict | None = None,
    ) -> dict:
        """Create a ground type or change the user's override of one (see the registered tool description)."""
        if not isinstance(name, str):
            raise ToolError("name must be text, a lower_snake type name like my_meadow")
        if attributes is None and osm is None:
            raise ToolError("give attributes (the attributes to set, null removes one) and/or osm")
        if attributes is not None:
            if not isinstance(attributes, dict) or not attributes:
                raise ToolError("attributes must be an object like {\"colours\": [\"#d8c38a\"], \"recipe\": \"field\"} (see list_ground_types catalog)")
            unknown = [k for k in attributes if k not in ATTRIBUTES]
            if unknown:
                raise ToolError(
                    f"unknown attribute {', '.join(unknown)}; a ground type is terrain only, the catalog has {', '.join(ATTRIBUTES)} "
                    "(what an area means in play is a zone: see create_zone) (see list_ground_types)"
                )
        rules = rules_ground_types(self.settings.rules_path)

        def change(edits: Edits) -> str:
            before = ground_type_source(name, rules, edits.ground_types)
            if before == "deleted":
                raise ToolError(f"{name} is deleted by you; reset_ground_type it first to bring it back. Nothing was changed.")
            existed = name in edits.ground_types
            entry = edits.ground_types.setdefault(name, {})
            if attributes is not None:
                own = entry.setdefault("attributes", {})
                carried_by_rules = attributes_of(rules.get(name))
                for key, value in attributes.items():
                    if value is None:  # remove: a null only where the rules carry it, else just drop what was set
                        if key in carried_by_rules:
                            own[key] = None
                        else:
                            own.pop(key, None)
                    elif key == "detail":
                        if not isinstance(value, dict):
                            raise ToolError("detail must be an object, e.g. {\"furrow\": {\"probability\": 0.0}}")
                        own[key] = _merge(own.get(key) or {}, value)
                    else:
                        own[key] = value
                if not own:
                    del entry["attributes"]
            if osm is not None:
                entry["osm"] = osm
            return "updated" if existed else ("created" if name not in rules else "overridden")

        what = self._write(change)
        row = next(r for r in self.list_ground_types()["types"] if r["type"] == name)
        return {
            "summary": f"ground type {name} {what}; the map shows it after the next full run (make map)",
            "type": row,
            "warnings": [],
        }

    def reset_ground_type(self, name: str) -> dict:
        """Remove the user's override of a ground type of the rules (also brings a deleted one back)."""
        rules = rules_ground_types(self.settings.rules_path)

        def change(edits: Edits) -> str:
            source = ground_type_source(name, rules, edits.ground_types) if isinstance(name, str) else "custom"
            if source == "custom":
                raise ToolError(
                    f"{name!r} is not a type of rules.yaml, so there is nothing to reset to; use delete_ground_type to remove it. "
                    f"Types of the rules: {', '.join(rules)}. Nothing was changed."
                )
            if source == "rules":
                raise ToolError(f"{name} has no override of yours; it is as in rules.yaml. Nothing was changed.")
            del edits.ground_types[name]
            return source

        was = self._write(change)
        return {
            "summary": f"ground type {name} is as in rules.yaml again" + (" (it was deleted)" if was == "deleted" else "")
            + "; the map shows it after the next full run (make map)",
            "type": next(r for r in self.list_ground_types()["types"] if r["type"] == name),
            "warnings": [],
        }

    def delete_ground_type(self, name: str) -> dict:
        """Delete a ground type: a custom one is removed, one of the rules is marked deleted (reset_ground_type undoes)."""
        rules = rules_ground_types(self.settings.rules_path)

        def change(edits: Edits) -> str:
            if not isinstance(name, str) or name not in {*rules, *edits.ground_types}:
                raise ToolError(f"unknown ground type {name!r}; choose one of {', '.join(self._ground_types())} (see list_ground_types)")
            source = ground_type_source(name, rules, edits.ground_types)
            if source == "deleted":
                raise ToolError(f"{name} is already deleted. Nothing was changed.")
            if name in protected_ground_types():
                raise ToolError(f"{name} cannot be deleted, it {protected_ground_types()[name]}. Nothing was changed.")
            on_parcels = sorted(pid for pid, e in edits.parcels.items() if e.get("ground_type") == name)
            if on_parcels:
                raise ToolError(
                    f"parcels {', '.join(on_parcels)} have the ground of type {name}; give them another "
                    "(set_parcel_ground_type) first. Nothing was changed."
                )
            used = self._ground_type_patches(name, edits)
            if used:
                raise ToolError(
                    f"ground patches {', '.join(used)} are of type {name}; delete them or give them another type "
                    "(set_patch_type) first. Nothing was changed."
                )
            if source == "custom":
                del edits.ground_types[name]
            else:
                edits.ground_types[name] = {"deleted": True}
            return source

        was = self._write(change)
        return {
            "summary": f"ground type {name} deleted" + ("" if was == "custom" else " (reset_ground_type brings it back)")
            + "; the map shows it after the next full run (make map)",
            "type": name,
            "warnings": [],
        }


def register(server: Any, tools: Any) -> None:
    """Add this group's tools to the MCP ``server`` (each calls the matching method of ``tools``)."""
    @server.tool(description=(
        "List the ground types (rules.yaml ground.types plus your overrides and additions in edits.json): name, "
        "`source` (rules = as in rules.yaml, overridden = rules.yaml plus your override, custom = made by you), "
        "the `attributes` it carries (a ground type is terrain only: colours, detail recipe and settings, water), the OSM "
        "values it takes, and where it comes from on its own (OSM, a rule, or only hand-drawn); `deleted` names the types "
        "of the rules you removed. `catalog` lists every attribute that exists, grouped (Appearance, Water), and what "
        "values it takes. Use a name as the `type` of create_ground_patch."))
    def list_ground_types() -> dict:
        return tools.list_ground_types()

    @server.tool(description=(
        "Create a ground type, or change your override of one that rules.yaml defines. Name: lower_snake, at most 24 "
        "characters, an ID that cannot be renamed. A type is a named bundle of terrain attributes; `attributes` sets the "
        "ones you give (see the `catalog` of list_ground_types): `colours` (1-6 \"#rrggbb\" terrain shades), `recipe` (field, "
        "forest, village or dike), `detail` (settings laid over that recipe's section of rules.yaml `detail`, e.g. "
        "{\"furrow\": {\"probability\": 0.0}, \"tone_per_ha\": 8}; merged into your earlier ones), `water` (true; a patch of "
        "a water type needs a water_y). A type with only `colours` is a paint colour. A value null removes that "
        "attribute (the type stops carrying it). A ground type carries nothing a game system reads: what an area means in "
        "play is a zone (create_zone). `osm` ({\"landuse\": [...], \"natural\": [...]}, the OSM values the type "
        "takes; each class given replaces its list; such a type needs attributes). Only what you give is "
        "overridden; the rest stays as in rules.yaml. A new name makes a custom type (usable as the `type` of a "
        "patch). Needs the next full run to show on the map (make map)."))
    def set_ground_type(
        name: str,
        attributes: dict | None = None,
        osm: dict | None = None,
    ) -> dict:
        return tools.set_ground_type(name, attributes, osm)

    @server.tool(description=(
        "Remove your override of a ground type of rules.yaml, so it is as in rules.yaml again; also brings back one you "
        "deleted. Not for custom types (delete_ground_type)."))
    def reset_ground_type(name: str) -> dict:
        return tools.reset_ground_type(name)

    @server.tool(description=(
        "Delete a ground type: a custom one is removed, one of rules.yaml is marked deleted (reset_ground_type brings it "
        "back). Refused for the default type and for the dike, and while ground patches are of the type (the error names "
        "them)."))
    def delete_ground_type(name: str) -> dict:
        return tools.delete_ground_type(name)
