#!/usr/bin/env python3
"""
tetra3_tune.py — sweep Tetra3 parameters to find settings that solve all demo images.

Runs a grid of fov_max_error × sigma × pattern_checking_stars for each image and
reports every successful solve, so you can identify a single robust set of defaults
to bake into tetra3_solve_service.py.

Key insight: fov_max_error is in absolute degrees; sigma controls centroid extraction
sensitivity (lower = more stars / more noise, higher = fewer stars / cleaner detections).

Usage:
    python3 tetra3_tune.py [--db PATH] [--images DIR] [--verbose]

Defaults use the dev-environment venv database and the in-tree demo image directory.
"""

import logging
logging.disable(logging.INFO)  # suppress tetra3's verbose load/solve messages

import argparse
import itertools
import math
import sys
import time
from pathlib import Path

import numpy as np
if not hasattr(np, 'math'):
    np.math = math

# Astrometry ground-truth FOV (width × height degrees) for each demo image.
# Diagonal FOV is used as fov_estimate since that is what Tetra3 expects.
IMAGE_FOVS: dict[str, tuple[float, float]] = {
    "demo.jpg":         (22.849,   17.2545),
    "aquarius.jpg":     (14.3522,   8.09267),
    "orionsShield.jpg": (14.356,    8.10747),
    "coronaB.jpg":      (22.212,   11.2259),
}

# Default sweep grids — override via CLI flags
DEFAULT_FOV_MAX_ERROR = [1.0, 2.0, 3.0, 5.0, 8.0]   # degrees
DEFAULT_SIGMA         = [2, 3, 4, 5, 6]               # std-devs above background
DEFAULT_PATTERN_STARS = [6, 8, 10, 12]                # stars tried for pattern


def diagonal_fov(w: float, h: float) -> float:
    return math.sqrt(w * w + h * h)


def probe_centroids(t3_module, image, sigmas: list[int]) -> dict:
    """Return {sigma: centroid_count} using only centroid extraction (no solve)."""
    counts = {}
    for s in sigmas:
        try:
            cents = t3_module.get_centroids_from_image(image, sigma=s)
            counts[s] = len(cents)
        except Exception as e:
            counts[s] = f"err({e})"
    return counts


def try_solve(t3, image, fov_deg: float, fov_max_error: float,
              sigma: int, pattern_checking_stars: int) -> tuple[bool, float, float, float]:
    """Attempt one solve. Returns (success, ra_deg, dec_deg, elapsed_ms)."""
    t0 = time.monotonic()
    try:
        result = t3.solve_from_image(
            image,
            fov_estimate=fov_deg,
            fov_max_error=fov_max_error,
            pattern_checking_stars=pattern_checking_stars,
            sigma=sigma,
        )
        elapsed = (time.monotonic() - t0) * 1000
        if result and result.get("RA") is not None:
            return True, float(result["RA"]), float(result["Dec"]), elapsed
        return False, 0.0, 0.0, elapsed
    except Exception as e:
        elapsed = (time.monotonic() - t0) * 1000
        if args_verbose:
            print(f"        exception: {e}")
        return False, 0.0, 0.0, elapsed


# Module-level flag so try_solve can read it without threading through every call
args_verbose = False


def main():
    global args_verbose

    parser = argparse.ArgumentParser(
        description=__doc__,
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    parser.add_argument(
        "--db",
        default="/Users/tomo/.stepsolve-dev-venv/lib/python3.13/site-packages/tetra3/data/default_database",
        help="Path to Tetra3 database (with or without .npz extension)",
    )
    parser.add_argument(
        "--images",
        default="/Users/tomo/Development/solve/stepsolve/src/wwwroot/demo",
        help="Directory containing demo images",
    )
    parser.add_argument(
        "--fov-max-error", type=float, nargs="+", metavar="DEG",
        default=DEFAULT_FOV_MAX_ERROR,
        help="fov_max_error values to sweep in degrees (default: 1 2 3 5 8)",
    )
    parser.add_argument(
        "--sigma", type=int, nargs="+", metavar="N",
        default=DEFAULT_SIGMA,
        help="sigma values to sweep for centroid extraction (default: 2 3 4 5 6)",
    )
    parser.add_argument(
        "--pattern-stars", type=int, nargs="+", metavar="N",
        default=DEFAULT_PATTERN_STARS,
        help="pattern_checking_stars values to sweep (default: 6 8 10 12)",
    )
    parser.add_argument(
        "--verbose", action="store_true",
        help="Print every failed combo (very noisy but useful for debugging)",
    )
    args = parser.parse_args()
    args_verbose = args.verbose

    try:
        import tetra3
        from PIL import Image as PILImage
    except ImportError as e:
        sys.exit(f"ERROR: missing dependency — {e}")

    print(f"Loading Tetra3 database: {args.db}")
    t0 = time.monotonic()
    t3 = tetra3.Tetra3(args.db)
    print(f"Loaded in {(time.monotonic() - t0) * 1000:.0f} ms\n")
    t3_module = tetra3  # module-level get_centroids_from_image

    image_dir = Path(args.images)
    total_combos = len(args.fov_max_error) * len(args.sigma) * len(args.pattern_stars)
    summary: list[tuple[str, list[dict]]] = []

    for name, (w_deg, h_deg) in IMAGE_FOVS.items():
        path = image_dir / name
        if not path.exists():
            print(f"=== {name}: NOT FOUND at {path}, skipping ===\n")
            continue

        diag = diagonal_fov(w_deg, h_deg)
        print(f"=== {name} ===")
        print(f"    FOV  {w_deg:.4f} x {h_deg:.4f} deg  |  diagonal = {diag:.3f} deg")

        image = PILImage.open(path)

        centroid_counts = probe_centroids(t3_module, image, args.sigma)
        print("    Centroids by sigma:", "  ".join(f"σ{s}→{c}" for s, c in centroid_counts.items()))

        print(f"    Sweeping {total_combos} combos "
              f"({len(args.fov_max_error)} fov_max_error × {len(args.sigma)} sigma × {len(args.pattern_stars)} pattern_stars) …")

        solves: list[dict] = []
        attempts = 0

        for fme, sigma, pstars in itertools.product(args.fov_max_error, args.sigma, args.pattern_stars):
            attempts += 1
            ok, ra, dec, elapsed = try_solve(t3, image, diag, fme, sigma, pstars)
            if ok:
                entry = dict(fov_max_error=fme, sigma=sigma, pattern_checking_stars=pstars,
                             ra=ra, dec=dec, elapsed_ms=elapsed, attempt=attempts)
                solves.append(entry)
                print(f"    SOLVED  fov_max_error={fme:4.1f}°  sigma={sigma}  pattern_stars={pstars:2d}"
                      f"  →  RA={ra:.4f}  Dec={dec:.4f}  ({elapsed:.0f} ms)")
            elif args.verbose:
                print(f"        fail   fov_max_error={fme:4.1f}°  sigma={sigma}  pattern_stars={pstars:2d}"
                      f"  ({elapsed:.0f} ms)")

        if not solves:
            print(f"    NO SOLVE across all {attempts} combos")
        summary.append((name, solves))
        print()

    # ── Summary ──────────────────────────────────────────────────────────────
    print("=" * 72)
    print("SUMMARY")
    print("=" * 72)
    for name, solves in summary:
        if solves:
            best = solves[0]  # first (tightest fov_max_error, lowest sigma, fewest stars)
            print(f"  SOLVED  {name}")
            print(f"          best: fov_max_error={best['fov_max_error']}°  sigma={best['sigma']}"
                  f"  pattern_checking_stars={best['pattern_checking_stars']}")
            print(f"          RA={best['ra']:.4f}  Dec={best['dec']:.4f}"
                  f"  ({len(solves)} combo(s) solved this image)")
        else:
            print(f"  FAILED  {name}")

    # Suggest a single shared config if everything solved
    all_solved = all(solves for _, solves in summary)
    if all_solved:
        print()
        print("All images solved. To find a single param set that works for all:")
        print("  Look for a (fov_max_error, sigma, pattern_stars) combo that appears")
        print("  in every image's SOLVED lines above.")


if __name__ == "__main__":
    main()
