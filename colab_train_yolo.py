# =============================================================================
# MapleStory detection — Colab training script (Ultralytics YOLO)
#
# Usage:
#   1. On your PC: zip the generator's output `data` folder ->  dataset.zip
#      (the zip should contain: data/obj/*, data/train.txt, data/test.txt,
#       data/obj.names, data/obj.data)
#   2. Upload dataset.zip to your Google Drive (e.g. MyDrive/maplestory/).
#   3. Open a new Colab notebook, set Runtime -> GPU, paste this whole file
#      into one cell, edit the CONFIG block below, and run.
#
# Re-running is safe. If the runtime disconnects, just run the cell again —
# training resumes from the last checkpoint saved on Drive.
# =============================================================================

# ===================== CONFIG (edit these) ==================================
DRIVE_ZIP   = "/content/drive/MyDrive/maplestory/dataset.zip"  # dataset zip on Drive
PROJECT_DIR = "/content/drive/MyDrive/maplestory/runs"         # runs saved here (survives disconnects)
RUN_NAME    = "ms_yolo_v1"

MODEL   = "yolo11s.pt"   # first try: small. bump to yolo11m.pt later if mAP is GPU-bound, not data-bound
EPOCHS  = 100
IMGSZ   = 960            # game frames are 1366x768; 960 keeps small mobs visible. 1280 = better but slower
BATCH   = -1             # -1 = auto-pick from GPU memory
PATIENCE = 25            # early-stop if no improvement for N epochs

# Augmentation — this is where occlusion robustness comes from (see chat notes).
MOSAIC      = 1.0        # 4-image mosaic: main source of crowding/occlusion. Keep on.
MIXUP       = 0.10       # blends 2 images: extra overlap variety
COPY_PASTE  = 0.20       # NOTE: most effective with segmentation masks; darknet output is
                         #       bbox-only, so this helps less here. Mosaic does the heavy lifting.
FLIPLR      = 0.5        # horizontal flip OK
FLIPUD      = 0.0        # never flip vertically — the game is never upside down
DEGREES     = 0.0        # no rotation — game view is axis-aligned
SCALE       = 0.5        # +/- 50% scale jitter
# =============================================================================

import os, sys, glob, zipfile, shutil, subprocess
from pathlib import Path

# ---- 0. environment: Colab detection, Drive mount, install ultralytics ------
try:
    from google.colab import drive
    drive.mount("/content/drive")
    IN_COLAB = True
except ImportError:
    IN_COLAB = False
    print("Not running in Colab — Drive mount skipped.")

subprocess.run([sys.executable, "-m", "pip", "install", "-q", "ultralytics"], check=True)

# ---- 1. unzip dataset -------------------------------------------------------
WORK = Path("/content/ms_dataset")
if WORK.exists():
    shutil.rmtree(WORK)
WORK.mkdir(parents=True)

print(f"Extracting {DRIVE_ZIP} ...")
with zipfile.ZipFile(DRIVE_ZIP) as z:
    z.extractall(WORK)

# ---- 2. locate the darknet `data` root and read class names ----------------
names_files = glob.glob(str(WORK / "**" / "obj.names"), recursive=True)
if not names_files:
    raise FileNotFoundError("obj.names not found in the zip — is this a darknet (-f darknet) output?")
DATA_ROOT = Path(names_files[0]).parent          # the folder that holds obj/, train.txt, obj.names
OBJ_DIR   = DATA_ROOT / "obj"

class_names = [ln.strip() for ln in (DATA_ROOT / "obj.names").read_text(encoding="utf-8").splitlines() if ln.strip()]
print(f"data root : {DATA_ROOT}")
print(f"classes   : {class_names}")

# ---- 3. reorganize into the Ultralytics layout ------------------------------
#   images/train  images/val   labels/train  labels/val
DATASET = Path("/content/dataset")
if DATASET.exists():
    shutil.rmtree(DATASET)
for sub in ("images/train", "images/val", "labels/train", "labels/val"):
    (DATASET / sub).mkdir(parents=True)

def _place(list_file: Path, split: str) -> int:
    """Copy every image+label listed in list_file into images/<split>, labels/<split>."""
    if not list_file.exists():
        return 0
    n = 0
    for line in list_file.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line:
            continue
        stem = Path(line).stem                   # GUID, ignore the path stored in the txt
        jpg, txt = OBJ_DIR / f"{stem}.jpg", OBJ_DIR / f"{stem}.txt"
        if not jpg.exists():
            continue
        for src, dst_dir in ((jpg, DATASET / "images" / split), (txt, DATASET / "labels" / split)):
            dst = dst_dir / src.name
            if src.exists():
                try:
                    os.link(src, dst)            # hardlink = instant, no disk doubling
                except OSError:
                    shutil.copy2(src, dst)
        n += 1
    return n

n_train = _place(DATA_ROOT / "train.txt", "train")
n_val   = _place(DATA_ROOT / "test.txt",  "val")
print(f"train images: {n_train}   val images: {n_val}")

# >>> IMPORTANT: this val set is the generator's RANDOM 5% split. With dense
# >>> step sampling, near-duplicate frames leak across train/val, so the val
# >>> mAP here will be OPTIMISTIC. The real signal is inference on REAL game
# >>> screenshots — see the predict cell at the bottom.

# ---- 4. write data.yaml -----------------------------------------------------
yaml_path = DATASET / "data.yaml"
with open(yaml_path, "w", encoding="utf-8") as f:
    f.write(f"path: {DATASET}\n")
    f.write("train: images/train\n")
    f.write("val: images/val\n")
    f.write("names:\n")
    for i, name in enumerate(class_names):
        f.write(f"  {i}: {name}\n")
print(yaml_path.read_text(encoding="utf-8"))

# ---- 5. train (auto-resume after a disconnect) ------------------------------
from ultralytics import YOLO

last_ckpt = Path(PROJECT_DIR) / RUN_NAME / "weights" / "last.pt"
close_mosaic = min(10, max(0, EPOCHS // 10))

if last_ckpt.exists():
    print(f"Found checkpoint {last_ckpt} — resuming.")
    model = YOLO(str(last_ckpt))
    model.train(resume=True)
else:
    print("No checkpoint — starting a fresh run.")
    model = YOLO(MODEL)
    model.train(
        data=str(yaml_path),
        epochs=EPOCHS, imgsz=IMGSZ, batch=BATCH, patience=PATIENCE,
        project=PROJECT_DIR, name=RUN_NAME, exist_ok=True,
        # augmentation
        mosaic=MOSAIC, close_mosaic=close_mosaic, mixup=MIXUP, copy_paste=COPY_PASTE,
        fliplr=FLIPLR, flipud=FLIPUD, degrees=DEGREES, scale=SCALE,
    )

best = Path(PROJECT_DIR) / RUN_NAME / "weights" / "best.pt"
print(f"\nDone. Best weights: {best}")

# ---- 6. (optional) predict on REAL screenshots ------------------------------
# Upload a few real game screenshots to a Drive folder, then uncomment:
#
# REAL_DIR = "/content/drive/MyDrive/maplestory/real_shots"
# YOLO(str(best)).predict(source=REAL_DIR, imgsz=IMGSZ, conf=0.25, save=True)
# print("Predictions saved under runs/detect/predict — download and eyeball them.")
