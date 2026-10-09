# 数据集生成与训练笔记

> 记录数据集生成、训练策略的相关决策与待办。需要时在此基础上继续探讨。
> 最后更新:2026-05-22

## 1. 已确定的决策

### 模型策略:所有地图合训一个模型
- **不**按地图单独训练,所有地图数据喂同一个模型。
- 理由:目标检测靠"忽略背景、聚焦目标"泛化;多地图背景多样性是免费的数据增强;
  玩家/NPC/传送门等元素跨地图通用;几百个 per-map 模型工程上无法维护。
- 冒险岛地图差异大 → 这个结论比一般情况更成立(差异 = 训练信号,不是障碍)。

### 逐步加图时,数据集必须累积
- 工作流上"先一张图、后续再加图"没问题。
- **但每次训练喂的是累积数据集**(图1+图2+…+图N 全部),不是只喂新图。
- 只喂新图 → 灾难性遗忘(catastrophic forgetting),模型会忘掉旧地图。
- 每张图的生成数据要留着别删。

### 数据集规模
- 关键指标是**每类实例数**,不是总图数。
  - 跑通管线:300~500/类;像样可用:1000~2000/类;较强:5000+/类。
- 合成数据图数几乎免费 → **堆多样性,别堆数量**(动画帧、缩放、翻转、对象数量、遮挡、背景)。
- 第一张图目标 ~1000~1500 张。
- 磁盘不是问题:JPG 单张 ~150~300KB,全地图(假设 100 图)也就几十 GB,不是 TB 级。

### 采样步长
- 命令行 `-x` / `-y`,二维网格步进。数量 ∝ 1/步长²。
- 公式:`新数量 ≈ 旧数量 × (旧步长/新步长)²`。
- step 只移动相机,不改场景内容 → 步长太小(如 5)产生大量近重复图。
- **当前选定:`-x 20 -y 20`**(2 万多张 → ~1250 张/图)。
- `-i`(interval)用来给动画/姿态多样性:每次截图间留时间让怪动起来。
  **当前选定:`-i 200`**。`-i 0` 太快会导致启动黑屏(渲染没画完就截图)。

### 输出格式:darknet
- 训练用 Ultralytics YOLO → 用 `-f darknet`(YOLO txt 格式),省掉 COCO→YOLO 转换。

### 训练环境:Google Colab + Ultralytics YOLO
- 脚本:[`colab_train_yolo.py`](./colab_train_yolo.py)(自包含,贴进 Colab 单元格即可)。
- 遮挡鲁棒性靠**训练时增强**:Mosaic 为主力;MixUp 辅助。
  `copy_paste` 在纯 bbox 数据上效果有限(更吃分割掩码),不指望它。
- Colab 注意:数据集打包成 zip 上传 Drive;run 输出存 Drive 以便断线 `resume`。

## 2. 已知问题与坑

### train/val 划分有泄漏
- 工具自带随机 5% 划分(`CocoWriter` / `DarknetWriter` 里 `DefaultTestingPortion`)。
- 步长 20 仍有近重复,随机划分会让近重复图分到 train/val 两侧 → **val mAP 虚高**。
- darknet 输出文件名是 GUID、不含坐标 → 下游无法做"按区域划分"。
- **应对**:合成数据全部当训练集;**val/test 用真实游戏截图手工标**。真实截图才是真标尺。

### 多次运行不会合并,会互相覆盖
- `CocoWriter` / `DarknetWriter` 构造时 `CleanDirectory` 会清空输出目录。
- 多张地图要合并 → **一条命令里 `-m id1 id2 id3` 一次跑完**(自动合并)。
- 分多次跑务必用**不同的 `-o` 目录**,否则后一次覆盖前一次。

### 类别是固定枚举,但 ID 按出现顺序编号
- `ObjectClass`:`Mob=1, Player, Npc, InMapPortal, CrossMapPortal, Foothold, LadderRope, Unknown`。
- 是**通用类检测**(检测"一只怪"),不是按怪物种类分。
- 写出的类别 ID 是 `_occurrenceClasses.IndexOf(type)`,按出现顺序 → **不同运行间 ID 不一致**,
  跨次数据集无法直接合并。

### 类别不平衡
- `Foothold` / `LadderRope` 每图实例极多,可能淹没 `Mob`。训练时留意,必要时排除这些类。

### 重叠度低
- 当前怪按 WZ 固定出生点渲染,重叠/遮挡少;真实打怪场景重叠是常态。
- 第一版靠 Mosaic 增强补;不够再改生成器(见下)。

## 3. 待探讨 / 未来改动(需要时再做)

### 跨次运行的数据集累加
- 几百张图一个个测,无法一次 `-m` 全列上。三条路:
  - **darknet + 改 writer**:改 writer 让类别 ID 用固定枚举值(`IndexOf(type)` → `(int)type`),
    之后合并 = 拼文件夹,YOLO `data.yaml` 列多个路径,无需合并工具。**最省事。**
  - COCO + 改 writer + 简化合并工具(只重编号 id)。
  - COCO + 只写完整合并工具(按类别 name 重映射 ID)。

### 让样本真正独立(解决泄漏 + 重叠,一举两得)
当前顺序 x-y 网格扫描,相邻帧近重复。两个层面的改动:
- **随机相机跳跃**(改 `Sampler.SampleAll` 的双重 for → 随机取 N 个 (x,y)):
  只随机化取景框,缓解系统性近重复,但**不根治**(随机点仍可能很近)。
- **随机怪物位置**(每帧重新摆怪):让样本内容真正独立 → 随机划分不再泄漏,
  顺便造出重叠。这才是根治。两个实现路线:
  - **路线 A**:改 `WzComparerR2.MapRender` 渲染逻辑(改子模块;项目本就维护其补丁)。
    最真实,但改动深。
  - **路线 B(推荐)**:写 `MobProcessor` 后处理器,在已渲染帧上随机贴怪 sprite。
    模板现成 = `MapleStory.Sampler/PostProcessor/PlayerProcessor.cs`。不碰子模块,工作量小。
    对检测训练足够(YOLO Copy-Paste 增强本质就是这事)。

### 决策原则
- 以上都**先看第一轮训练效果再定**。若合成数据 + Mosaic 已能在真实截图上出框,则都不用做。

## 4. 相关文件
- `colab_train_yolo.py` —— Colab 训练脚本(Ultralytics YOLO)。
- `MapleStory.Sampler/Sampler.cs` —— `SampleAll`,网格采样逻辑。
- `MapleStory.Sampler/DarknetWriter.cs` / `CocoWriter.cs` —— 数据集写出。
- `MapleStory.Sampler/PostProcessor/PlayerProcessor.cs` —— 后处理器模板。
- `MapRender.Invoker/ObjectClass.cs` —— 类别枚举定义。
