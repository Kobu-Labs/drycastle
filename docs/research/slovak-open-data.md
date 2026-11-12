# Slovak open data for Drycastle: availability, formats, licences

Resolves #3 (part of #1). Researched 2026-11-10 from primary sources (skgeodesy.sk / geoportal, INSPIRE services of GKU Bratislava and UGKK SR). Live endpoints were probed; results marked "verified live".

Village: Drycastle (okres Malacky), cadastral zoning (katastralne uzemie) code **859524** (verified live from the INSPIRE cadastral WFS). Extent in WGS84 roughly lon 16.850-16.914, lat 48.373-48.435.

## Summary

| Need | Best source | Access | Licence |
|---|---|---|---|
| Parcels (cadastral map, C register) | INSPIRE WFS / OGC API Features, `cp` | Query by KU, GeoJSON/GML | CC BY 4.0, author UGKK SR |
| Terrain | DMR 5.0 (1 m) via INSPIRE WCS or ALS ZIP / MAPKA | WCS clip, GeoTIFF | CC BY 4.0, author UGKK SR |
| Orthophoto | Orthophotomosaic via MAPKA clip or WMTS | MAPKA export (free), WMTS tiles | CC BY 4.0 on service pages BUT 2018 terms PDF has no-modify clauses (see Uncertainties) |
| Buildings | INSPIRE Buildings ATOM (whole-SK ZIP) | GML/GDB/GPKG, 0.4-0.6 GB | CC BY 4.0, GKU Bratislava |
| Landuse | Not found as a free open download | See below | n/a |

## Cadastral parcels

- Services (page: https://www.skgeodesy.sk/geoportal-en/inspire/download-services):
  - WFS 2.0: `https://inspirews.skgeodesy.sk/geoserver/cp/ows?service=wfs` (C register, the vector cadastral map)
  - OGC API Features: `https://inspirews.skgeodesy.sk/geoserver/cp/ogc/features/v1`
  - E register (original-parcel/land-consolidation register, different thing): `.../geoserver/cp_uo/ows`
  - ATOM bulk ZIP: service feed `https://inspirews.skgeodesy.sk/atom/f48baf46-4fdc-4f89-a57c-1f9f0faaa5d3_dlsFeed.atom`; per-region dataset feeds listed on the download-services page (Bratislava region feed `79547966-9ec7-4558-8a68-d9053178e54f`). The page lists the same ID for Zilina and Kosice (likely page error).
- Verified live: feature types `cp:CP.CadastralParcel` and `cp:CP.CadastralZoning`. Filter `cql_filter=nationalCadastralReference LIKE '859524%'` returned **2153 parcels** for Drycastle KU. Parcel feature has `nationalCadastralReference` (e.g. `859524_2819.C`), `label` (parcel number), `areaValue` (m2), `referencePoint`, polygon geometry (WGS84 by default via GeoJSON). Data version stamp seen: 2026-10-05.
- Attributes are INSPIRE-minimal: geometry, parcel number, area. No landuse type, owner, or building link in this service (owner data is not open). The vector cadastral map is separate from the attribute register. Parcel numbers are directly usable as stable "addressable parcel" keys, but see uncertainties on stability when parcels are split/merged.
- Licence: capabilities document states `AccessConstraints: CC BY 4.0`, `Fees: no conditions apply`; ATOM feed `<rights>` = https://creativecommons.org/licenses/by/4.0/deed.sk. Author: UGKK SR. Attribution string: "UGKK SR".
- Also exists: the public Katastralny portal (kataster.skgeodesy.sk) with WMS/WMTS display layers; no licence text found there on the pages read. Display-only, not needed given WFS.

## Terrain (DMR)

- "DMR 3.5" as named in the issue is **not offered at useful resolution**: the ZBGIS download page (https://www.skgeodesy.sk/geoportal-en/zbgis/download) lists DMR 3.5 only at 10/25/50/100 m (TIFF, S-JTSK); the file sizes shown duplicate the Geographical Names ones, probably a page copy error. It is a legacy product.
- **DMR 5.0 (airborne laser scanning, 1 m)** is the current product: https://www.skgeodesy.sk/geoportal-en/zbgis/als/provision-als-products. Free, CC BY 4.0, author UGKK SR, credit "UGKK SR", plus the ALS product terms. 1st cycle: DTM 5.0 / DSM 1.0, 1 m, whole Slovakia. 2nd cycle (DTM 6.0 / DSM 2.0, 0.5 m) is available by lot only; page lists LOT04-LOT32 and does not say whether Malacky is covered.
- Access: whole-SK ZIP rasters (TIFF); MAPKA clip up to 400 km2 (more than enough for Drycastle, KU is about 4 x 7 km); classified point clouds as LAZ only by offline order form.
- Verified live: INSPIRE WCS `https://inspirews.skgeodesy.sk/geoserver/el/ows?service=WCS` serves coverage `el__EL.GridCoverage`, "DTM 5.0", 1 m x 1 m, from ALS 2017-2022, CRS EPSG:3046 (ETRS89-TM34). So a village-sized GeoTIFF can be pulled with a WCS GetCoverage subset, scriptable in the pipeline. (A GetCoverage was not run; capability only.)
- Vertical datum: Bpv (S-JTSK03) or ETRS89 ellipsoidal heights depending on product; confirm before mixing with other sources.

## Orthophoto

- Page: https://www.skgeodesy.sk/geoportal-en/zbgis/download (section 6). Cycles: 1st 2017-19, 2nd 2020-22, 3rd 2023-25 (western Slovakia flown 2023, central 2024 in the 3rd cycle; Malacky is western Slovakia, so 3rd-cycle 2023 imagery is expected but acquisition dates should be checked in `lms_datum_snimkovania_3.zip`). Bulk ZIPs are 100-200 GB each, unsuitable; smaller extents are free via MAPKA (https://zbgis.skgeodesy.sk/mapka/en/zakladna-mapa). RGB and RGBN variants.
- WMTS: `https://zbgisws.skgeodesy.sk/zbgis_ortofoto_wmts_sjtsk/service.svc/get?` (verified live, CRS EPSG:5514, one layer `WMS_zbgis_ortofoto_wmts_sjtsk`, 13 zoom levels). No stated rate limits. Good for previews; bulk-scraping tiles to build a texture is a grey area, prefer MAPKA export.
- Licence conflict: the WMTS page lists CC BY 4.0 (author GKU Bratislava, page updated 2026-02-01). The terms PDF (https://www.skgeodesy.sk/files/geoportal/licencne-podmienky/terms_and_conditions_of_provision_and_use_orthophotomosaic.pdf, dated 11 April 2018, authors GKU Bratislava and NLC) says: free; may combine with other data, use in own work incl. commercial, publish online and as a base map; attribute "GKU Bratislava, NLC"; BUT "may not further reproduce and distribute the Source Data in its original form" and "may not modify or alter the Source Data for any purpose". The strict reading forbids both redistributing raw tiles and altering them (stylising/tinting them into game textures).

## Buildings and landuse

- Buildings: INSPIRE Buildings, ATOM feed `https://inspirews.skgeodesy.sk/atom/4d6b8451-7fb1-4783-98d7-da2eecf97244_dataFeed.atom` (verified live). Whole-Slovakia ZIPs in EPSG:4258: GML 593 MB, GDB 418 MB, GPKG 637 MB under `https://opendata.skgeodesy.sk/static/INSPIRE/Buildings/`. CC BY 4.0, GKU Bratislava. Content/attributes (heights, roof, floors) not inspected: unknown whether it holds more than footprints.
- ZBGIS vector (feature catalogue KTO ZBGIS: https://www.skgeodesy.sk/files/sk/slovensky/ugkk/geodezia-kartografia/zb-gis/kto_zbgis.pdf) covers buildings, roads, water, landuse, vegetation. The download page offers only a **sample** (GPKG 6.8 MB) and the raster product; no free full vector download of ZBGIS categories was found. The WMTS "all categories" service is raster only (CC BY 4.0, no FeatureInfo). Full vector ZBGIS appears to be provided via order (https://www.skgeodesy.sk/geoportal-en/zbgis/order/), terms not read.
- Landuse: not found as open vector. Cadastral land-use type (druh pozemku) lives in the attribute register (not in INSPIRE parcel WFS); the OSM wiki lists "ako.vugk.sk" open attribute data from UGKK (consent 2022-05-25), not verified by me. OSM (ODbL) is the practical fallback for landuse and roads, outside this ticket's scope.

## Redistribution inside a game

- CC BY 4.0 data (parcels, DMR 5.0, buildings, ZBGIS WMTS, elevation): redistribution of the data and derivatives, including in a game, is allowed with attribution and an indication of changes. Games baking terrain/parcels into assets is fine. Suggested credits: "UGKK SR" (parcels, DMR), "GKU Bratislava" (buildings, orthophoto, ZBGIS), plus link to the licence.
- ZBGIS Raster terms (https://www.skgeodesy.sk/files/geoportal/licencne-podmienky/terms_and_conditions_of_provision_and_use_zbgis_raster.pdf, 18 Feb 2021): own work allowed; on publishing must cite metadata and source "GKU Bratislava"; a new cartographic work must be notified to the provider and handled per Act 215/1995. A game map arguably is not a cartographic work, but this is a legal judgement.
- Third-party record (OSM wiki, https://wiki.openstreetmap.org/wiki/WikiProject_Slovakia/Sources): GKU said 2023-06-28 that freely available ZBGIS services need no formal agreement, with attribution; it also notes UGKK states cadastral data has no copyright (undated). Secondary source, not a licence.

## Size for the village

- Parcels: 2153 polygons for KU Drycastle (about a few MB GeoJSON). Whole village area about 4 km x 7 km; DMR 5.0 at 1 m is about 28M cells, a few tens of MB as compressed GeoTIFF (estimate). Orthophoto at native resolution depends on GSD (not confirmed; likely 20 cm in 3rd cycle; unverified) so on the order of 0.5-2 GB uncompressed for the whole KU (estimate); buildings are a subset of a 0.4-0.6 GB national file.

## Uncertainties and follow-ups

1. Orthophoto licence conflict (CC BY 4.0 on 2026 pages vs. 2018 no-modify/no-redistribute-original terms). Ask gku@skgeodesy.sk for written confirmation that stylised/processed texture use in a distributed game is allowed. This is the main legal risk.
2. Whether INSPIRE Buildings has height/roof attributes; download one GPKG sample or read the schema.
3. DMR 5.0 second-cycle coverage for Malacky; vertical datum choice; run an actual WCS GetCoverage to confirm clipping works.
4. Parcel ID stability across cadastre updates (affects mod parcel addressing and base-map versioning); data is refreshed continuously (observed version stamp 2026-10-05).
5. No open landuse vector found; ZBGIS vector order terms not read.
6. UGKK general geoportal terms-of-use page was not located; ECDL-type conditions rely on per-service CC BY 4.0 statements. www.geoportal.sk certificate/DNS issues prevented fetching geoportal.sk pages from this environment; skgeodesy.sk mirrors the content.
7. Many other pages (MAPKA limits, rate limits for WFS/WMTS) not documented; no quotas found.
