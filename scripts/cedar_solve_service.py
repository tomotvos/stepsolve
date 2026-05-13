#!/usr/bin/env python3
"""
Cedar plate solver service for StepSolve.

Long-running subprocess: loads the Cedar catalog once at startup, then
processes solve requests over stdin/stdout JSON pipes.

Protocol
--------
Startup (stdout):   {"ready": true}
Request (stdin):    {"image_path": "...", "ra_hint": null, "dec_hint": null, "radius_deg": null}
Response (stdout):  {"ra_deg": 0.0, "dec_deg": 0.0, "confidence": 0.0, "solve_time_ms": 0.0}
Error (stdout):     {"ra_deg": 0.0, ..., "error": "description"}

Usage: python3 cedar_solve_service.py <index_path>
"""

import sys
import json
import time


def main():
    index_path = sys.argv[1] if len(sys.argv) > 1 else ""

    try:
        import cedar_detect  # pip install cedar-detect
    except ImportError as e:
        print(json.dumps({"ready": False, "error": f"cedar_detect not installed: {e}"}), flush=True)
        sys.exit(1)

    try:
        solver = cedar_detect.CedarSolver(index_path)
    except Exception as e:
        print(json.dumps({"ready": False, "error": f"Failed to load Cedar catalog: {e}"}), flush=True)
        sys.exit(1)

    print(json.dumps({"ready": True}), flush=True)

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        start = time.monotonic()
        try:
            req = json.loads(line)
            image_path = req["image_path"]
            ra_hint = req.get("ra_hint")
            dec_hint = req.get("dec_hint")
            radius_deg = req.get("radius_deg")

            result = solver.solve(
                image_path,
                ra_hint=ra_hint,
                dec_hint=dec_hint,
                radius_deg=radius_deg,
            )

            elapsed_ms = (time.monotonic() - start) * 1000

            if result and getattr(result, "ra_deg", None) is not None:
                print(json.dumps({
                    "ra_deg": float(result.ra_deg),
                    "dec_deg": float(result.dec_deg),
                    "confidence": float(getattr(result, "confidence", 0.0)),
                    "solve_time_ms": elapsed_ms,
                }), flush=True)
            else:
                print(json.dumps({
                    "ra_deg": 0.0,
                    "dec_deg": 0.0,
                    "confidence": 0.0,
                    "solve_time_ms": elapsed_ms,
                    "error": "no solution",
                }), flush=True)

        except Exception as e:
            elapsed_ms = (time.monotonic() - start) * 1000
            print(json.dumps({
                "ra_deg": 0.0,
                "dec_deg": 0.0,
                "confidence": 0.0,
                "solve_time_ms": elapsed_ms,
                "error": str(e),
            }), flush=True)


if __name__ == "__main__":
    main()
