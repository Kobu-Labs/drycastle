"""The service layer over synthetic records: fake retrievers, no models, no network."""

from __future__ import annotations

import pytest

from asset_catalog import config, service
from asset_catalog import index as index_mod
from asset_catalog.search.engine import Params
from asset_catalog.search.types import Filters, Hit
from synthetic_records import make


class Fixed:
    """A retriever that returns the same hits for any query, honouring the filters."""

    def __init__(self, name: str, ids: list[str], idx: index_mod.Index):
        self.name, self.ids, self.idx = name, ids, idx

    def retrieve(self, query: str, filters: Filters, k: int) -> list[Hit]:
        ok = self.idx.filter_ids(filters)
        return [
            Hit(id=i, score=1.0 / n, why=[self.name]) for n, i in enumerate(self.ids, 1) if i in ok
        ][:k]


@pytest.fixture
def svc(tmp_path):
    records = [
        make(
            "s/p/chair_01",
            variant_group="chair",
            category="furniture/seating/chair",
            kind="furniture",
        ),
        make(
            "s/p/chair_02",
            variant_group="chair",
            category="furniture/seating/chair",
            kind="furniture",
        ),
        make("s/p/lamp", category="prop/light/lamp", materials=["metal"]),
        make(
            "s/q/crate",
            category="prop/storage/crate",
            materials=["wood"],
            contact_sheet="../secret.png",
        ),
        make(
            "s/q/barrel",
            category="prop/storage/barrel",
            materials=["wood"],
            contact_sheet="r/barrel.png",
        ),
    ]
    path = tmp_path / "data" / "catalog.db"
    path.parent.mkdir(parents=True)
    index_mod.build_db(records, "m", path)
    idx = index_mod.Index(path)
    (tmp_path / "data" / "r").mkdir()
    (tmp_path / "data" / "r" / "barrel.png").write_bytes(b"png")
    (tmp_path / "secret.png").write_bytes(b"x")
    ids = idx.all_ids()
    retrievers = {n: Fixed(n, ids, idx) for n in service.RETRIEVER_NAMES}
    seen = {}

    def similar_fn(asset_id, k, describer, filters, allowed_ids):
        seen["filters"] = filters
        allowed = allowed_ids(filters)
        return [Hit(id=i, score=1.0, why=["x"]) for i in ids if i in allowed][:k]

    s = service.Service(
        idx,
        retrievers,
        "m",
        Params(parse_model=None, rerank_model=None),
        similar_fn,
        tmp_path / "data",
    )
    s.seen = seen
    return s


def test_search_reports_timings_and_eligible(svc):
    resp = svc.search("something", Filters(packs=["p"]), k=10)
    assert {"retrieve", "fuse", "total", "keyword", "text-embedding"} <= set(resp.timings)
    assert "parse" not in resp.timings and "rerank" not in resp.timings
    assert resp.eligible == 3
    assert all(r.record.pack == "p" for r in resp.results)
    assert [r.record.id for r in resp.results].count("s/p/chair_01") == 1  # variants fold
    assert resp.results[0].variants in ([], ["s/p/chair_02"])


def test_search_rejects_empty_input(svc):
    with pytest.raises(ValueError, match="empty"):
        svc.search("  ")
    with pytest.raises(ValueError, match="at least one"):
        svc.search("x", use=[])
    with pytest.raises(ValueError, match="Unknown"):
        svc.search("x", use=["nope"])


def test_use_subset_runs_only_those(svc):
    resp = svc.search("x", use=["keyword"])
    assert "keyword" in resp.timings and "text-embedding" not in resp.timings


def test_missing_key_skips_model_stages_and_says_so(svc, monkeypatch):
    def no_key():
        raise RuntimeError("ANTHROPIC_API_KEY not set")

    monkeypatch.setattr(config, "api_key", no_key)
    svc.params = Params()  # models named, but no key to call them with
    resp = svc.search("x")
    assert resp.notes and "ANTHROPIC_API_KEY" in resp.notes[0]
    assert "parse" not in resp.timings and "rerank" not in resp.timings


def test_facet_counts_ignore_the_facets_own_selection(svc):
    f = svc.facets(Filters(materials=["wood"]))
    assert f.total == 2
    assert f.counts["material"]["metal"] == 1  # what ticking metal as well would add
    assert f.counts["kind"]["prop"] == 2


def test_browse_folds_variants_and_pages(svc):
    page = svc.browse(Filters(), 0, 2)
    assert (page.assets, page.total) == (5, 4)
    assert [r.record.id for r in page.results] == ["s/p/chair_01", "s/p/lamp"]
    assert page.results[0].variants == ["s/p/chair_02"]
    assert [r.record.id for r in svc.browse(Filters(), 2, 2).results] == ["s/q/barrel", "s/q/crate"]


def test_similar_leaves_out_the_asset_and_its_variants(svc):
    out = svc.similar("s/p/chair_01", k=5)
    ids = [r.record.id for r in out.results]
    assert "s/p/chair_01" not in ids and "s/p/chair_02" not in ids and ids
    assert set(svc.seen["filters"].exclude_ids) == {"s/p/chair_01", "s/p/chair_02"}
    with pytest.raises(KeyError):
        svc.similar("s/p/nope")


def test_vocabulary_has_definitions_for_every_filter_facet(svc):
    vocab = svc.vocabulary()
    assert {"kind", "material", "category", "size_class"} <= set(vocab)
    assert vocab["size_class"]["hand"] == "longest side 0 to 0.3 m"
    assert vocab["size_class"]["terrain"].startswith("longest side over")


def test_asset_has_siblings(svc):
    d = svc.asset("s/p/chair_01")
    assert [r.id for r in d.siblings] == ["s/p/chair_02"]


def test_image_path_stays_inside_the_data_dir(svc):
    assert svc.image_path("s/q/barrel", "contact_sheet").name == "barrel.png"
    with pytest.raises(KeyError):
        svc.image_path("s/q/crate", "contact_sheet")  # the record points outside the data dir
    with pytest.raises(KeyError):
        svc.image_path("../../etc/passwd", "contact_sheet")
    with pytest.raises(KeyError):
        svc.image_path("s/q/barrel", "../../x")
    with pytest.raises(FileNotFoundError):
        svc.image_path("s/p/lamp", "contact_sheet")  # c.png was never written
