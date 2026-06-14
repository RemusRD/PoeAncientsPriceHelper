#!/usr/bin/env python3
"""
Prototype detector for PoE2 Runeshape list geometry.

This is intentionally a screenshot-only tool. It does not read process memory,
hook the game client, sniff packets, or send input. The goal is to test whether
a future Windows implementation can replace manual calibration with:

  PoE client rect -> approximate runeshape region -> row-frame detection

The script writes annotated PNGs and a JSON summary for each input image.
"""

from __future__ import annotations

import argparse
import json
from collections import Counter
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Iterable

import cv2
import numpy as np


@dataclass(frozen=True)
class Rect:
    x: int
    y: int
    width: int
    height: int

    def clip(self, image_width: int, image_height: int) -> "Rect":
        x = max(0, min(self.x, image_width - 1))
        y = max(0, min(self.y, image_height - 1))
        right = max(x + 1, min(self.x + self.width, image_width))
        bottom = max(y + 1, min(self.y + self.height, image_height))
        return Rect(x, y, right - x, bottom - y)


@dataclass
class Detection:
    path: str
    image_width: int
    image_height: int
    search_region: Rect
    method: str
    row_pitch: int | None
    boundary_y: list[int]
    row_center_y: list[int]
    text_center_y: list[int]
    confidence: float
    annotated_path: str


# Profiles from Barragek0/RuneshapePriceChecker, kept here only as a prototype
# comparison point. In the app these should be expressed relative to the PoE2
# client rect, not the desktop.
PROFILES: dict[tuple[int, int], Rect] = {
    (1600, 900): Rect(43, 128, 414, 447),
    (1920, 1080): Rect(52, 154, 497, 536),
    (2560, 1440): Rect(69, 205, 663, 715),
    (3440, 1440): Rect(69, 205, 663, 715),
    (3840, 2160): Rect(104, 308, 994, 1072),
}


def parse_rect(value: str) -> Rect:
    parts = [int(p.strip()) for p in value.split(",")]
    if len(parts) != 4:
        raise argparse.ArgumentTypeError("region must be x,y,width,height")
    return Rect(*parts)


def parse_size(value: str) -> tuple[int, int]:
    try:
        w, h = value.lower().split("x", 1)
        return int(w), int(h)
    except ValueError as exc:
        raise argparse.ArgumentTypeError("size must be WIDTHxHEIGHT") from exc


def interpolate_profile(client_width: int, client_height: int) -> Rect | None:
    exact = PROFILES.get((client_width, client_height))
    if exact:
        return exact

    profiles = sorted(PROFILES.items(), key=lambda item: item[0][0] * item[0][1])
    target_pixels = client_width * client_height
    lower = None
    upper = None
    for size, rect in profiles:
        pixels = size[0] * size[1]
        if pixels <= target_pixels:
            lower = (size, rect)
        if pixels >= target_pixels and upper is None:
            upper = (size, rect)

    if lower is None:
        return upper[1] if upper else None
    if upper is None or lower[0] == upper[0]:
        return lower[1]

    (lw, lh), lr = lower
    (uw, uh), ur = upper
    tx = 0.0 if uw == lw else (client_width - lw) / (uw - lw)
    ty = 0.0 if uh == lh else (client_height - lh) / (uh - lh)
    tx = min(1.0, max(0.0, tx))
    ty = min(1.0, max(0.0, ty))
    return Rect(
        round(lr.x + (ur.x - lr.x) * tx),
        round(lr.y + (ur.y - lr.y) * ty),
        round(lr.width + (ur.width - lr.width) * tx),
        round(lr.height + (ur.height - lr.height) * ty),
    )


def default_search_region(image_width: int, image_height: int) -> Rect:
    # The runeshape panel is left-anchored in the real screenshots. Cap the
    # search width so item tooltips and quest text do not dominate the row score.
    width = min(700, max(360, round(image_width * 0.65)))
    height = min(image_height, round(image_height * 0.92))
    return Rect(0, 0, width, height)


def smooth(values: np.ndarray, window: int = 5) -> np.ndarray:
    kernel = np.ones(window, dtype=np.float32) / window
    return np.convolve(values, kernel, mode="same")


def find_score_peaks(score: np.ndarray, y_offset: int) -> list[int]:
    smoothed = smooth(score.astype(np.float32), 5)
    threshold = max(
        float(np.percentile(smoothed, 88)),
        float(smoothed.mean() + smoothed.std() * 0.7),
    )

    peaks: list[tuple[int, float]] = []
    for i in range(3, len(smoothed) - 3):
        window = smoothed[i - 3 : i + 4]
        if smoothed[i] >= threshold and smoothed[i] == window.max():
            peaks.append((y_offset + i, float(smoothed[i])))

    merged: list[tuple[int, float]] = []
    for y, value in peaks:
        if not merged or y - merged[-1][0] > 12:
            merged.append((y, value))
        elif value > merged[-1][1]:
            merged[-1] = (y, value)
    return [y for y, _ in merged]


def candidate_pitches(peaks: Iterable[int]) -> list[int]:
    values = sorted(peaks)
    diffs: list[int] = []
    for i, y in enumerate(values):
        for z in values[i + 1 :]:
            diff = z - y
            if 30 <= diff <= 95:
                diffs.append(round(diff / 4) * 4)

    common = [pitch for pitch, _ in Counter(diffs).most_common(8)]
    return common or [40]


def longest_chain(peaks: list[int], pitch: int) -> list[int]:
    tolerance = max(7, round(pitch * 0.22))
    best: list[int] = []
    for start in peaks:
        chain = [start]
        current = start
        while True:
            candidates = [
                peak
                for peak in peaks
                if peak > current + tolerance and abs(peak - (current + pitch)) <= tolerance
            ]
            if not candidates:
                break
            current = min(candidates, key=lambda peak: abs(peak - (current + pitch)))
            chain.append(current)
        if len(chain) > len(best):
            best = chain
    return best


def infer_rows(peaks: list[int]) -> tuple[int | None, list[int], list[int], float]:
    peaks = sorted(peaks)
    best_pitch: int | None = None
    best_boundaries: list[int] = []

    for pitch in candidate_pitches(peaks):
        chain = longest_chain(peaks, pitch)
        if len(chain) > len(best_boundaries):
            best_pitch = pitch
            best_boundaries = chain

    if len(best_boundaries) >= 4:
        centers = [(a + b) // 2 for a, b in zip(best_boundaries, best_boundaries[1:])]
        gaps = [b - a for a, b in zip(best_boundaries, best_boundaries[1:])]
        spread = float(np.std(gaps)) if len(gaps) > 1 else 0.0
        confidence = min(1.0, len(centers) / 10.0) * max(0.25, 1.0 - spread / 18.0)
        return best_pitch, best_boundaries, centers, confidence

    centers: list[int] = []
    for a, b in zip(peaks, peaks[1:]):
        if 28 <= b - a <= 90:
            centers.append((a + b) // 2)
    confidence = min(0.45, len(centers) / 20.0)
    return None, peaks, centers, confidence


def detect_rows(image: np.ndarray, region: Rect) -> tuple[str, int | None, list[int], list[int], float]:
    crop = image[region.y : region.y + region.height, region.x : region.x + region.width]
    gray = cv2.cvtColor(crop, cv2.COLOR_BGR2GRAY)

    sobel_y = np.abs(cv2.Sobel(gray, cv2.CV_32F, 0, 1, ksize=3))
    edge_peaks = find_score_peaks(sobel_y.mean(axis=1), region.y)
    edge_pitch, edge_boundaries, edge_centers, edge_confidence = infer_rows(edge_peaks)

    dark_peaks = find_score_peaks(255 - gray.mean(axis=1), region.y)
    dark_pitch, dark_boundaries, dark_centers, dark_confidence = infer_rows(dark_peaks)

    if dark_confidence > edge_confidence + 0.10:
        return "dark-row-projection", dark_pitch, dark_boundaries, dark_centers, dark_confidence

    return "horizontal-edge-projection", edge_pitch, edge_boundaries, edge_centers, edge_confidence


def refine_centers_to_text(
    image: np.ndarray,
    region: Rect,
    boundaries: list[int],
    fallback_centers: list[int],
) -> list[int]:
    if len(boundaries) < 2:
        return fallback_centers

    gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
    refined: list[int] = []
    for index, (top_boundary, bottom_boundary) in enumerate(zip(boundaries, boundaries[1:])):
        fallback = fallback_centers[index] if index < len(fallback_centers) else (top_boundary + bottom_boundary) // 2
        gap = bottom_boundary - top_boundary
        if gap < 24:
            refined.append(fallback)
            continue

        top = max(0, top_boundary + max(8, round(gap * 0.18)))
        bottom = min(gray.shape[0], bottom_boundary - max(8, round(gap * 0.14)))
        left = region.x + round(region.width * 0.45)
        right = min(gray.shape[1], region.x + region.width + 220)
        crop = gray[top:bottom, left:right]
        if crop.size == 0:
            refined.append(fallback)
            continue

        threshold = min(120.0, float(np.percentile(crop, 25)) + 8.0)
        mask = crop < threshold
        score = mask.sum(axis=1).astype(np.float32)
        # Row borders and creases are long horizontal marks; text is more broken up.
        score[score > crop.shape[1] * 0.38] = 0
        if float(score.max()) < max(4.0, crop.shape[1] * 0.008):
            refined.append(fallback)
            continue

        refined.append(top + int(np.argmax(smooth(score, 3))))
    return refined


def annotate(
    image: np.ndarray,
    region: Rect,
    boundaries: list[int],
    centers: list[int],
    text_centers: list[int],
    label: str,
    output_path: Path,
) -> None:
    annotated = image.copy()
    cv2.rectangle(
        annotated,
        (region.x, region.y),
        (region.x + region.width, region.y + region.height),
        (0, 180, 255),
        2,
    )
    for y in boundaries:
        cv2.line(annotated, (region.x, y), (region.x + region.width, y), (255, 160, 0), 1)
    for index, y in enumerate(centers, start=1):
        cv2.line(annotated, (region.x, y), (region.x + region.width, y), (60, 255, 80), 2)
        cv2.putText(
            annotated,
            str(index),
            (region.x + 8, max(20, y - 4)),
            cv2.FONT_HERSHEY_SIMPLEX,
            0.55,
            (60, 255, 80),
            2,
            cv2.LINE_AA,
        )
    for y in text_centers:
        cv2.line(annotated, (region.x, y), (region.x + region.width, y), (255, 80, 255), 1)
    cv2.putText(
        annotated,
        label,
        (region.x + 8, max(24, region.y + 24)),
        cv2.FONT_HERSHEY_SIMPLEX,
        0.58,
        (0, 180, 255),
        2,
        cv2.LINE_AA,
    )
    output_path.parent.mkdir(parents=True, exist_ok=True)
    cv2.imwrite(str(output_path), annotated)


def output_name(path: Path, suffix: str) -> str:
    stem = "".join(ch if ch.isalnum() or ch in "-_" else "_" for ch in path.stem)
    return f"{stem}-{suffix}.png"


def run(args: argparse.Namespace) -> int:
    out_dir = Path(args.out_dir)
    results: list[Detection] = []

    for input_path in args.images:
        image_path = Path(input_path)
        image = cv2.imread(str(image_path))
        if image is None:
            raise SystemExit(f"Could not read image: {image_path}")

        height, width = image.shape[:2]
        if args.region:
            region = args.region.clip(width, height)
            region_source = "manual-region"
        elif args.client_size:
            profile = interpolate_profile(*args.client_size)
            if profile is None:
                region = default_search_region(width, height)
                region_source = "auto-left-search"
            else:
                region = profile.clip(width, height)
                region_source = f"profile-{args.client_size[0]}x{args.client_size[1]}"
        else:
            region = default_search_region(width, height)
            region_source = "auto-left-search"

        method, pitch, boundaries, centers, confidence = detect_rows(image, region)
        text_centers = refine_centers_to_text(image, region, boundaries, centers)
        suffix = region_source.replace(",", "_")
        annotated_path = out_dir / output_name(image_path, suffix)
        label = f"{region_source} / {method} / rows={len(centers)}"
        annotate(image, region, boundaries, centers, text_centers, label, annotated_path)

        results.append(
            Detection(
                path=str(image_path),
                image_width=width,
                image_height=height,
                search_region=region,
                method=method,
                row_pitch=pitch,
                boundary_y=boundaries,
                row_center_y=centers,
                text_center_y=text_centers,
                confidence=round(confidence, 3),
                annotated_path=str(annotated_path),
            )
        )

    summary_path = out_dir / "runeshape-row-detection-summary.json"
    summary_path.write_text(
        json.dumps(
            [
                {
                    **asdict(result),
                    "search_region": asdict(result.search_region),
                }
                for result in results
            ],
            indent=2,
        )
        + "\n",
        encoding="utf-8",
    )

    print(json.dumps({"summary": str(summary_path), "results": [asdict(r) for r in results]}, indent=2))
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description="Detect Runeshape row centers in screenshots.")
    parser.add_argument("images", nargs="+", help="Screenshot paths")
    parser.add_argument(
        "--out-dir",
        default="/tmp/poe-ancients-samples/row-detector",
        help="Directory for annotated PNGs and JSON summary",
    )
    parser.add_argument("--region", type=parse_rect, help="Use explicit x,y,width,height search region")
    parser.add_argument(
        "--client-size",
        type=parse_size,
        help="Use prototype resolution profile for a PoE2 client size, e.g. 1920x1080",
    )
    return run(parser.parse_args())


if __name__ == "__main__":
    raise SystemExit(main())
