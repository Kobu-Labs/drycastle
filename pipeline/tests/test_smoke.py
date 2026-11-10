import pytest

from suchohrad_pipeline import cli
from suchohrad_pipeline.stages import STAGES

EXPECTED_ORDER = ["fetch", "normalise", "parcels", "rules", "terrain", "roads", "props", "export"]


def test_stage_order() -> None:
    assert list(STAGES) == EXPECTED_ORDER


@pytest.fixture
def fake_fetch(monkeypatch: pytest.MonkeyPatch) -> list[bool]:
    calls: list[bool] = []

    def run(force: bool = False, **_options: object) -> int:
        calls.append(force)
        print("fetch: fake")
        return 0

    def fake_stage(name: str):
        def stage(**_options: object) -> int:
            print(f"{name}: fake")
            return 0

        return stage

    monkeypatch.setitem(STAGES, "fetch", run)
    for name in ("normalise", "parcels", "rules", "terrain", "roads", "props", "export"):
        monkeypatch.setitem(STAGES, name, fake_stage(name))
    return calls


def test_run_all_runs_every_stage_in_order(
    fake_fetch: list[bool], capsys: pytest.CaptureFixture[str]
) -> None:
    assert cli.main(["run-all"]) == 0
    lines = capsys.readouterr().out.strip().splitlines()
    assert lines == [f"{s}: fake" for s in EXPECTED_ORDER]


def test_force_flag_reaches_fetch(fake_fetch: list[bool]) -> None:
    assert cli.main(["fetch", "--force"]) == 0
    assert cli.main(["fetch"]) == 0
    assert fake_fetch == [True, False]


def test_cli_stage_names_match_stages() -> None:
    assert list(cli.STAGE_NAMES) == list(STAGES)
