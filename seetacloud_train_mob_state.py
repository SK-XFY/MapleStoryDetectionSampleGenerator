#!/usr/bin/env python3
"""Train the generated alive/hit/dying/unknown Mob ROI classifier."""

import argparse
from pathlib import Path


def parse_args():
    parser = argparse.ArgumentParser()
    parser.add_argument("--data", required=True, help="Path to mob-state/images")
    parser.add_argument("--model", default="yolo11n-cls.pt")
    parser.add_argument("--workspace", required=True)
    parser.add_argument("--epochs", type=int, default=80)
    parser.add_argument("--imgsz", type=int, default=160)
    parser.add_argument("--batch", type=int, default=-1)
    return parser.parse_args()


def main():
    args = parse_args()
    data = Path(args.data).resolve()
    workspace = Path(args.workspace).resolve()
    if not (data / "train").is_dir() or not (data / "val").is_dir():
        raise FileNotFoundError(f"Expected train/val class folders under {data}")

    from ultralytics import YOLO

    run_name = "ms079_mob_state_yolo11n_160"
    runs = workspace / "runs"
    last = runs / run_name / "weights" / "last.pt"
    if last.exists():
        YOLO(str(last)).train(resume=True)
    else:
        YOLO(args.model).train(
            data=str(data),
            epochs=args.epochs,
            imgsz=args.imgsz,
            batch=args.batch,
            patience=20,
            device=0,
            workers=8,
            project=str(runs),
            name=run_name,
            exist_ok=True,
            fliplr=0.5,
            flipud=0.0,
            degrees=0.0,
            scale=0.25,
            erasing=0.15,
        )
    print(f"Best weights: {runs / run_name / 'weights' / 'best.pt'}", flush=True)


if __name__ == "__main__":
    main()
