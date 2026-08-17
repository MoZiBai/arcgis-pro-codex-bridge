from __future__ import annotations

import subprocess
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
RUNTIME_CONTRACTS = {
    "application_integration_contract_test.py",
    "attribute_rule_evaluation_contract_test.py",
    "framework_registry_contract_test.py",
    "geoprocessing_registry_contract_test.py",
    "policy_reload_contract_test.py",
    "sdk_registry_contract_test.py",
}


def main() -> int:
    tests = sorted(
        test
        for test in (ROOT / "scripts").glob("*_contract_test.py")
        if test.name not in RUNTIME_CONTRACTS
    )
    if not tests:
        raise RuntimeError("No Python contract tests were found")
    for test in tests:
        print(f"[RUN] {test.name}", flush=True)
        completed = subprocess.run(
            [sys.executable, str(test)],
            cwd=ROOT,
            check=False,
        )
        if completed.returncode != 0:
            return completed.returncode
    print(f"source contract suite ok: {len(tests)} Python contracts")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
