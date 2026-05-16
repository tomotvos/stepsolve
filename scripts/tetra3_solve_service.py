#!/usr/bin/env python3
"""
Tetra3 plate solver service for StepSolve.

Long-running subprocess: loads the Tetra3 database once at startup, then
processes solve requests over stdin/stdout JSON pipes.

Protocol
--------
Startup (stdout):   {"ready": true}
Request (stdin):    {"image_path": "...", "ra_hint": null, "dec_hint": null, "radius_deg": null}
Response (stdout):  {"ra_deg": 0.0, "dec_deg": 0.0, "confidence": 0.0, "solve_time_ms": 0.0}
Error (stdout):     {"ra_deg": 0.0, ..., "error": "description"}

Usage: python3 tetra3_solve_service.py <database_path>
"""

import sys
import json
import math
import time

# numpy 2.0 removed numpy.math (it was always just an alias for the built-in
# math module). Restore it before tetra3 imports numpy so the library works
# on both numpy 1.x and 2.x.
import numpy as np
if not hasattr(np, 'math'):
    np.math = math


def main():
    database_path = sys.argv[1] if len(sys.argv) > 1 else "tetra3_database"

    try:
        import tetra3  # pip install tetra3
    except ImportError as e:
        print(json.dumps({"ready": False, "error": f"tetra3 not installed: {e}"}), flush=True)
        sys.exit(1)

    try:
        t3 = tetra3.Tetra3(database_path)
    except Exception as e:
        print(json.dumps({"ready": False, "error": f"Failed to load Tetra3 database: {e}"}), flush=True)
        sys.exit(1)

    try:
        from PIL import Image as PILImage
    except ImportError:
        PILImage = None

    print(json.dumps({"ready": True}), flush=True)

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        start = time.monotonic()
        try:
            req = json.loads(line)
            image_path = req["image_path"]

            if PILImage is None:
                raise RuntimeError("Pillow not installed; cannot open image")

            image = PILImage.open(image_path)

            solve_kwargs = {}
            fov = req.get("fov_estimate_deg")
            if fov:
                solve_kwargs["fov_estimate"] = fov
                solve_kwargs["fov_max_error"] = fov * 0.5

            result = t3.solve_from_image(image, **solve_kwargs)
            elapsed_ms = (time.monotonic() - start) * 1000

            if result and result.get("RA") is not None:
                print(json.dumps({
                    "ra_deg": float(result["RA"]),
                    "dec_deg": float(result["Dec"]),
                    "confidence": 1.0,
                    "solve_time_ms": elapsed_ms,
                }), flush=True)
            else:
                try:
                    diag = t3.get_centroids_from_image(image)
                    n_centroids = len(diag)
                except Exception:
                    n_centroids = "error"
                print(json.dumps({
                    "ra_deg": 0.0,
                    "dec_deg": 0.0,
                    "confidence": 0.0,
                    "solve_time_ms": elapsed_ms,
                    "error": f"no solution (centroids={n_centroids})",
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
