using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MapleStory.Common;
using Microsoft.Xna.Framework;
using WzComparerR2.Common;
using WzComparerR2.MapRender;
using WzComparerR2.PluginBase;
using WzComparerR2.WzLib;

namespace MapRender.Invoker
{
    public class MapRenderInvoker : MapRenderInvokerBase, IDisposable
    {
        private Wz_Image _currentMapImage;
        private StringLinker _stringLinker;
        private Thread _renderThread;
        private MapRender _mapRender;
        private Camera _camera;
        private volatile bool _isRunning;
        private volatile bool _stopRequested;
        private readonly int[] _mobIds;

        public bool IsRunning => _isRunning;

        public int ScreenWidth { private set; get; }

        public int ScreenHeight { private set; get; }

        public int WorldWidth => _camera.WorldRect.Width;

        public int WorldHeight => _camera.WorldRect.Height;

        public int WorldOriginX => _camera.WorldRect.X;

        public int WorldOriginY => _camera.WorldRect.Y;

        public int CurrentCameraX => (int)_camera.Center.X;

        public int CurrentCameraY => (int)_camera.Center.Y;

        public int CurrentMap { get; private set; }

        public MapRenderInvoker(
            string mapleStoryPath,
            Encoding encoding,
            bool disableImgCheck = false,
            IEnumerable<int> mobIds = null)
            : base(mapleStoryPath, encoding, disableImgCheck)
        {
            _mobIds = (mobIds ?? Enumerable.Empty<int>()).Distinct().ToArray();
            WzComparerR2.Config.ConfigManager.RegisterAllSection(
                typeof(WzComparerR2.MapRender.Config.MapRenderConfig).Assembly);
            _isRunning = false;
            _stopRequested = false;
            AddFindWzEventHandler();
        }

        /// <summary>
        /// Attach event handler to PlugManager, let it throw if reflection fail so we know there are changes in WzComparerR2
        /// </summary>
        /// <seealso cref="WzComparerR2.PluginBase.PluginManager.WzFileFinding"/>
        private void AddFindWzEventHandler()
        {
            EventInfo findWzEvent = typeof(PluginManager)
                .GetEvent("WzFileFinding", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo findWzHandler =
                typeof(MapRenderInvoker).GetMethod("CharaSimLoader_WzFileFinding", BindingFlags.NonPublic | BindingFlags.Instance);
            Delegate findWzDelegate = Delegate.CreateDelegate(findWzEvent.EventHandlerType, this, findWzHandler);
            findWzEvent.AddMethod.Invoke(this, new[] { findWzDelegate });
        }

        ~MapRenderInvoker()
        {
            Stop();
        }

        ///<inheritdoc/>
        public override void LoadMap(string imgText)
        {
            string mapIdText = imgText.EndsWith(".img", StringComparison.OrdinalIgnoreCase)
                ? imgText.Substring(0, imgText.Length - 4)
                : imgText;
            CurrentMap = int.Parse(mapIdText);
            if (_extractedImgDataSource != null)
            {
                _currentMapImage = _extractedImgDataSource.LoadMapImage(CurrentMap);
            }
            else
            {
                string imageName = mapIdText + ".img";
                _currentMapImage = WzTreeSearcher.SearchForMap(_wzStructure.WzNode, imageName);
            }
            ExtractAndMergeMobs(_currentMapImage);
            _stringLinker = new StringLinker();
            if (_extractedImgDataSource == null)
            {
                _stringLinker.Load(PluginManager.FindWz(Wz_Type.String).GetValueEx<Wz_File>(null));
            }
        }

        /// <inheritdoc/>
        public override void Launch(int width, int height)
        {
            if (_currentMapImage == null)
            {
                throw new InvalidOperationException("MapRenderInvoker.LoadMap() must be called before Launch().");
            }

            _isRunning = false;
            _renderThread = new Thread(() =>
            {
                _mapRender = new MapRender(_currentMapImage) { StringLinker = _stringLinker };
                _mapRender.Window.Title = FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).FileName;
                try
                {
                    using (_mapRender)
                    {
                        _mapRender.RunOneFrame(); // Initialize
                        _mapRender.ChangeResolution(width, height);
                        _camera = _mapRender.renderEnv.Camera;
                        _mapRender.Run();
                    }
                }
                catch (Exception) when (_stopRequested)
                {
                    // MonoGame WinForms may tick once after Exit() clears its window.
                    // This is expected during an explicit generator shutdown.
                }
                finally
                {
                    _mapRender = null;
                }
            });
            ScreenHeight = height;
            ScreenWidth = width;
            _renderThread.SetApartmentState(ApartmentState.STA);
            _renderThread.IsBackground = true;
            _renderThread.Start();
            SpinWait spinWait = new SpinWait();
            while (_mapRender == null)
            {
                spinWait.SpinOnce();
            }
            _mapRender.WaitSceneLoading();
            _isRunning = true;
        }

        public override void SwitchMap(string imgText)
        {
            string mapIdText = imgText.EndsWith(".img", StringComparison.OrdinalIgnoreCase)
                ? imgText.Substring(0, imgText.Length - 4)
                : imgText;
            CurrentMap = int.Parse(mapIdText);
            Wz_Image mapImage;
            if (_extractedImgDataSource != null)
            {
                mapImage = _extractedImgDataSource.LoadMapImage(CurrentMap);
            }
            else
            {
                mapImage = WzTreeSearcher.SearchForMap(_wzStructure.WzNode, mapIdText + ".img");
            }
            ExtractAndMergeMobs(mapImage);
            _mapRender.SwitchToNewMap(CurrentMap);
        }

        private void ExtractAndMergeMobs(Wz_Image mapImage)
        {
            if (mapImage == null)
            {
                throw new InvalidOperationException($"Map {CurrentMap:D9}.img could not be loaded.");
            }

            mapImage.TryExtract(out Exception error);
            if (error != null)
            {
                throw error;
            }

            if (_mobIds.Length == 0)
            {
                return;
            }

            foreach (int mobId in _mobIds)
            {
                if (PluginManager.FindWz($@"Mob\{mobId:D7}.img") == null)
                {
                    throw new FileNotFoundException(
                        $"Cannot find Mob IMG for --mobId {mobId}: Mob\\{mobId:D7}.img");
                }
            }

            Wz_Node lifeNode = mapImage.Node.Nodes["life"];
            if (lifeNode == null)
            {
                throw new InvalidOperationException(
                    $"Map {CurrentMap:D9}.img has no life node, so --mobId cannot place a mob.");
            }

            bool isCategory = lifeNode.Nodes["isCategory"].GetValueEx<int>(0) != 0;
            IEnumerable<Wz_Node> lifeEntries = isCategory
                ? lifeNode.Nodes.SelectMany(node => node.Nodes)
                : lifeNode.Nodes;
            List<Wz_Node> mobEntries = lifeEntries
                .Where(node =>
                    string.Equals(node.Nodes["type"].GetValueEx<string>(null), "m",
                        StringComparison.OrdinalIgnoreCase) &&
                    node.Nodes["id"] != null)
                .ToList();

            if (mobEntries.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Map {CurrentMap:D9}.img has no mob spawn slots to use for --mobId.");
            }

            int[] mapMobIds = mobEntries
                .Select(node => node.Nodes["id"].GetValueEx<int>(0))
                .Where(id => id > 0)
                .Distinct()
                .ToArray();
            int[] mergedMobIds = mapMobIds
                .Concat(_mobIds)
                .Distinct()
                .ToArray();

            if (mergedMobIds.Length > mobEntries.Count)
            {
                throw new InvalidOperationException(
                    $"Map {CurrentMap:D9}.img has {mobEntries.Count} mob spawn slot(s), " +
                    $"but retaining its {mapMobIds.Length} mob type(s) and adding --mobId requires " +
                    $"{mergedMobIds.Length} slots.");
            }

            for (int index = 0; index < mobEntries.Count; index++)
            {
                mobEntries[index].Nodes["id"].Value = mergedMobIds[index % mergedMobIds.Length];
            }

            Console.WriteLine(
                "Map {0:D9}: distributed {1} map Mob ID(s) plus {2} extra ID(s) across " +
                "{3} spawn slot(s): {4}",
                CurrentMap,
                mapMobIds.Length,
                mergedMobIds.Length - mapMobIds.Length,
                mobEntries.Count,
                string.Join(", ", mergedMobIds));
        }

        public void MoveCamera(int centerX, int centerY)
        {
            _camera.Center = new Vector2(centerX, centerY);
            _camera.AdjustToWorldRect();
        }

        public ScreenShotData TakeScreenShot(Stream stream)
        {
            return _mapRender.TakeScreenShot(stream);
        }

        public void SetMobRenderState(MobRenderState state, double normalizedProgress = 0.0)
        {
            if (!_isRunning || _mapRender == null)
            {
                throw new InvalidOperationException("Map renderer is not running.");
            }
            _mapRender.SetMobRenderState(state, normalizedProgress);
        }

        public void Stop()
        {
            _stopRequested = true;
            _isRunning = false;
            MapRender render = _mapRender;
            if (render != null)
            {
                try
                {
                    render.Exit();
                }
                catch
                {
                    // The game window may already be tearing down.
                }
            }
            if (_renderThread != null && _renderThread.IsAlive &&
                Thread.CurrentThread != _renderThread)
            {
                _renderThread.Join(TimeSpan.FromSeconds(5));
            }
        }

        public void Dispose()
        {
            Stop();
            GC.SuppressFinalize(this);
        }

        public IReadOnlyList<byte[]> CreateRandomAvatarOccluders(int count)
        {
            return new RandomAvatarGenerator(_extractedImgDataSource).Create(count);
        }

        public IReadOnlyList<byte[]> CreateMapObjectOccluders(int count)
        {
            return new MapObjectOccluderGenerator(_currentMapImage).Create(count);
        }

        public IReadOnlyList<byte[]> CreateNpcOccluders(int count)
        {
            return new NpcOccluderGenerator(_extractedImgDataSource, _currentMapImage).Create(count);
        }

        public IReadOnlyList<byte[]> CreateDropItemOccluders(int count)
        {
            return new DropItemOccluderGenerator(_extractedImgDataSource).Create(count);
        }

        public IReadOnlyList<MobOccluderAsset> CreateMobOccluders(int count)
        {
            return new MobOccluderGenerator(_currentMapImage).Create(count);
        }

        public IReadOnlyList<SkillEffectAsset> CreateSkillEffects(
            int count,
            IEnumerable<int> skillIds,
            int characterLevel = 0)
        {
            return new SkillEffectGenerator(_extractedImgDataSource)
                .Create(count, skillIds, characterLevel);
        }

        #region COPIED_CODE

        /// <summary>
        /// !!!!!!!!!!!!COPIED CODE!!!!!!!!!!!!!!
        /// Version: git@github.com:Kagamia/WzComparerR2.git:f6ecfb18cae661f125a189e527feea1964f5bda8
        /// </summary>
        /// <see cref="WzComparerR2.MainForm.CharaSimLoader_WzFileFinding"/>
        private void CharaSimLoader_WzFileFinding(object sender, WzComparerR2.FindWzEventArgs e)
        {
            if (_extractedImgDataSource != null)
            {
                if (!string.IsNullOrEmpty(e.FullPath))
                {
                    e.WzNode = _extractedImgDataSource.Resolve(e.FullPath);
                }
                return;
            }

            string[] fullPath = null;
            if (!string.IsNullOrEmpty(e.FullPath)) //用fullpath作为输入参数
            {
                fullPath = e.FullPath.Split('/', '\\');
                try
                {
                    e.WzType = (Wz_Type)Enum.Parse(typeof(Wz_Type), fullPath[0], true);
                }
                catch
                {
                    e.WzType = Wz_Type.Unknown;
                }
            }

            List<Wz_Node> preSearch = new List<Wz_Node>();
            if (e.WzType != Wz_Type.Unknown) //用wztype作为输入参数
            {
                IEnumerable<Wz_Structure> preSearchWz = e.WzFile?.WzStructure != null ?
                    Enumerable.Repeat(e.WzFile.WzStructure, 1) : new List<Wz_Structure>() { _wzStructure };
                foreach (var wzs in preSearchWz)
                {
                    Wz_File baseWz = null;
                    bool find = false;
                    foreach (Wz_File wz_f in wzs.wz_files)
                    {
                        if (wz_f.Type == e.WzType)
                        {
                            preSearch.Add(wz_f.Node);
                            find = true;
                            //e.WzFile = wz_f;
                        }
                        if (wz_f.Type == Wz_Type.Base)
                        {
                            baseWz = wz_f;
                        }
                    }

                    // detect data.wz
                    if (baseWz != null && !find)
                    {
                        string key = e.WzType.ToString();
                        foreach (Wz_Node node in baseWz.Node.Nodes)
                        {
                            if (node.Text == key && node.Nodes.Count > 0)
                            {
                                preSearch.Add(node);
                            }
                        }
                    }
                }
            }

            if (fullPath == null || fullPath.Length <= 1)
            {
                if (e.WzType != Wz_Type.Unknown && preSearch.Count > 0) //返回wzFile
                {
                    e.WzNode = preSearch[0];
                    e.WzFile = preSearch[0].Value as Wz_File;
                }
                return;
            }

            if (preSearch.Count <= 0)
            {
                return;
            }

            foreach (var wzFileNode in preSearch)
            {
                var searchNode = wzFileNode;
                for (int i = 1; i < fullPath.Length && searchNode != null; i++)
                {
                    searchNode = searchNode.Nodes[fullPath[i]];
                    var img = searchNode.GetValueEx<Wz_Image>(null);
                    if (img != null)
                    {
                        searchNode = img.TryExtract() ? img.Node : null;
                    }
                }

                if (searchNode != null)
                {
                    e.WzNode = searchNode;
                    e.WzFile = wzFileNode.Value as Wz_File;
                    return;
                }
            }
            //寻找失败
            e.WzNode = null;
        }
        #endregion
    }

    public abstract class MapRenderInvokerBase
    {

        protected readonly Wz_Structure _wzStructure;
        protected readonly ExtractedImgDataSource _extractedImgDataSource;

        /// <summary>
        /// Provide base methods to open MapleStory data directory.
        /// For maintenance with upstream WzR2, check <see cref="WzComparerR2.MainForm.openWz"/>
        /// </summary>
        /// <param name="mapleStoryPath"></param>
        /// <param name="encoding"></param>
        /// <param name="disableImgCheck"></param>
        /// <exception cref="ArgumentException"></exception>
        protected MapRenderInvokerBase(string mapleStoryPath, Encoding encoding, bool disableImgCheck = false)
        {
            // Static settings for Wz_Structure :(
            Wz_Structure.DefaultAutoDetectExtFiles = true;
            Wz_Structure.DefaultEncoding = encoding;
            Wz_Structure.DefaultImgCheckDisabled = disableImgCheck;
            // Then our constructor
            string baseWzPath = Path.Combine(mapleStoryPath, MapleStoryPathHelper.MapleStoryBaseWzName);
            if (!File.Exists(baseWzPath))
            {
                string dataRoot = FindExtractedDataRoot(mapleStoryPath);
                if (dataRoot == null)
                {
                    throw new ArgumentException(
                        $"Cannot find {MapleStoryPathHelper.MapleStoryBaseWzName} or an extracted Data directory in {mapleStoryPath}.");
                }

                _wzStructure = new Wz_Structure();
                _extractedImgDataSource = new ExtractedImgDataSource(dataRoot, encoding);
                return;
            }
            // See WzComparerR2.MainForm.openWz()
            _wzStructure = new Wz_Structure();
            string[] msFileExtensions = { ".ms", ".mn" };
            if (msFileExtensions.Any(ext => string.Equals(Path.GetExtension(baseWzPath), ext, StringComparison.OrdinalIgnoreCase)))
            {
                _wzStructure.LoadMsFile(baseWzPath);
            }
            else if (_wzStructure.IsKMST1125WzFormat(baseWzPath))
            {
                _wzStructure.LoadKMST1125DataWz(baseWzPath);
                if (string.Equals(Path.GetFileName(baseWzPath), "Base.wz", StringComparison.OrdinalIgnoreCase))
                {
                    string packsDir = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(baseWzPath)), "Packs");
                    if (Directory.Exists(packsDir))
                    {
                        foreach (var ext in msFileExtensions)
                        {
                            foreach (var msFile in Directory.GetFiles(packsDir, $"*{ext}"))
                            {
                                _wzStructure.LoadMsFile(msFile);
                            }
                        }
                    }
                }
            }
            else
            {
                _wzStructure.Load(baseWzPath, true);
            }
        }

        private static string FindExtractedDataRoot(string suppliedPath)
        {
            string[] candidates =
            {
                suppliedPath,
                Path.Combine(suppliedPath, "Data")
            };

            return candidates.FirstOrDefault(path =>
                Directory.Exists(Path.Combine(path, "Map", "Map")) &&
                Directory.Exists(Path.Combine(path, "Mob")));
        }

        /// <summary>
        /// Load specified Wz map img to this invoker.
        /// </summary>
        /// <param name="imgText">The map node search. E.g. "450007010" </param>
        /// <exception cref="MapleStory.Common.Exceptions.WzImgNotFoundException">If supplied img cannot be found.</exception>
        public abstract void LoadMap(string imgText);

        /// <summary>
        /// Lunch map render. Make sure we have loaded img.
        /// </summary>
        /// <param name="width">Width of resolution</param>
        /// <param name="height">Height of resolution</param>
        public abstract void Launch(int width, int height);

        /// <summary>
        /// Switch to a new map after <see cref="Launch"/>
        /// </summary>
        /// <param name="imgText">The map node search. E.g. "450007010" </param>
        public abstract void SwitchMap(string imgText);

    }
}
