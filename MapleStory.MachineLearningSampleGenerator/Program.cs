using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommandLine;
using CommandLine.Text;
using MapleStory.Common;
using MapleStory.Sampler;
using MapleStory.Sampler.PostProcessor;
using MapRender.Invoker;

namespace MapleStory.MachineLearningSampleGenerator
{
    internal static class Program
    {
        [DllImport("kernel32.dll")]
        static extern bool SetDllDirectory(string path);

        [DllImport("kernel32")]
        static extern bool AllocConsole();

        [DllImport("kernel32.dll")]
        static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll")]
        static extern bool FreeConsole();

        [DllImport("kernel32.dll")]
        static extern IntPtr GetConsoleWindow();

        private static bool _hasConsole;
        private static bool _lastRunWasPreview;

        static Program()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            string libPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Lib",
                Environment.Is64BitProcess ? "x64" : "x86");
            SetDllDirectory(libPath); // Add dll search path for WzComparerR2
        }

        private class Options
        {
            [Option('m', "map", Required = true, HelpText = "Space-separated Wz image ID of map(s) used for generating TFRecord.")]
            public IEnumerable<string> Maps { get; set; }

            [Option('x', "xStep", Required = false, Default = 200, HelpText = "Camera step in X.")]
            public int StepX { get; set; }

            [Option('y', "yStep", Required = false, Default = 120, HelpText = "Camera step in Y.")]
            public int StepY { get; set; }

            [Option('f', "format", Required = true, HelpText = "Output format, must be one of: [tfrecord, darknet, coco]")]
            public string Format { get; set; }

            [Option('o', "output", Required = false, Default = ".", HelpText = "Data set output location")]
            public string OutputPath { get; set; }

            [Option('w', "width", Required = false, Default = 1366, HelpText = "Width of sample image.")]
            public int RenderWidth { get; set; }

            [Option('h', "height", Required = false, Default = 768, HelpText = "Height of sample image.")]
            public int RenderHeight { get; set; }

            [Option('i', "interval", Required = false, Default = 100, HelpText = "Time interval in ms between each sample")]
            public int SampleInterval { get; set; }

            [Option("previewSamples", Required = false, Default = 0, HelpText =
                "Generate this many annotated previews without writing the formal dataset (0 disables preview mode).")]
            public int PreviewSamples { get; set; }

            [Option('p', "path", Required = false, Default = "", HelpText = "MapleStory Installed Path")]
            public string MapleStoryPath { get; set; }

            [Option("post", Required = false, Default = false, HelpText = "Indicate whether to enable post-processing.")]
            public bool PostProcessingEnable { get; set; }

            [Option("players", Required = false, Default = "", HelpText = "Directory where the post-processing player images stored.")]
            public string PlayerImageDirectory { get; set; }

            [Option('e', "encoding", Required = false, HelpText = "Encoding used to decode Wz strings. Using system default if not specified.")]
            public string Encoding { get; set; } = "";

            [Option("mobId", Required = false, Separator = ' ', HelpText =
                "Optional space-separated Mob IDs. Adds these IDs to the map's existing mob types.")]
            public IEnumerable<int> MobIds { get; set; } = Enumerable.Empty<int>();

            [Option("mobOcclusion", Required = false, Default = false, HelpText =
                "Enable target-aware Mob occlusion using random avatars assembled from Character WZ/IMG.")]
            public bool MobOcclusion { get; set; }

            [Option("avatarOccluders", Required = false, Default = 96, HelpText =
                "Number of random Character WZ avatars cached as Mob occluders.")]
            public int AvatarOccluderCount { get; set; }

            [Option("avatarPreview", Required = false, Default = false, HelpText =
                "Save generated avatar/NPC/drop/map occluder PNGs under output/occluder-preview.")]
            public bool AvatarPreview { get; set; }

            [Option("npcOccluders", Required = false, Default = 48, HelpText =
                "Maximum number of real NPC WZ animation frames mixed into side occlusion.")]
            public int NpcOccluderCount { get; set; }

            [Option("dropItemOccluders", Required = false, Default = 64, HelpText =
                "Number of Item/Character WZ icons cached as lower-body ground-drop occluders.")]
            public int DropItemOccluderCount { get; set; }

            [Option("mobOccluders", Required = false, Default = 64, HelpText =
                "Maximum number of labelled current-map Mob frames used for Mob-on-Mob overlap.")]
            public int MobOccluderCount { get; set; }

            [Option("mapOccluders", Required = false, Default = 24, HelpText =
                "Maximum number of current-map WZ object sprites mixed into Mob occlusion.")]
            public int MapOccluderCount { get; set; }

            [Option("occlusionProbability", Required = false, Default = 0.30, HelpText =
                "Probability that an image receives one or two Mob occlusions (0-1).")]
            public double OcclusionProbability { get; set; }

            [Option("occlusionMin", Required = false, Default = 0.08, HelpText =
                "Minimum fraction of a Mob bounding box covered by an avatar (0-1).")]
            public double MinimumOcclusion { get; set; }

            [Option("occlusionMax", Required = false, Default = 0.35, HelpText =
                "Maximum fraction of a Mob bounding box covered by an avatar (0-1).")]
            public double MaximumOcclusion { get; set; }

            [Option("minMobVisible", Required = false, Default = 0.55, HelpText =
                "Drop a Mob label when its estimated visible fraction is below this value (0-1).")]
            public double MinimumMobVisible { get; set; }

            [Option("combatStates", Required = false, Default = false, HelpText =
                "Generate alive/hit/dying Mob animation sweeps and an ROI classification dataset.")]
            public bool CombatStates { get; set; }

            [Option("combatStateSamples", Required = false, Default = 3, HelpText =
                "Number of animation progress samples generated for each combat state.")]
            public int CombatStateSamples { get; set; }

            [Option("skillId", Required = false, Separator = ' ', HelpText =
                "Optional space-separated Skill IDs used for hit/death effect overlays.")]
            public IEnumerable<int> SkillIds { get; set; } = Enumerable.Empty<int>();

            [Option("skillEffects", Required = false, Default = 64, HelpText =
                "Maximum number of Skill WZ effect frames cached for combat-state generation.")]
            public int SkillEffectCount { get; set; }

            [Option("skillCharacterLevel", Required = false, Default = 0, HelpText =
                "Character level used to select CharLevel/SkillLevel visual effects; 0 selects the highest branch.")]
            public int SkillCharacterLevel { get; set; }

            [Option("hitEffectProbability", Required = false, Default = 0.80, HelpText =
                "Probability of applying a configured skill effect to hit samples (0-1).")]
            public double HitEffectProbability { get; set; }

            [Option("dyingEffectProbability", Required = false, Default = 1.0, HelpText =
                "Probability of applying a configured skill effect to dying samples (0-1).")]
            public double DyingEffectProbability { get; set; }
        }

        [STAThread]
        private static int Main(string[] args)
        {
            bool interactive = args == null || args.Length == 0;
            int ret;
            do
            {
                if (interactive)
                {
                    System.Windows.Forms.Application.EnableVisualStyles();
                    System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
                    using (var form = new GeneratorForm())
                    {
                        if (form.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                        {
                            ret = 0;
                            break;
                        }
                        args = form.BuildCommandLineArguments();
                    }
                }
                EnsureConsole();
                _lastRunWasPreview = false;
                ret = CommandLine.Parser.Default.ParseArguments<Options>(args)
                    .MapResult(RunAndReturn, OnParseError);
                Console.WriteLine("MapleStoryDetectionSampleGenerator exited with code= {0}", ret);
            } while (interactive && _lastRunWasPreview);

            if (_hasConsole)
            {
                FreeConsole();
            }
            return ret;
        }

        private static void EnsureConsole()
        {
            if (GetConsoleWindow() == IntPtr.Zero)
            {
                _hasConsole = AttachConsole(-1) || AllocConsole();
            }
            else
            {
                _hasConsole = true;
            }
            if (_hasConsole)
            {
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
                Console.OutputEncoding = Encoding.UTF8;
            }
        }

        private static int OnParseError(IEnumerable<Error> errors)
        {
            foreach (var error in errors)
            {
                Console.Error.WriteLine(Enum.GetName(typeof(ErrorType), error.Tag));
            }
            return -1;
        }

        /// <summary>
        /// Main logic here
        /// </summary>
        private static int RunAndReturn(Options options)
        {
            // Print program info
            Console.WriteLine(HeadingInfo.Default);
            Console.WriteLine(FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).LegalCopyright);
            // Check arguments
            PreRunTest(options);
            Console.WriteLine("MapleStory Location: {0}", options.MapleStoryPath);
            // Initialize render
            using MapRenderInvoker renderInvoker = new MapRenderInvoker(options.MapleStoryPath,
                options.Encoding == string.Empty ? Encoding.Default : Encoding.GetEncoding(options.Encoding),
                false,
                options.MobIds);
            string[] requestedMaps = options.Maps.ToArray();
            Queue<string> maps = new Queue<string>(requestedMaps);
            var first = maps.Dequeue();
            renderInvoker.LoadMap(first);
            IReadOnlyList<byte[]> avatarOccluders = options.MobOcclusion
                ? renderInvoker.CreateRandomAvatarOccluders(options.AvatarOccluderCount)
                : Array.Empty<byte[]>();
            IReadOnlyList<byte[]> mapOccluders = Array.Empty<byte[]>();
            IReadOnlyList<byte[]> npcOccluders = Array.Empty<byte[]>();
            IReadOnlyList<MobOccluderAsset> mobOccluders = Array.Empty<MobOccluderAsset>();
            IReadOnlyList<byte[]> dropItemOccluders = options.MobOcclusion
                ? renderInvoker.CreateDropItemOccluders(options.DropItemOccluderCount)
                : Array.Empty<byte[]>();
            if (options.MobOcclusion &&
                (options.MapOccluderCount > 0 || options.NpcOccluderCount > 0 ||
                 options.MobOccluderCount > 0))
            {
                int mapPerMap = Math.Max(1,
                    (int)Math.Ceiling((double)options.MapOccluderCount / requestedMaps.Length));
                int npcPerMap = Math.Max(1,
                    (int)Math.Ceiling((double)options.NpcOccluderCount / requestedMaps.Length));
                int mobPerMap = Math.Max(1,
                    (int)Math.Ceiling((double)options.MobOccluderCount / requestedMaps.Length));
                var combinedMapOccluders = new List<byte[]>();
                var combinedNpcOccluders = new List<byte[]>();
                var combinedMobOccluders = new List<MobOccluderAsset>();
                foreach (string map in requestedMaps)
                {
                    renderInvoker.LoadMap(map);
                    if (options.MapOccluderCount > 0)
                    {
                        combinedMapOccluders.AddRange(
                            renderInvoker.CreateMapObjectOccluders(mapPerMap));
                    }
                    if (options.NpcOccluderCount > 0)
                    {
                        combinedNpcOccluders.AddRange(
                            renderInvoker.CreateNpcOccluders(npcPerMap));
                    }
                    if (options.MobOccluderCount > 0)
                    {
                        combinedMobOccluders.AddRange(
                            renderInvoker.CreateMobOccluders(mobPerMap));
                    }
                }
                mapOccluders = combinedMapOccluders.Take(options.MapOccluderCount).ToArray();
                npcOccluders = combinedNpcOccluders.Take(options.NpcOccluderCount).ToArray();
                mobOccluders = combinedMobOccluders.Take(options.MobOccluderCount).ToArray();
                renderInvoker.LoadMap(first);
            }
            IReadOnlyList<SkillEffectAsset> skillEffects = options.CombatStates
                ? renderInvoker.CreateSkillEffects(
                    options.SkillEffectCount,
                    options.SkillIds,
                    options.SkillCharacterLevel)
                : Array.Empty<SkillEffectAsset>();
            if (options.CombatStates && skillEffects.Count == 0)
            {
                throw new InvalidOperationException(
                    "Combat-state generation requires at least one usable Skill WZ hit/effect frame.");
            }
            if (options.MobOcclusion && options.AvatarPreview)
            {
                string previewDirectory = Path.Combine(options.OutputPath, "occluder-preview");
                SaveOccluderPreviews(previewDirectory, "player", avatarOccluders);
                SaveOccluderPreviews(previewDirectory, "npc", npcOccluders);
                SaveOccluderPreviews(previewDirectory, "drop", dropItemOccluders);
                SaveMobOccluderPreviews(previewDirectory, mobOccluders);
                SaveOccluderPreviews(previewDirectory, "map", mapOccluders);
                Console.WriteLine("Occluder previews saved to {0}", previewDirectory);
            }
            renderInvoker.Launch(options.RenderWidth, options.RenderHeight);
            // Initialize sampler and the same visual post-processors for both previews and datasets.
            Sampler.Sampler sampler = new Sampler.Sampler(renderInvoker);
            if (options.PostProcessingEnable && options.PlayerImageDirectory != "")
            {
                IPostProcessor postProcessor = new PlayerProcessor(options.PlayerImageDirectory);
                sampler.OnSampleCaptured += (s, e) => postProcessor.Process(s);
            }
            MobOcclusionProcessor mobOcclusionProcessor = null;
            if (options.MobOcclusion)
            {
                mobOcclusionProcessor = new MobOcclusionProcessor(
                    avatarOccluders,
                    npcOccluders,
                    dropItemOccluders,
                    mobOccluders,
                    mapOccluders,
                    options.OcclusionProbability,
                    options.MinimumOcclusion,
                    options.MaximumOcclusion,
                    options.MinimumMobVisible);
                sampler.OnSampleCaptured += (s, e) => mobOcclusionProcessor.Process(s);
            }

            SkillHitEffectProcessor skillEffectProcessor = null;
            if (options.CombatStates)
            {
                skillEffectProcessor = new SkillHitEffectProcessor(
                    skillEffects,
                    options.HitEffectProbability,
                    options.DyingEffectProbability,
                    options.MinimumMobVisible);
                sampler.OnSampleCaptured += (s, e) => skillEffectProcessor.Process(s);
            }

            if (options.PreviewSamples > 0)
            {
                mobOcclusionProcessor?.ConfigurePreviewSchedule(options.PreviewSamples);
                IReadOnlyList<string> previewPaths = GeneratePreviews(
                    renderInvoker,
                    sampler,
                    options);
                renderInvoker.Stop();
                string previewDirectory = Path.GetDirectoryName(previewPaths.FirstOrDefault())
                                          ?? options.OutputPath;
                using (var gallery = new PreviewGalleryForm(previewPaths, previewDirectory))
                {
                    gallery.ShowDialog();
                }
                _lastRunWasPreview = true;
                return 0;
            }

            IDatasetWriter writer = GetDatasetWriter(options, first);
            using var stateWriter = options.CombatStates
                ? new MobStateDatasetWriter(options.OutputPath)
                : null;
            if (stateWriter != null)
            {
                sampler.OnSampleCaptured += (s, e) => stateWriter.Process(s);
            }

            while (true)
            {
                SampleCurrentMap(renderInvoker, sampler, writer, options);
                if (maps.Count == 0)
                {
                    break;
                }
                renderInvoker.SwitchMap(maps.Dequeue());
            }
            writer.Finish();
            if (mobOcclusionProcessor != null)
            {
                Console.WriteLine(
                    "Mob occlusion summary: {0} image(s), {1} Mob(s) occluded, {2} label(s) dropped.",
                    mobOcclusionProcessor.ProcessedSamples,
                    mobOcclusionProcessor.OccludedMobs,
                    mobOcclusionProcessor.DroppedLabels);
            }
            if (skillEffectProcessor != null)
            {
                Console.WriteLine(
                    "Combat-state summary: {0} ROI crop(s), {1} Skill effect overlay(s).",
                    stateWriter.CropCount,
                    skillEffectProcessor.AppliedEffects);
            }
            return 0;
        }

        private static void SaveOccluderPreviews(
            string rootDirectory,
            string category,
            IReadOnlyList<byte[]> assets)
        {
            string directory = Path.Combine(rootDirectory, category);
            Directory.CreateDirectory(directory);
            for (int index = 0; index < assets.Count; index++)
            {
                File.WriteAllBytes(
                    Path.Combine(directory, $"{category}-{index + 1:D3}.png"),
                    assets[index]);
            }
        }

        private static void SaveMobOccluderPreviews(
            string rootDirectory,
            IReadOnlyList<MobOccluderAsset> assets)
        {
            string directory = Path.Combine(rootDirectory, "mob");
            Directory.CreateDirectory(directory);
            for (int index = 0; index < assets.Count; index++)
            {
                File.WriteAllBytes(
                    Path.Combine(directory,
                        $"mob-{assets[index].MobId:D7}-{index + 1:D3}.png"),
                    assets[index].ImageBytes);
            }
        }

        private static IReadOnlyList<string> GeneratePreviews(
            MapRenderInvoker renderInvoker,
            Sampler.Sampler sampler,
            Options options)
        {
            string previewDirectory = Path.Combine(
                options.OutputPath,
                "preview",
                DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(previewDirectory);

            List<(int X, int Y)> cameraPositions = EnumerateCameraPositions(
                renderInvoker,
                options.StepX,
                options.StepY).ToList();
            MobRenderState[] states = options.CombatStates
                ? new[]
                {
                    MobRenderState.Alive,
                    MobRenderState.Hit,
                    MobRenderState.Dying,
                    MobRenderState.Spawning
                }
                : new[] { MobRenderState.Automatic };
            var paths = new List<string>(options.PreviewSamples);
            int positionCursor = 0;

            for (int index = 0; index < options.PreviewSamples; index++)
            {
                MobRenderState state = states[index % states.Length];
                double progress = ((index / states.Length) % Math.Max(1, options.CombatStateSamples) + 0.5)
                                  / Math.Max(1, options.CombatStateSamples);
                renderInvoker.SetMobRenderState(state, progress);

                Sample selected = null;
                for (int attempt = 0; attempt < cameraPositions.Count; attempt++)
                {
                    (int x, int y) = cameraPositions[positionCursor++ % cameraPositions.Count];
                    renderInvoker.MoveCamera(x, y);
                    Sample candidate = sampler.SampleSingleAsync().GetAwaiter().GetResult();
                    if (selected != null)
                    {
                        selected.ImageStream.Dispose();
                    }
                    selected = candidate;
                    if (candidate.Items.Any(item => item.Type == ObjectClass.Mob))
                    {
                        break;
                    }
                    if (options.SampleInterval > 0)
                    {
                        Thread.Sleep(options.SampleInterval);
                    }
                }

                if (selected == null)
                {
                    continue;
                }
                string[] occlusionSources = selected.Items
                    .Where(item => item.Type == ObjectClass.Mob &&
                                   !string.IsNullOrWhiteSpace(item.OcclusionSource))
                    .Select(item => item.OcclusionSource.ToLowerInvariant())
                    .Distinct()
                    .OrderBy(source => source)
                    .ToArray();
                string occlusionTag = occlusionSources.Length == 0
                    ? "clear"
                    : "occ-" + string.Join("+", occlusionSources);
                string path = Path.Combine(previewDirectory,
                    $"preview-{index + 1:D2}-{state.ToString().ToLowerInvariant()}-{occlusionTag}.jpg");
                SaveAnnotatedPreview(selected, path);
                selected.ImageStream.Dispose();
                paths.Add(path);
                if (options.SampleInterval > 0)
                {
                    Thread.Sleep(options.SampleInterval);
                }
            }

            Console.WriteLine("Generated {0} preview image(s): {1}", paths.Count, previewDirectory);
            return paths;
        }

        private static IEnumerable<(int X, int Y)> EnumerateCameraPositions(
            MapRenderInvoker renderInvoker,
            int xStep,
            int yStep)
        {
            int startX = renderInvoker.WorldOriginX + renderInvoker.ScreenWidth / 2;
            int startY = renderInvoker.WorldOriginY + renderInvoker.ScreenHeight / 2;
            int endX = renderInvoker.WorldOriginX + renderInvoker.WorldWidth - renderInvoker.ScreenWidth / 2;
            int endY = renderInvoker.WorldOriginY + renderInvoker.WorldHeight - renderInvoker.ScreenHeight / 2;
            var positions = new List<(int X, int Y)>();

            if (endX <= startX || endY <= startY)
            {
                positions.Add((
                    renderInvoker.WorldOriginX + renderInvoker.WorldWidth / 2,
                    renderInvoker.WorldOriginY + renderInvoker.WorldHeight / 2));
                return positions;
            }
            for (int x = startX; x < endX; x += Math.Abs(xStep))
            {
                for (int y = startY; y < endY; y += Math.Abs(yStep))
                {
                    positions.Add((x, y));
                }
            }
            return positions.Count == 0
                ? new[] { (startX, startY) }
                : positions;
        }

        private static void SaveAnnotatedPreview(Sample sample, string path)
        {
            sample.ImageStream.Position = 0;
            using (var source = new Bitmap(sample.ImageStream))
            using (var graphics = Graphics.FromImage(source))
            using (var font = new Font("Segoe UI", 10F, FontStyle.Bold))
            {
                foreach (TargetItem item in sample.Items.Where(item => item.Type == ObjectClass.Mob))
                {
                    bool isOccluded = item.VisibleFraction < 0.995;
                    Color color = isOccluded
                        ? Color.Magenta
                        : item.IsSpawnTransparent
                            ? Color.Cyan
                        : item.CombatState == MobCombatState.Dying
                        ? Color.Red
                        : item.CombatState == MobCombatState.Hit
                            ? Color.Orange
                            : item.CombatState == MobCombatState.Unknown
                                ? Color.Gray
                                : Color.Lime;
                    using (var pen = new Pen(color, isOccluded ? 4F : 2F))
                    using (var brush = new SolidBrush(Color.FromArgb(210, color)))
                    {
                        graphics.DrawRectangle(pen, item.X, item.Y, item.Width, item.Height);
                        string occlusion = isOccluded
                            ? $"OCC[{item.OcclusionSource ?? "EFFECT"}] visible:{item.VisibleFraction:P0} "
                            : string.Empty;
                        string spawning = item.IsSpawnTransparent
                            ? $"SPAWN alpha:{item.RenderOpacity:P0} "
                            : string.Empty;
                        string label =
                            $"{occlusion}{spawning}{item.Id} {item.CombatState} {item.AnimationName}:{item.AnimationFrame}";
                        SizeF textSize = graphics.MeasureString(label, font);
                        float textY = Math.Max(0, item.Y - textSize.Height);
                        graphics.FillRectangle(Brushes.Black, item.X, textY, textSize.Width, textSize.Height);
                        graphics.DrawString(label, font, brush, item.X, textY);
                    }
                }
                TargetItem highlighted = sample.Items
                    .Where(item => item.Type == ObjectClass.Mob &&
                                   (item.VisibleFraction < 0.995 || item.IsSpawnTransparent))
                    .OrderBy(item => item.IsSpawnTransparent && item.VisibleFraction >= 0.995 ? 1 : 0)
                    .ThenBy(item => item.VisibleFraction)
                    .FirstOrDefault();
                if (highlighted != null)
                {
                    using (var calloutFont = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold))
                    using (var pen = new Pen(
                               highlighted.IsSpawnTransparent ? Color.Cyan : Color.Magenta, 4F))
                    {
                        var circle = new Rectangle(
                            highlighted.X - 10,
                            highlighted.Y - 10,
                            highlighted.Width + 20,
                            highlighted.Height + 20);
                        graphics.DrawEllipse(pen, circle);
                        string callout = highlighted.IsSpawnTransparent
                            ? $"SPAWN 刷新半透明 {highlighted.RenderOpacity:P0}"
                            : $"OCCLUSION {highlighted.OcclusionSource ?? "EFFECT"}  可见 {highlighted.VisibleFraction:P0}";
                        SizeF size = graphics.MeasureString(callout, calloutFont);
                        float calloutX = Math.Max(0,
                            Math.Min(source.Width - size.Width, highlighted.X));
                        float calloutY = Math.Min(source.Height - size.Height,
                            highlighted.Y + highlighted.Height + 12);
                        graphics.FillRectangle(Brushes.Black,
                            calloutX, calloutY, size.Width, size.Height);
                        graphics.DrawString(callout, calloutFont,
                            highlighted.IsSpawnTransparent ? Brushes.Cyan : Brushes.Magenta,
                            calloutX, calloutY);
                    }
                }
                source.Save(path, ImageFormat.Jpeg);
            }
        }

        private static void SampleCurrentMap(
            MapRenderInvoker renderInvoker,
            Sampler.Sampler sampler,
            IDatasetWriter writer,
            Options options)
        {
            if (!options.CombatStates)
            {
                renderInvoker.SetMobRenderState(MobRenderState.Automatic);
                sampler.SampleAll(options.StepX, options.StepY, writer, options.SampleInterval);
                return;
            }

            (MobRenderState State, int Samples)[] states =
            {
                (MobRenderState.Alive, options.CombatStateSamples),
                (MobRenderState.Hit, options.CombatStateSamples),
                (MobRenderState.Dying, options.CombatStateSamples),
                // Refresh is brief in real gameplay. Keep it well represented without
                // allowing translucent Alive crops to dominate the state classifier.
                (MobRenderState.Spawning, Math.Max(1,
                    (int)Math.Ceiling(options.CombatStateSamples * 0.60)))
            };
            foreach ((MobRenderState state, int sampleCount) in states)
            {
                for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
                {
                    double progress = (sampleIndex + 0.5) / sampleCount;
                    Console.WriteLine(
                        "Combat state sweep: {0}, animation progress {1:P0}",
                        state,
                        progress);
                    renderInvoker.SetMobRenderState(state, progress);
                    sampler.SampleAll(options.StepX, options.StepY, writer, options.SampleInterval);
                }
            }
        }


        /// <summary>
        /// Throw if condition not meet.
        /// </summary>
        private static void PreRunTest(Options options)
        {
            // Check resolution
            if (options.RenderHeight <= 0)
            {
                throw new ArgumentException("Render size cannot exceed screen size. Height illegal.",
                    nameof(options.RenderHeight));
            }
            if (options.RenderWidth <= 0)
            {
                throw new ArgumentException("Render size cannot exceed screen size. Width illegal.",
                    nameof(options.RenderWidth));
            }

            // Check format
            Enum.Parse(typeof(OutputFormat), options.Format, true);

            // Check file path
            if (options.MapleStoryPath == string.Empty)
            {
                if (MapleStoryPathHelper.FoundMapleStoryInstalled)
                {
                    options.MapleStoryPath = MapleStoryPathHelper.MapleStoryInstallDirectory;
                }
                else
                {
                    throw new ArgumentException("Cannot find MapleStory installed location. Please specify it in commandline or retry as Administrator.");
                }
            }
            else if (!Directory.Exists(options.MapleStoryPath))
            {
                throw new ArgumentException("Supplied MapleStory directory does not exist.");
            }

            // Check map id format
            foreach (var map in options.Maps)
            {
                string id = map.Replace(".img", string.Empty);
                if (!id.All(char.IsDigit))
                {
                    throw new ArgumentException("Supplied Map Id is not in correct format." +
                                                " --map parameter should be space-separated list of IDs. " +
                                                "E.g. --map 450007010 450007060");
                }
            }

            // Check optional mob id format
            foreach (int mobId in options.MobIds ?? Enumerable.Empty<int>())
            {
                if (mobId <= 0)
                {
                    throw new ArgumentException(
                        "Supplied Mob ID is not in correct format. --mobId accepts positive numeric IDs.");
                }
            }

            foreach (int skillId in options.SkillIds ?? Enumerable.Empty<int>())
            {
                if (skillId <= 0)
                {
                    throw new ArgumentException(
                        "Supplied Skill ID is not in correct format. --skillId accepts positive numeric IDs.");
                }
            }

            if (options.CombatStates)
            {
                if (options.CombatStateSamples <= 0 || options.CombatStateSamples > 10)
                {
                    throw new ArgumentException("--combatStateSamples must be between 1 and 10.");
                }
                if (options.SkillEffectCount <= 0)
                {
                    throw new ArgumentException("--skillEffects must be positive.");
                }
                if (options.SkillCharacterLevel < 0 || options.SkillCharacterLevel > 300)
                {
                    throw new ArgumentException("--skillCharacterLevel must be between 0 and 300.");
                }
                int distinctSkillCount = (options.SkillIds ?? Enumerable.Empty<int>())
                    .Distinct()
                    .Count();
                if (distinctSkillCount > options.SkillEffectCount)
                {
                    throw new ArgumentException(
                        "--skillEffects must be at least the number of distinct --skillId values.");
                }
                ValidateRatio(options.HitEffectProbability, "--hitEffectProbability", allowZero: true);
                ValidateRatio(options.DyingEffectProbability, "--dyingEffectProbability", allowZero: false);
            }

            if (options.MobOcclusion)
            {
                if (options.AvatarOccluderCount <= 0)
                {
                    throw new ArgumentException("--avatarOccluders must be positive.");
                }
                if (options.MapOccluderCount < 0)
                {
                    throw new ArgumentException("--mapOccluders cannot be negative.");
                }
                if (options.NpcOccluderCount < 0)
                {
                    throw new ArgumentException("--npcOccluders cannot be negative.");
                }
                if (options.DropItemOccluderCount < 0)
                {
                    throw new ArgumentException("--dropItemOccluders cannot be negative.");
                }
                if (options.MobOccluderCount < 0)
                {
                    throw new ArgumentException("--mobOccluders cannot be negative.");
                }
                ValidateRatio(options.OcclusionProbability, "--occlusionProbability", allowZero: true);
                ValidateRatio(options.MinimumOcclusion, "--occlusionMin", allowZero: false);
                ValidateRatio(options.MaximumOcclusion, "--occlusionMax", allowZero: false);
                ValidateRatio(options.MinimumMobVisible, "--minMobVisible", allowZero: false);
                if (options.MinimumOcclusion >= options.MaximumOcclusion)
                {
                    throw new ArgumentException("--occlusionMin must be less than --occlusionMax.");
                }
            }

            // Check encoding
            if (options.Encoding != string.Empty)
            {
                try
                {
                    Encoding.GetEncoding(options.Encoding);
                }
                catch (ArgumentException)
                {
                    throw new ArgumentException(
                        $"{options.Encoding} is not an available Encoding in your system.");
                }
            }

            // Check output path
            if (!Directory.Exists(options.OutputPath))
            {
                throw new ArgumentException($"OutputPath {options.OutputPath} does not exist.");
            }

            // Check interval
            if (options.SampleInterval < 0)
            {
                throw new ArgumentException("SampleInterval cannot be negative!");
            }
            if (options.StepX <= 0 || options.StepY <= 0)
            {
                throw new ArgumentException("--xStep and --yStep must be positive.");
            }
            if (options.PreviewSamples < 0 || options.PreviewSamples > 30)
            {
                throw new ArgumentException("--previewSamples must be between 0 and 30.");
            }

            // Check for post-processing
            if (options.PostProcessingEnable)
            {
                if (options.PlayerImageDirectory != "")
                {
                    if (!Directory.Exists(options.PlayerImageDirectory))
                    {
                        throw new ArgumentException($"PlayerImageDirectory {options.PlayerImageDirectory} cannot be found!");
                    }
                }
            }
        }

        private static void ValidateRatio(double value, string option, bool allowZero)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value > 1.0 ||
                (allowZero ? value < 0.0 : value <= 0.0))
            {
                string range = allowZero ? "between 0 and 1" : "greater than 0 and at most 1";
                throw new ArgumentException(
                    $"{option} must be {range}. Current value: " +
                    value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        private static IDatasetWriter GetDatasetWriter(Options options, string map)
        {
            switch (Enum.Parse(typeof(OutputFormat), options.Format, true))
            {
                case OutputFormat.TfRecord:
                    return new TfRecordWriter(Path.Combine(options.OutputPath, map));
                case OutputFormat.Darknet:
                    return new DarknetWriter(options.OutputPath);
                case OutputFormat.Coco:
                    return new CocoWriter(options.OutputPath, $"MapleStory {map}.img Object Detection Samples");
                default:
                    throw new ArgumentOutOfRangeException(nameof(options), options, null);
            }
        }

    }
}
