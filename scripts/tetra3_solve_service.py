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


def _solve_with_sigma_retry(t3, image, solve_kwargs: dict) -> tuple[dict | None, int, int]:
    """Try to solve starting at sigma=3, adapting on failure based on centroid count.

    Clean images have few centroids and may need sigma lowered; noisy images
    (obstructions, foliage) have hundreds and need it raised. Centroid count at
    the starting sigma guides which direction to retry.

    Sigma schedule:
      - Start:           sigma=3  (biased towards clean images)
      - Too many (>100): sigma=5, then sigma=6
      - Too few   (<10): sigma=2

    Returns (result, sigma_used, attempts).
    """
    _SIGMA_START      = 3
    _SIGMA_SPARSE     = 2    # retry when centroids are very few
    _SIGMA_NOISY      = 5    # retry when centroids are very many
    _SIGMA_VERY_NOISY = 6    # second retry for extremely noisy images
    _CENTROID_HIGH    = 100  # above this → image is too noisy at current sigma
    _CENTROID_LOW     = 10   # below this → image is too sparse at current sigma

    sigma = _SIGMA_START
    attempts = 1
    result = t3.solve_from_image(image, sigma=sigma, **solve_kwargs)
    if result and result.get("RA") is not None:
        return result, sigma, attempts

    try:
        n = len(tetra3.get_centroids_from_image(image, sigma=_SIGMA_START))
    except Exception:
        return result, sigma, attempts

    if n > _CENTROID_HIGH:
        sigma = _SIGMA_NOISY
        attempts += 1
        result = t3.solve_from_image(image, sigma=sigma, **solve_kwargs)
        if result and result.get("RA") is not None:
            return result, sigma, attempts
        sigma = _SIGMA_VERY_NOISY
        attempts += 1
        result = t3.solve_from_image(image, sigma=sigma, **solve_kwargs)
    elif n < _CENTROID_LOW:
        sigma = _SIGMA_SPARSE
        attempts += 1
        result = t3.solve_from_image(image, sigma=sigma, **solve_kwargs)

    return result, sigma, attempts


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

            # Fixed tuning constants (from parameter sweep across demo images).
            # fov_max_error is absolute degrees; 8° gives enough window for the
            # solver's internal scale definition to differ from our diagonal FOV.
            # pattern_checking_stars=12 is needed for the noisiest images.
            solve_kwargs = {"fov_max_error": 8.0, "pattern_checking_stars": 12}
            fov = req.get("fov_estimate_deg")
            if fov:
                solve_kwargs["fov_estimate"] = fov

            result, sigma_used, attempts = _solve_with_sigma_retry(t3, image, solve_kwargs)
            elapsed_ms = (time.monotonic() - start) * 1000

            if result and result.get("RA") is not None:
                print(json.dumps({
                    "ra_deg": float(result["RA"]),
                    "dec_deg": float(result["Dec"]),
                    "confidence": 1.0,
                    "solve_time_ms": elapsed_ms,
                    "sigma_used": sigma_used,
                    "attempts": attempts,
                }), flush=True)
            else:
                try:
                    n_centroids = len(tetra3.get_centroids_from_image(image, sigma=3))
                except Exception:
                    n_centroids = "error"
                print(json.dumps({
                    "ra_deg": 0.0,
                    "dec_deg": 0.0,
                    "confidence": 0.0,
                    "solve_time_ms": elapsed_ms,
                    "sigma_used": sigma_used,
                    "attempts": attempts,
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
