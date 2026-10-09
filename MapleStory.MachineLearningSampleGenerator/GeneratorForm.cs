using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;

namespace MapleStory.MachineLearningSampleGenerator
{
    internal sealed class GeneratorForm : Form
    {
        private readonly TextBox _dataPath = new TextBox();
        private readonly TextBox _outputPath = new TextBox();
        private readonly TextBox _maps = new TextBox();
        private readonly TextBox _mobIds = new TextBox();
        private readonly TextBox _skillIds = new TextBox();
        private readonly TextBox _encoding = new TextBox();
        private readonly ComboBox _format = new ComboBox();
        private readonly NumericUpDown _xStep = IntegerBox(1, 100000, 200);
        private readonly NumericUpDown _yStep = IntegerBox(1, 100000, 120);
        private readonly NumericUpDown _width = IntegerBox(320, 7680, 1366);
        private readonly NumericUpDown _height = IntegerBox(240, 4320, 768);
        private readonly NumericUpDown _interval = IntegerBox(0, 60000, 100);
        private readonly NumericUpDown _previewSamples = IntegerBox(1, 30, 3);
        private readonly CheckBox _combatStates = new CheckBox { Text = "生成 alive / hit / dying 状态分类集", Checked = true, AutoSize = true };
        private readonly NumericUpDown _stateSamples = IntegerBox(1, 10, 3);
        private readonly NumericUpDown _skillEffects = IntegerBox(1, 1000, 64);
        private readonly NumericUpDown _skillCharacterLevel = IntegerBox(0, 300, 0);
        private readonly NumericUpDown _hitEffectProbability = RatioBox(0.80M);
        private readonly NumericUpDown _dyingEffectProbability = RatioBox(1.00M);
        private readonly CheckBox _mobOcclusion = new CheckBox { Text = "加入角色和地图前景遮挡", Checked = true, AutoSize = true };
        private readonly CheckBox _avatarPreview = new CheckBox { Text = "保存四类遮挡素材预览", Checked = false, AutoSize = true };
        private readonly NumericUpDown _avatarOccluders = IntegerBox(1, 1000, 96);
        private readonly NumericUpDown _npcOccluders = IntegerBox(0, 1000, 48);
        private readonly NumericUpDown _dropItemOccluders = IntegerBox(0, 1000, 64);
        private readonly NumericUpDown _mobOccluders = IntegerBox(0, 1000, 64);
        private readonly NumericUpDown _mapOccluders = IntegerBox(0, 1000, 24);
        private readonly NumericUpDown _occlusionProbability = RatioBox(0.30M);
        private readonly NumericUpDown _occlusionMin = RatioBox(0.08M, 0.01M);
        private readonly NumericUpDown _occlusionMax = RatioBox(0.35M, 0.01M);
        private readonly NumericUpDown _minimumVisible = RatioBox(0.55M, 0.01M);
        private string[] _arguments;

        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MapleStorySampleGenerator",
            "ui-settings.json");

        public GeneratorForm()
        {
            Text = "MapleStory 079 训练数据生成器";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(820, 720);
            Size = new Size(920, 850);
            Font = new Font("Microsoft YaHei UI", 9F);

            _format.DropDownStyle = ComboBoxStyle.DropDownList;
            _format.Items.AddRange(new object[] { "darknet", "coco", "tfrecord" });
            _format.SelectedIndex = 0;
            _encoding.Text = "gb18030";
            _dataPath.Text = @"D:\079\绿猫Maple\Data";
            _outputPath.Text = @"D:\output\data-combat";

            var page = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                Padding = new Padding(16),
                ColumnCount = 3,
                RowCount = 1
            };
            page.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
            page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            page.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            Controls.Add(page);

            int row = 0;
            AddHeader(page, ref row, "基础配置");
            AddPathRow(page, ref row, "079 Data 目录", _dataPath, false);
            AddPathRow(page, ref row, "输出目录", _outputPath, true);
            AddRow(page, ref row, "地图 ID（多个用空格）", _maps);
            AddRow(page, ref row, "额外 Mob ID（多个用空格）", _mobIds);
            AddRow(page, ref row, "技能 ID（多个用空格）", _skillIds);
            AddRow(page, ref row, "IMG 字符编码", _encoding);
            AddRow(page, ref row, "检测集格式", _format);

            AddHeader(page, ref row, "地图采样");
            AddRow(page, ref row, "横向步长（推荐 200）", _xStep);
            AddRow(page, ref row, "纵向步长（推荐 120）", _yStep);
            AddRow(page, ref row, "画面宽度", _width);
            AddRow(page, ref row, "画面高度", _height);
            AddRow(page, ref row, "每次截图后的等待（毫秒）", _interval);
            AddHint(page, ref row,
                "相机每移动一个 XY 步长都会截图；此值只控制截图后等待多久，让怪物动画继续播放。默认 100ms，兼顾动画差异和生成速度。\n" +
                "1366×768 建议 XY=200×120，可为中小地图保留足够的基础视角；50×50 会产生大量高度重复画面。");
            AddRow(page, ref row, "预览图片数量", _previewSamples);

            AddHeader(page, ref row, "命中与死亡状态");
            AddWideRow(page, ref row, _combatStates);
            AddRow(page, ref row, "每种状态动画采样数", _stateSamples);
            AddRow(page, ref row, "缓存技能特效帧数", _skillEffects);
            AddRow(page, ref row, "技能特效角色等级（0=最高）", _skillCharacterLevel);
            AddHint(page, ref row,
                "部分旧版技能在 CharLevel/SkillLevel 下按等级换颜色。0 会使用最高视觉分支；例如 2001005 会选择 CharLevel/25。若填写 18，则选择 CharLevel/15。");
            AddRow(page, ref row, "受击叠加技能概率", _hitEffectProbability);
            AddRow(page, ref row, "死亡叠加技能概率", _dyingEffectProbability);

            AddHeader(page, ref row, "遮挡增强");
            _mobOcclusion.Text = "加入角色、NPC、掉落物、怪物重叠和地图前景遮挡";
            AddWideRow(page, ref row, _mobOcclusion);
            AddHint(page, ref row,
                "掉落装备/道具遮挡怪物下半身；任务 NPC、玩家和真实怪物帧从左右遮挡。重叠怪物也会生成 Mob 标注，不会成为漏标。\n" +
                "约 30% 图片加入自然遮挡；30 张预览会按该比例排出约 9 张遮挡，并确保每种可用素材至少出现一次。正式数据保持随机自然比例。");
            AddWideRow(page, ref row, _avatarPreview);
            AddRow(page, ref row, "随机角色素材数", _avatarOccluders);
            AddRow(page, ref row, "任务 NPC 素材数", _npcOccluders);
            AddRow(page, ref row, "地面掉落物素材数", _dropItemOccluders);
            AddRow(page, ref row, "怪物重叠素材数", _mobOccluders);
            AddRow(page, ref row, "地图前景素材数", _mapOccluders);
            AddRow(page, ref row, "普通遮挡概率", _occlusionProbability);
            AddRow(page, ref row, "最小遮挡比例", _occlusionMin);
            AddRow(page, ref row, "最大遮挡比例", _occlusionMax);
            AddRow(page, ref row, "Mob 最低可见比例", _minimumVisible);

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                Padding = new Padding(0, 14, 0, 14)
            };
            var start = new Button { Text = "开始生成", Width = 120, Height = 36 };
            var preview = new Button { Text = "生成预览", Width = 120, Height = 36 };
            var save = new Button { Text = "保存配置", Width = 110, Height = 36 };
            var cancel = new Button { Text = "取消", Width = 90, Height = 36 };
            start.Click += Start_Click;
            preview.Click += Preview_Click;
            save.Click += (_, __) => SaveSettings(showConfirmation: true);
            cancel.Click += (_, __) => { DialogResult = DialogResult.Cancel; Close(); };
            buttons.Controls.Add(start);
            buttons.Controls.Add(preview);
            buttons.Controls.Add(save);
            buttons.Controls.Add(cancel);
            page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            page.Controls.Add(buttons, 0, row);
            page.SetColumnSpan(buttons, 3);

            AcceptButton = start;
            CancelButton = cancel;
            LoadSettings();
        }

        public string[] BuildCommandLineArguments()
        {
            return _arguments ?? Array.Empty<string>();
        }

        private void Start_Click(object sender, EventArgs e)
        {
            PrepareAndClose(previewOnly: false);
        }

        private void Preview_Click(object sender, EventArgs e)
        {
            PrepareAndClose(previewOnly: true);
        }

        private void PrepareAndClose(bool previewOnly)
        {
            try
            {
                if (!Directory.Exists(_dataPath.Text.Trim()))
                {
                    throw new DirectoryNotFoundException("079 Data 目录不存在。");
                }
                if (Tokens(_maps.Text).Length == 0)
                {
                    throw new ArgumentException("至少填写一个地图 ID。");
                }
                if (_mobOcclusion.Checked && _occlusionMin.Value >= _occlusionMax.Value)
                {
                    throw new ArgumentException("最小遮挡比例必须小于最大遮挡比例。");
                }
                Directory.CreateDirectory(_outputPath.Text.Trim());
                _arguments = BuildArguments(previewOnly).ToArray();
                SaveSettings(showConfirmation: false);
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, exception.Message, "配置错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private IEnumerable<string> BuildArguments(bool previewOnly)
        {
            yield return "--map";
            foreach (string map in Tokens(_maps.Text)) yield return map;
            yield return "--xStep"; yield return ((int)_xStep.Value).ToString();
            yield return "--yStep"; yield return ((int)_yStep.Value).ToString();
            yield return "--format"; yield return _format.Text;
            yield return "--output"; yield return _outputPath.Text.Trim();
            yield return "--path"; yield return _dataPath.Text.Trim();
            yield return "--width"; yield return ((int)_width.Value).ToString();
            yield return "--height"; yield return ((int)_height.Value).ToString();
            yield return "--interval"; yield return ((int)_interval.Value).ToString();
            if (previewOnly)
            {
                yield return "--previewSamples";
                yield return ((int)_previewSamples.Value).ToString();
            }
            if (!string.IsNullOrWhiteSpace(_encoding.Text))
            {
                yield return "--encoding"; yield return _encoding.Text.Trim();
            }
            string[] mobs = Tokens(_mobIds.Text);
            if (mobs.Length > 0)
            {
                yield return "--mobId";
                foreach (string id in mobs) yield return id;
            }
            string[] skills = Tokens(_skillIds.Text);
            if (skills.Length > 0)
            {
                yield return "--skillId";
                foreach (string id in skills) yield return id;
            }
            if (_combatStates.Checked)
            {
                yield return "--combatStates";
                yield return "--combatStateSamples"; yield return ((int)_stateSamples.Value).ToString();
                yield return "--skillEffects"; yield return ((int)_skillEffects.Value).ToString();
                yield return "--skillCharacterLevel"; yield return ((int)_skillCharacterLevel.Value).ToString();
                yield return "--hitEffectProbability"; yield return DecimalText(_hitEffectProbability.Value);
                yield return "--dyingEffectProbability"; yield return DecimalText(_dyingEffectProbability.Value);
            }
            if (_mobOcclusion.Checked)
            {
                yield return "--mobOcclusion";
                yield return "--avatarOccluders"; yield return ((int)_avatarOccluders.Value).ToString();
                yield return "--npcOccluders"; yield return ((int)_npcOccluders.Value).ToString();
                yield return "--dropItemOccluders"; yield return ((int)_dropItemOccluders.Value).ToString();
                yield return "--mobOccluders"; yield return ((int)_mobOccluders.Value).ToString();
                yield return "--mapOccluders"; yield return ((int)_mapOccluders.Value).ToString();
                yield return "--occlusionProbability"; yield return DecimalText(_occlusionProbability.Value);
                yield return "--occlusionMin"; yield return DecimalText(_occlusionMin.Value);
                yield return "--occlusionMax"; yield return DecimalText(_occlusionMax.Value);
                yield return "--minMobVisible"; yield return DecimalText(_minimumVisible.Value);
                if (_avatarPreview.Checked) yield return "--avatarPreview";
            }
        }

        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;
                UiSettings value = JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(SettingsPath));
                if (value == null) return;
                _dataPath.Text = value.DataPath ?? _dataPath.Text;
                _outputPath.Text = value.OutputPath ?? _outputPath.Text;
                _maps.Text = value.Maps ?? string.Empty;
                _mobIds.Text = value.MobIds ?? string.Empty;
                _skillIds.Text = value.SkillIds ?? string.Empty;
                _encoding.Text = value.Encoding ?? "gb18030";
                _format.SelectedItem = value.Format ?? "darknet";
                if (value.Version < 2)
                {
                    value.XStep = 600;
                    value.YStep = 350;
                }
                if (value.Version < 3)
                {
                    value.Interval = 100;
                    value.PreviewSamples = 3;
                    value.StateSamples = 3;
                    value.SkillEffects = 64;
                    value.SkillCharacterLevel = 0;
                    value.HitEffectProbability = 0.80M;
                    value.DyingEffectProbability = 1.00M;
                    value.AvatarOccluders = 48;
                    value.MapOccluders = 48;
                    value.OcclusionProbability = 0.40M;
                    value.OcclusionMin = 0.10M;
                    value.OcclusionMax = 0.45M;
                    value.MinimumVisible = 0.35M;
                }
                if (value.Version < 4)
                {
                    value.NpcOccluders = 48;
                    value.DropItemOccluders = 64;
                    value.MapOccluders = 24;
                    value.OcclusionProbability = 0.50M;
                    value.OcclusionMin = 0.08M;
                    value.OcclusionMax = 0.35M;
                    value.MinimumVisible = 0.55M;
                }
                if (value.Version < 5)
                {
                    value.AvatarOccluders = 96;
                }
                if (value.Version < 6)
                {
                    value.OcclusionProbability = 0.30M;
                }
                if (value.Version < 7)
                {
                    value.MobOccluders = 64;
                }
                if (value.Version < 8 && value.XStep == 600M && value.YStep == 350M)
                {
                    value.XStep = 200M;
                    value.YStep = 120M;
                }
                NormalizeOcclusionSettings(value);
                SetValue(_xStep, value.XStep); SetValue(_yStep, value.YStep);
                SetValue(_width, value.Width); SetValue(_height, value.Height);
                SetValue(_interval, value.Interval);
                SetValue(_previewSamples, value.PreviewSamples);
                _combatStates.Checked = value.CombatStates;
                SetValue(_stateSamples, value.StateSamples); SetValue(_skillEffects, value.SkillEffects);
                SetValue(_skillCharacterLevel, value.SkillCharacterLevel);
                SetValue(_hitEffectProbability, value.HitEffectProbability);
                SetValue(_dyingEffectProbability, value.DyingEffectProbability);
                _mobOcclusion.Checked = value.MobOcclusion;
                _avatarPreview.Checked = value.AvatarPreview;
                SetValue(_avatarOccluders, value.AvatarOccluders);
                SetValue(_npcOccluders, value.NpcOccluders);
                SetValue(_dropItemOccluders, value.DropItemOccluders);
                SetValue(_mobOccluders, value.MobOccluders);
                SetValue(_mapOccluders, value.MapOccluders);
                SetValue(_occlusionProbability, value.OcclusionProbability);
                SetValue(_occlusionMin, value.OcclusionMin);
                SetValue(_occlusionMax, value.OcclusionMax);
                SetValue(_minimumVisible, value.MinimumVisible);
            }
            catch
            {
                // Invalid or old settings should not prevent the UI from opening.
            }
        }

        private void SaveSettings(bool showConfirmation)
        {
            var value = new UiSettings
            {
                Version = 8,
                DataPath = _dataPath.Text,
                OutputPath = _outputPath.Text,
                Maps = _maps.Text,
                MobIds = _mobIds.Text,
                SkillIds = _skillIds.Text,
                Encoding = _encoding.Text,
                Format = _format.Text,
                XStep = _xStep.Value, YStep = _yStep.Value,
                Width = _width.Value, Height = _height.Value, Interval = _interval.Value,
                PreviewSamples = _previewSamples.Value,
                CombatStates = _combatStates.Checked,
                StateSamples = _stateSamples.Value, SkillEffects = _skillEffects.Value,
                SkillCharacterLevel = _skillCharacterLevel.Value,
                HitEffectProbability = _hitEffectProbability.Value,
                DyingEffectProbability = _dyingEffectProbability.Value,
                MobOcclusion = _mobOcclusion.Checked, AvatarPreview = _avatarPreview.Checked,
                AvatarOccluders = _avatarOccluders.Value,
                NpcOccluders = _npcOccluders.Value,
                DropItemOccluders = _dropItemOccluders.Value,
                MobOccluders = _mobOccluders.Value,
                MapOccluders = _mapOccluders.Value,
                OcclusionProbability = _occlusionProbability.Value,
                OcclusionMin = _occlusionMin.Value, OcclusionMax = _occlusionMax.Value,
                MinimumVisible = _minimumVisible.Value
            };
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
            File.WriteAllText(SettingsPath,
                JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
            if (showConfirmation)
            {
                MessageBox.Show(this, "配置已保存。", "完成",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private static void AddHeader(TableLayoutPanel page, ref int row, string text)
        {
            var label = new Label
            {
                Text = text,
                AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold),
                Padding = new Padding(0, 14, 0, 6)
            };
            page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            page.Controls.Add(label, 0, row);
            page.SetColumnSpan(label, 3);
            row++;
        }

        private static void AddRow(TableLayoutPanel page, ref int row, string labelText, Control control)
        {
            var label = new Label { Text = labelText, AutoSize = true, Anchor = AnchorStyles.Left };
            control.Dock = DockStyle.Fill;
            control.Margin = new Padding(3, 5, 3, 5);
            page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            page.Controls.Add(label, 0, row);
            page.Controls.Add(control, 1, row);
            page.SetColumnSpan(control, 2);
            row++;
        }

        private static void AddWideRow(TableLayoutPanel page, ref int row, Control control)
        {
            control.Margin = new Padding(3, 6, 3, 6);
            page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            page.Controls.Add(control, 1, row);
            page.SetColumnSpan(control, 2);
            row++;
        }

        private static void AddHint(TableLayoutPanel page, ref int row, string text)
        {
            var label = new Label
            {
                Text = text,
                AutoSize = true,
                MaximumSize = new Size(620, 0),
                ForeColor = Color.DimGray,
                Padding = new Padding(0, 2, 0, 8)
            };
            page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            page.Controls.Add(label, 1, row);
            page.SetColumnSpan(label, 2);
            row++;
        }

        private void AddPathRow(TableLayoutPanel page, ref int row, string labelText, TextBox box, bool output)
        {
            var label = new Label { Text = labelText, AutoSize = true, Anchor = AnchorStyles.Left };
            box.Dock = DockStyle.Fill;
            var browse = new Button { Text = "浏览…", Dock = DockStyle.Fill };
            browse.Click += (_, __) =>
            {
                using (var dialog = new FolderBrowserDialog
                {
                    Description = output ? "选择数据集输出目录" : "选择 MapleStory 079 Data 目录",
                    SelectedPath = box.Text
                })
                {
                    if (dialog.ShowDialog(this) == DialogResult.OK) box.Text = dialog.SelectedPath;
                }
            };
            page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            page.Controls.Add(label, 0, row);
            page.Controls.Add(box, 1, row);
            page.Controls.Add(browse, 2, row);
            row++;
        }

        private static NumericUpDown IntegerBox(int min, int max, int value)
        {
            return new NumericUpDown { Minimum = min, Maximum = max, Value = value, ThousandsSeparator = true };
        }

        private static NumericUpDown RatioBox(decimal value, decimal minimum = 0M)
        {
            return new NumericUpDown
            {
                Minimum = minimum, Maximum = 1, DecimalPlaces = 2, Increment = 0.05M, Value = value
            };
        }

        private static void NormalizeOcclusionSettings(UiSettings value)
        {
            bool invalidRange = value.OcclusionMin <= 0M || value.OcclusionMin >= 1M ||
                                value.OcclusionMax <= 0M || value.OcclusionMax > 1M ||
                                value.OcclusionMin >= value.OcclusionMax;
            bool invalidVisible = value.MinimumVisible <= 0M || value.MinimumVisible > 1M;

            if (!invalidRange && !invalidVisible) return;

            value.OcclusionProbability = 0.30M;
            value.OcclusionMin = 0.08M;
            value.OcclusionMax = 0.35M;
            value.MinimumVisible = 0.55M;
        }

        private static string[] Tokens(string text)
        {
            return (text ?? string.Empty).Split(
                new[] { ' ', '\t', '\r', '\n', ',', ';', '，' },
                StringSplitOptions.RemoveEmptyEntries);
        }

        private static string DecimalText(decimal value)
        {
            return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static void SetValue(NumericUpDown box, decimal value)
        {
            box.Value = Math.Max(box.Minimum, Math.Min(box.Maximum, value));
        }

        private sealed class UiSettings
        {
            public int Version { get; set; }
            public string DataPath { get; set; }
            public string OutputPath { get; set; }
            public string Maps { get; set; }
            public string MobIds { get; set; }
            public string SkillIds { get; set; }
            public string Encoding { get; set; }
            public string Format { get; set; }
            public decimal XStep { get; set; } = 200;
            public decimal YStep { get; set; } = 120;
            public decimal Width { get; set; } = 1366;
            public decimal Height { get; set; } = 768;
            public decimal Interval { get; set; } = 100;
            public decimal PreviewSamples { get; set; } = 3;
            public bool CombatStates { get; set; } = true;
            public decimal StateSamples { get; set; } = 3;
            public decimal SkillEffects { get; set; } = 64;
            public decimal SkillCharacterLevel { get; set; }
            public decimal HitEffectProbability { get; set; } = 0.80M;
            public decimal DyingEffectProbability { get; set; } = 1.0M;
            public bool MobOcclusion { get; set; } = true;
            public bool AvatarPreview { get; set; }
            public decimal AvatarOccluders { get; set; } = 96;
            public decimal NpcOccluders { get; set; } = 48;
            public decimal DropItemOccluders { get; set; } = 64;
            public decimal MobOccluders { get; set; } = 64;
            public decimal MapOccluders { get; set; } = 24;
            public decimal OcclusionProbability { get; set; } = 0.30M;
            public decimal OcclusionMin { get; set; } = 0.08M;
            public decimal OcclusionMax { get; set; } = 0.35M;
            public decimal MinimumVisible { get; set; } = 0.55M;
        }
    }
}
