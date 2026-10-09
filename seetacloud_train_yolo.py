#!/usr/bin/env python3
"""Prepare the generated Darknet dataset and train Ultralytics YOLO."""

import argparse
import faulthandler
import os
import signal
import shutil
import tarfile
from pathlib import Path


faulthandler.enable()
if hasattr(signal, "SIGUSR1"):
    faulthandler.register(signal.SIGUSR1)


def parse_args():
    parser = argparse.ArgumentParser()
    parser.add_argument("--archive", required=True)
    parser.add_argument("--workspace", required=True)
    parser.add_argument("--model", default="yolo11n.pt")
    parser.add_argument("--epochs", type=int, default=100)
    parser.add_argument("--imgsz", type=int, default=640)
    parser.add_argument("--batch", type=int, default=-1)
    return parser.parse_args()


def safe_extract(archive: Path, destination: Path):
    destination = destination.resolve()
    with tarfile.open(archive) as tar:
        for member in tar.getmembers():
            target = (destination / member.name).resolve()
            if destination != target and destination not in target.parents:
                raise ValueError(f"Unsafe archive entry: {member.name}")
        tar.extractall(destination)


def clean_label(source: Path, destination: Path):
    valid_lines = []
    rejected = 0
    for line in source.read_text(encoding="utf-8").splitlines():
        fields = line.split()
        try:
            class_id = int(fields[0])
            values = [float(value) for value in fields[1:]]
            valid = (
                len(fields) == 5
                and class_id >= 0
                and all(0.0 <= value <= 1.0 for value in values)
                and values[2] > 0.0
                and values[3] > 0.0
            )
        except (ValueError, IndexError):
            valid = False
        if valid:
            valid_lines.append(line)
        else:
            rejected += 1
    destination.write_text(
        "\n".join(valid_lines) + ("\n" if valid_lines else ""),
        encoding="utf-8",
    )
    return len(valid_lines), rejected


def prepare_split(list_file: Path, obj_dir: Path, dataset: Path, split: str):
    image_dir = dataset / "images" / split
    label_dir = dataset / "labels" / split
    image_dir.mkdir(parents=True, exist_ok=True)
    label_dir.mkdir(parents=True, exist_ok=True)
    image_count = box_count = rejected_count = 0

    for line in list_file.read_text(encoding="utf-8").splitlines():
        stem = Path(line.strip()).stem
        if not stem:
            continue
        source_image = obj_dir / f"{stem}.jpg"
        source_label = obj_dir / f"{stem}.txt"
        if not source_image.exists() or not source_label.exists():
            raise FileNotFoundError(f"Missing image or label for {stem}")

        target_image = image_dir / source_image.name
        if not target_image.exists():
            try:
                os.link(source_image, target_image)
            except OSError:
                shutil.copy2(source_image, target_image)

        boxes, rejected = clean_label(source_label, label_dir / source_label.name)
        image_count += 1
        box_count += boxes
        rejected_count += rejected

    return image_count, box_count, rejected_count


def main():
    args = parse_args()
    archive = Path(args.archive).resolve()
    workspace = Path(args.workspace).resolve()
    extracted = workspace / "source"
    dataset = workspace / "dataset"
    runs = workspace / "runs"
    extracted.mkdir(parents=True, exist_ok=True)
    dataset.mkdir(parents=True, exist_ok=True)
    runs.mkdir(parents=True, exist_ok=True)

    marker = extracted / ".extracted"
    if not marker.exists():
        print(f"Extracting {archive} to {extracted}", flush=True)
        safe_extract(archive, extracted)
        marker.write_text("ok\n", encoding="utf-8")

    names_files = list(extracted.rglob("obj.names"))
    if len(names_files) != 1:
        raise RuntimeError(f"Expected one obj.names, found {len(names_files)}")
    data_root = names_files[0].parent
    obj_dir = data_root / "obj"
    names = [
        line.strip()
        for line in names_files[0].read_text(encoding="utf-8").splitlines()
        if line.strip()
    ]

    train_stats = prepare_split(data_root / "train.txt", obj_dir, dataset, "train")
    val_stats = prepare_split(data_root / "test.txt", obj_dir, dataset, "val")
    print(f"train images/boxes/rejected: {train_stats}", flush=True)
    print(f"val images/boxes/rejected: {val_stats}", flush=True)

    yaml_path = dataset / "data.yaml"
    yaml_lines = [
        f"path: {dataset}",
        "train: images/train",
        "val: images/val",
        "names:",
    ]
    yaml_lines.extend(f"  {index}: {name}" for index, name in enumerate(names))
    yaml_path.write_text("\n".join(yaml_lines) + "\n", encoding="utf-8")
    print(yaml_path.read_text(encoding="utf-8"), flush=True)

    from ultralytics import YOLO

    run_name = "ms079_yolo11n_640"
    last_checkpoint = runs / run_name / "weights" / "last.pt"
    if last_checkpoint.exists():
        print(f"Resuming {last_checkpoint}", flush=True)
        YOLO(str(last_checkpoint)).train(resume=True)
    else:
        YOLO(args.model).train(
            data=str(yaml_path),
            epochs=args.epochs,
            imgsz=args.imgsz,
            batch=args.batch,
            patience=25,
            device=0,
            workers=8,
            project=str(runs),
            name=run_name,
            exist_ok=True,
            mosaic=0.8,
            close_mosaic=10,
            mixup=0.03,
            copy_paste=0.0,
            fliplr=0.5,
            flipud=0.0,
            degrees=0.0,
            scale=0.5,
        )

    print(f"Best weights: {runs / run_name / 'weights' / 'best.pt'}", flush=True)


if __name__ == "__main__":
    main()
