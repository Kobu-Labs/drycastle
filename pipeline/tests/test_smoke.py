import subprocess
import sys

from suchohrad_pipeline.stages import STAGES

EXPECTED_ORDER = ["fetch", "normalise", "parcels", "rules", "terrain", "roads", "props", "export"]


def test_stage_order() -> None:
    assert list(STAGES) == EXPECTED_ORDER


def test_run_all_runs_every_stage_in_order() -> None:
    result = subprocess.run(
        [sys.executable, "-m", "suchohrad_pipeline", "run-all"],
        capture_output=True,
        text=True,
        check=False,
    )
    assert result.returncode == 0, result.stderr
    lines = result.stdout.strip().splitlines()
    assert lines == [f"{stage}: not implemented yet" for stage in EXPECTED_ORDER]


def test_single_stage_prints_stub_message() -> None:
    result = subprocess.run(
        [sys.executable, "-m", "suchohrad_pipeline", "fetch"],
        capture_output=True,
        text=True,
        check=False,
    )
    assert result.returncode == 0, result.stderr
    assert result.stdout == "fetch: not implemented yet\n"
