# MapleStoryDetectionSampleGenerator
Generate Machine Learning Samples Object Detection In MapleStory
![](https://github.com/charlescao460/MapleStoryDetectionSampleGenerator/blob/main/pictures/result.png)



# Performance
This generator can generate arbitrarily many annotated samples. All bounding boxes are precisely annotated based on rendering coordinates.

With [YOLOv4](https://github.com/AlexeyAB/darknet/blob/master/cfg/yolov4-custom.cfg) and ~5000 samples, it can achieve 99.8%mAP in test set.


![](https://github.com/charlescao460/MapleStoryDetectionSampleGenerator/blob/main/pictures/chart_yolov4-custom.png)

# Requirement
* .NET 10.0 SDK (10.0.0 or above)

# Build
1. Clone this repository with submodules by </br> `git clone --recursive git@github.com:charlescao460/MapleStoryDetectionSampleGenerator.git`. </br>Note that `--recursive` is necessary.
2. Build `MapleStoryDetectionSampleGenerator.sln`
3. Run `MapleStory.MachineLearningSampleGenerator\bin\Release\net10.0-windows7.0\WzComparerR2.exe` and open MapRender once. Running `WzComparerR2.exe` will generate `Setting.config`, which is required for our MapRender invoker.

# Run
(Assuming assemblies are built with `Release` configuration. `Debug` configuration is similar)
0. Double-click `MapleStory.MachineLearningSampleGenerator.exe`, or start it
   without arguments, to open the Windows configuration UI. The UI supports
   multiple map, Mob, and Skill IDs; remembers the last configuration; and
   keeps the command-line mode available for batch generation. Use **生成预览**
   to render 1-30 annotated samples with the current settings. The preview
   gallery cycles through alive/hit/dying when combat-state generation is on,
   then returns to the configuration window after it is closed. Preview mode
   writes only to `output/preview/<timestamp>` and does not create or replace a
   formal dataset.
1. Cd into executable directory: `cd MapleStory.MachineLearningSampleGenerator\bin\Release\net10.0-windows7.0`
2. Use `WzComparerR2.exe` to find the desired map you want to sample. Assuming `993134200.img` is the map you want in Limina.
3. Prepare your player PNGs in a directory. </br>Since WzComparerR2 does not have Avatar supported inside MapRender, we have to draw player images in our post-processing steps. Player images should be transparent PNGs with only the player's appearance. You can get these PNGs by Photoshop or save from WzComparerR2's Avatar plugin. Assuming `.\players` is the directory containing all images
4. Run ```.\MapleStory.MachineLearningSampleGenerator.exe -m 993134200 -x 5 -y 5 -f coco -o ".\output" --post --players ".\players"```</br>
This means run the sampler in map 993134200.img with every 5 pixels in X and every 5 pixels in Y, outputing COCO format, and drawing players in post processor. </br>
You can run `.\MapleStory.MachineLearningSampleGenerator.exe --help` for usage hint. Also you can take a look of the entrypoint [Program.cs](https://github.com/charlescao460/MapleStoryDetectionSampleGenerator/blob/main/MapleStory.MachineLearningSampleGenerator/Program.cs)

## Standalone IMG Data Directories

The generator also supports extracted MapleStory data trees whose resources are
stored as standalone binary `.img` files and which do not contain
`Data\Base\Base.wz`. Pass either the client directory or its `Data` directory:

```powershell
.\MapleStory.MachineLearningSampleGenerator.exe `
  -m 104040000 -x 700 -y 500 -f coco `
  -o ".\output" -p "D:\079\绿猫Maple\Data"
```

Resources are opened lazily as each map references them, so the complete
extracted data tree is not loaded into memory. For legacy Chinese strings,
`--encoding gb18030` can be supplied when needed.

To train with private-server monsters whose spawn list differs from the map
data, pass one or more additional Mob IDs. The generator keeps the map's
original mob types and spawn coordinates, merges them with the supplied IDs,
and distributes the combined set across the available mob spawn slots:

```powershell
.\MapleStory.MachineLearningSampleGenerator.exe `
  -m 104040000 -x 700 -y 500 -f coco `
  -o ".\output" -p "D:\079\绿猫Maple\Data" `
  --mobId 210100
```

Multiple additional IDs can be supplied:

```powershell
--mobId 100100 100101 120100
```

For 1366x768 output, camera steps around `-x 200 -y 120` provide enough camera
positions on small and medium maps for varied occlusion and combat-state augmentation.
A `50x50` step still produces many almost-identical screenshots and a much larger dataset. The `-i`/
`--interval` value is not a probabilistic sampling frequency: every camera
position is captured exactly once, then the generator waits that many
milliseconds before moving to the next position. For example, `-i 250` allows
the animation about 250ms to advance after each capture (at most about four
captures per second, excluding render and encoding time).

Every supplied ID must have a corresponding `Mob\xxxxxxx.img` file. The
selected map must have at least as many mob spawn slots as the number of
distinct original and additional Mob IDs combined.

## Target-aware Mob occlusion

Use `--mobOcclusion` to create partially hidden Mob samples without preparing
any external images. Five real client-resource pools are used:

* random equipped players assembled from `Character` WZ/IMG;
* NPC animation frames, preferring NPCs referenced by the requested maps;
* equipment and item icons from `Character` and `Item`, placed on the foothold
  to hide the lower part of a Mob like real ground drops;
* alive animation frames from Mobs present on the requested maps, placed as
  labelled Mob-on-Mob overlap samples;
* foreground sprites referenced by the requested maps from `Map\Obj`.

Players and NPCs stand at native scale on the left or right of the selected
Mob, while ground drops remain near native icon size and overlap the Mob's
feet. Synthetic overlapping Mobs receive their own Mob detection box; the
other foreground occluders are not added as detection classes.

```powershell
.\MapleStory.MachineLearningSampleGenerator.exe `
  -m 104040000 -x 700 -y 500 -f darknet `
  -o "D:\output\datav5" -p "D:\079\绿猫Maple\Data" `
  --mobId 210100 `
  --mobOcclusion
```

Defaults cache 96 players, 48 NPCs, 64 ground drops, 64 current-map Mob frames,
and 24 map objects. Most
generated players use deliberately varied fashion-heavy outfits with randomized
skin, facial expression, hair coverage, hats, capes, gloves, accessories,
shields, and visible cash-weapon variants; a smaller share keeps ordinary
clothing for balance. About 30% of the generated images receive target-aware occlusion covering approximately
8%-35% of a selected Mob's alpha pixels. Ground drops and NPCs receive the
largest sampling weights because they match the common in-game failure cases.
Configure the pools with `--avatarOccluders`, `--npcOccluders`,
`--dropItemOccluders`, `--mobOccluders`, and `--mapOccluders`; configure frequency and strength
with `--occlusionProbability`, `--occlusionMin`, and `--occlusionMax`.

Natural foreground and Skill effects share `--minMobVisible`, which defaults
to 55%. Effect opacity is reduced as necessary so combined layers do not leave
only a tiny Mob fragment. A label is excluded only when an explicitly stronger
custom configuration still falls below that floor. The legacy
`--avatarPreview` switch now saves all five transparent asset pools under
`output/occluder-preview/{player,npc,drop,map}` for inspection; these files are
not included in generated image/label lists.

Combat-state generation also adds a short `Spawning` sweep. It prefers the
Mob's native `regen`, `spawn`, `appear`, or `birth` animation and applies native
renderer alpha between roughly 35%-75%; Mobs without such an action use a
normal Alive frame with the same alpha treatment. These samples keep the
existing `Mob` detection class and are written to the `alive` state directory,
so no runtime class/interface changes are required.

## Hit and death state dataset

Enable `--combatStates` to generate three forced Mob animation sweeps:
`alive`, `hit`, and `dying`. In addition to the normal object-detection output,
the generator writes enlarged Mob crops under `output/mob-state/images` in the
Ultralytics classification layout:

```text
mob-state/
|---images/
|   |---train/alive,hit,dying,unknown/
|   |---val/alive,hit,dying,unknown/
|---manifest.csv
```

Pass multiple actual attacking Skill IDs with `--skillId`. Frames are extracted
from each skill's `hit`, `effect`, and `ball` WZ/IMG nodes. Dying samples receive
a Skill effect by default, including varied fading opacity so the dataset covers
both visible death animation and partially obscured frames without violating
the shared Mob visibility floor. The CSV records the
Mob ID, animation name, frame number, Skill ID, and estimated visible fraction.

Some legacy skills change effect color through visual level branches. Use
`--skillCharacterLevel`; `0` (the UI default) selects the highest available
`CharLevel`/`SkillLevel` branch. For example, this 079 client's skill `2001005`
contains `CharLevel/10`, `15`, `20`, and `25`, so `0` selects `CharLevel/25`.
Setting the value to `18` selects `CharLevel/15`. Skills without these branches
continue to use their normal `hit/effect/ball` nodes.

```powershell
.\MapleStory.MachineLearningSampleGenerator.exe `
  -m 104040000 -x 700 -y 500 -f darknet `
  -o "D:\output\data-combat" -p "D:\079\绿猫Maple\Data" `
  --encoding gb18030 `
  --mobId 210100 `
  --combatStates --combatStateSamples 3 `
  --skillId 1001004 2001005 `
  --skillCharacterLevel 0 `
  --skillEffects 64 `
  --hitEffectProbability 0.8 `
  --dyingEffectProbability 1.0 `
  --mobOcclusion
```

Train the state classifier with `seetacloud_train_mob_state.py`. Its `--data`
argument points to `mob-state/images`. A runtime script should treat `dying`
as a death candidate and confirm death only after the tracked Mob subsequently
disappears; `unknown` must not be interpreted as death.

# Note
* Since NPCs look like players, including them without annotation could result a negative effect on our model. Therefore, by default, we changed [WzComparerR2.MapRender/MapData.cs](https://github.com/Kagamia/WzComparerR2/blob/main/WzComparerR2.MapRender/MapData.cs) to prevent any NPC data loaded into map render when invoking from `MapleStory.MachineLearningSampleGenerator.exe`,

# Output Formats
## Tensorflow TFRecord
According to Tensorflow [official document](https://www.tensorflow.org/tutorials/load_data/tfrecord#tfrecords_format_details), the output .tfrecord contains multiple [tf.train.Example](https://www.tensorflow.org/api_docs/python/tf/train/Example) in single file. With each example store in the following formats:

```
uint64 length
uint32 masked_crc32_of_length
byte   data[length]
uint32 masked_crc32_of_data
```
And
```
masked_crc = ((crc >> 15) | (crc << 17)) + 0xa282ead8ul
```
Each `tf.train.Example` is generated by [protobuf-net](https://github.com/protobuf-net/protobuf-net) according to Tensorflow [example.proto](https://github.com/tensorflow/tensorflow/blob/master/tensorflow/core/example/example.proto)

## Darknet
Output directory structure:
```
data/
|---obj/
|   |---1.jpg
|   |---1.txt
|   |---......
|---obj.data
|---obj.names
|---test.txt
|---train.txt
```
`obj.data` contains 
```
classes=2
train=data/train.txt
valid=data/test.txt
names=data/obj.names
backup = backup/
```
And `obj.names` contains the class name for object. `test.txt` and `train.txt` contains samples for testing/training with ratio of 5:95 (5% of images in `obj/` are used for testing).

## COCO
Output directory structure:
```
coco/
|---train2017/
|   |---1.jpg
|   |---2.jpg
|   |---......
|---val2017/
|   |---1000.jpg
|   |---1001.jpg
|   |---......
|---annotations/
|   |---instances_train2017.json
|   |---instances_val2017.json
```
The COCO json is defined as following:
```json
{
  "info": {
    "description": "MapleStory 993134100.img Object Detection Samples - Training",
    "url": "https://github.com/charlescao460/MapleStoryDetectionSampleGenerator",
    "version": "1.0",
    "year": 2021,
    "contributor": "CSR"
  },
  "licenses": [
    {
      "url": "https://github.com/charlescao460/MapleStoryDetectionSampleGenerator/blob/main/LICENSE",
      "id": 1,
      "name": "MIT License"
    }
  ],
  "images": [
    {
      "license": 1,
      "file_name": "30a892e1-7f3d-4c65-bdd1-9d28f1ae5187.jpg",
      "coco_url": "",
      "height": 768,
      "width": 1366,
      "flickr_url": "",
      "id": 1
    },
    ...],
  "categories": [
    {
      "supercategory": "element",
      "id": 1,
      "name": "Mob"
    },
    {
      "supercategory": "element",
      "id": 2,
      "name": "Player"
    }
  ],
  "annotations": [
    {
      "segmentation": [
        [
          524,
          429,
          664,
          429,
          664,
          578,
          524,
          578
        ]
      ],
      "area": 20860,
      "iscrowd": 0,
      "image_id": 1,
      "bbox": [
        524,
        429,
        140,
        149
      ],
      "category_id": 1,
      "id": 1
    },
    ...]
```
Note that `segmentation` covers the area as the same as `bbox` does. No segmentation or masked implemented .
