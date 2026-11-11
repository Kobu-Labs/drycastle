# OSM completeness and Mapillary coverage for Suchohrad

Resolves Kobu-Labs/drycastle#4 (part of #1). Data snapshot: Overpass `osm_base` 2026-11-10T14:00:59Z. Queries sent to `https://overpass-api.de/api/interpreter` (2 requests total). Raw output and scripts: scratchpad `osm/` (q0.ql, q1.ql, r1.json, an.py, an1.txt), not committed.

## Method

Municipality relation (query 0, resolved to relation 2206970, bbox 48.373-48.435 N, 16.850-16.914 E; wikidata Q650690):

```
[out:json][timeout:60];
rel["boundary"="administrative"]["name"="Suchohrad"];
out tags bb;
```

Data query (query 1), area id = 3600000000 + 2206970:

```
[out:json][timeout:120];
area(3602206970)->.a;
( nwr["building"](area.a); way["highway"](area.a); nwr["landuse"](area.a);
  nwr["barrier"](area.a); node["natural"="tree"](area.a); way["natural"="tree_row"](area.a);
  nwr["natural"~"wood|scrub|water|grassland"](area.a); nwr["waterway"](area.a);
  nwr["amenity"](area.a); nwr["addr:housenumber"](area.a); );
out tags center;
```

Tallies computed locally with `an.py`. Note the municipality is larger than the village: buildings cluster in roughly 48.404-48.416 N, 16.853-16.878 E (5th-95th percentile of building centres), the rest is forest, fields and tracks. Road/landuse counts below are for the whole municipality.

## Buildings

- 447 building features (446 ways, 1 node).
- Types: `yes` 429, `apartments` 8, `bunker` 2, `detached` 2, and one each of hut, church, garage, office, public, commercial. So 96% are untyped.
- `height`: 0 (0%). `building:levels`: 0 (0%). `roof:shape`: 0 (0%). Also 0 for `roof:levels`, `roof:height`, `roof:material`, `roof:colour`, `building:colour`, `building:material`, `building:part`.
- `addr:housenumber`: 312 on buildings (69.8%); 323 address-tagged features in total. Addresses use `addr:place` + `addr:conscriptionnumber` (no `addr:street`; the village has no street names in OSM).
- `name`: 6 buildings (1.3%).

Conclusion: footprints exist but there is no height, level, or roof data at all. Heights/roofs must come from a rule-based generator (defaults, randomisation by building type, optionally the DMR/DSM or Mapillary later). Footprint count is a usable ~450-building base.

## Roads (ways with `highway`, 202 total)

track 125, residential 37, service 30, tertiary 3, path 3, secondary 2, unclassified 1, footway 1. Most tracks are field/forest tracks outside the village; the village street network is about 37 residential + some service + the tertiary/secondary through-roads.
Attribute completeness: `surface` 91/202 (asphalt 33, gravel 27, concrete:plates 10, grass 10, dirt 4, earth 3, unpaved 2, concrete 2), `maxspeed` 28, `lanes` 27; `name` 0, `oneway` 0, `lit` 0, `sidewalk` 0, `width` 0. Gaps: no street names, no widths, no sidewalks, so road width/lane layout must be defaulted per highway class.

## Landuse, natural, water

- landuse: forest 9, meadow 8, industrial 7, farmland 2, allotments 2, residential 1, farmyard 1, cemetery 1.
- natural: wood 16, water 11, grassland 6, tree 4.
- waterway: river 5, stream 5, drain 3, ditch 3.
- Only ONE `landuse=residential` polygon, so parcel/zone information inside the village is thin; farmland coverage (2) looks incomplete for a rural municipality.

## Barriers and trees

- barrier: swing_gate 10, wall 2, gate 1, fence 1 (14 total). Property fences are essentially unmapped.
- Individual trees: 4 `natural=tree` nodes; no tree rows returned. Vegetation must come from `natural=wood` polygons plus procedural scattering (or land cover raster).

## Other amenities (for landmark candidates)

pub 2, place_of_worship 2, bench 2, fire_station 1, townhall 1, waste_basket 1, bicycle_repair_station 1.

## Obvious gaps (summary)

1. No building height/levels/roof data (0%).
2. No street names, widths, sidewalks, lighting.
3. Almost no fences, hedges, or individual trees.
4. Sparse landuse inside the village; no parcel boundaries in OSM (parcels for the base map will have to come from cadastre data or be generated).
5. 96% of buildings are generic `building=yes`.

## Mapillary coverage and licence

- Not verifiable without credentials. `GET https://graph.mapillary.com/images?bbox=16.88,48.40,16.90,48.42&limit=1` returned HTTP 500 `Invalid OAuth 2.0 Access Token` (error code 190); the public vector tiles endpoint `https://tiles.mapillary.com/maps/vtp/mly1_public/2/14/8960/5667` returned 403. The Mapillary API documentation page (https://www.mapillary.com/developer/api-documentation) states requests need an access token (`access_token=...`). A free Mapillary developer token is needed to measure coverage (sequence and image counts in the village bbox); that is a follow-up for the issue owner.
- The Mapillary licence help page returned 403 to curl, so the licence was not verified from the first-party source in this run. From memory (unverified): Mapillary imagery is CC BY-SA 4.0 and map data CC BY-SA; check before using any derived assets.
- Alternative street imagery source checked: KartaView API `GET https://api.kartaview.org/2.0/photo/?lat=48.41&lng=16.88&radius=500` returned HTTP 200 with zero photos (centre point is approximate, so this is weak evidence of no KartaView coverage near the village centre).

## Caveats

- Single snapshot, two requests, one municipality query; no completeness comparison against ground truth or cadastre.
- Village bbox is approximate and derived from building centres.
