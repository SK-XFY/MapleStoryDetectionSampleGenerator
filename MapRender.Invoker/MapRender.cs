using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using WzComparerR2.Animation;
using WzComparerR2.MapRender;
using WzComparerR2.MapRender.Patches;
using WzComparerR2.MapRender.Patches2;
using WzComparerR2.WzLib;

namespace MapRender.Invoker
{
    internal class MapRender : FrmMapRender2
    {
        private volatile Stream _screenShotStream;
        private volatile ScreenShotData _screenShotData;
        private readonly object _mobStateLock = new object();
        private MobRenderState _forcedMobState = MobRenderState.Automatic;
        private double _forcedMobProgress;
        private int _requestedMobStateVersion;
        private int _appliedMobStateVersion;

        public MapRender(Wz_Image img) : base()
        {
            LoadMap(img);
        }

        public void ChangeResolution(int width, int height)
        {
            GraphicsDeviceManager deviceManager = this.GraphicsManager;
            deviceManager.PreferredBackBufferWidth = width;
            deviceManager.PreferredBackBufferHeight = height;
            WzComparerR2.Rendering.D2DFactory.Instance.ReleaseContext(deviceManager.GraphicsDevice);
            deviceManager.ApplyChanges();
            this.ui.Width = width;
            this.ui.Height = height;
            engine.Renderer.ResetNativeSize();
        }

        /// <summary>
        /// Take Screen, save image to stream, and return items info on the entire map.
        /// </summary>
        /// <param name="stream">Stream to save image</param>
        public ScreenShotData TakeScreenShot(Stream stream)
        {
            _screenShotStream = stream;
            while (_screenShotStream != null) ; //Wait next Draw(), yield to GetScreenShotMapData()
            var ret = _screenShotData;
            _screenShotData = null;
            return ret;
        }

        public void SetMobRenderState(MobRenderState state, double normalizedProgress)
        {
            int version;
            lock (_mobStateLock)
            {
                _forcedMobState = state;
                _forcedMobProgress = Math.Max(0.0, Math.Min(0.999, normalizedProgress));
                version = ++_requestedMobStateVersion;
            }

            // Sampling can otherwise enter TakeScreenShot midway through a render tick:
            // metadata would say Spawning while pixels still contain the previous Dying frame.
            // Wait until the render thread has applied this exact request once.
            SpinWait.SpinUntil(
                () => Volatile.Read(ref _appliedMobStateVersion) >= version,
                millisecondsTimeout: 2000);
        }

        /// <summary>
        /// Switch to a new map
        /// </summary>
        /// <param name="imgId">Wz img id</param>
        public void SwitchToNewMap(int imgId)
        {
            MoveToPortal(imgId, null);
            WaitSceneLoading();
        }

        public void WaitSceneLoading()
        {
            SpinWait spinWait = new SpinWait();
            // Wait until new map loaded
            while (!SceneRunning)
            {
                spinWait.SpinOnce();
            }
        }

        protected override void Draw(GameTime gameTime)
        {
            ApplyForcedMobState();
            base.Draw(gameTime);
            if (_screenShotStream != null)
            {
                ScreenShotHelper(_screenShotStream, gameTime);
                _screenShotData = new ScreenShotData(new List<TargetItem>(), this.renderEnv.Camera.ClipRect);
                GetScreenShotMapData(mapData.Scene, ref _screenShotData);
                _screenShotStream = null;
            }
        }

        private void ScreenShotHelper(Stream destination, GameTime gameTime)
        {
            int width = GraphicsDevice.PresentationParameters.BackBufferWidth;
            int height = GraphicsDevice.PresentationParameters.BackBufferHeight;
            using RenderTarget2D target = new RenderTarget2D(GraphicsDevice, width, height, false,
                SurfaceFormat.Rgba64, DepthFormat.None);
            var oldTarget = GraphicsDevice.GetRenderTargets();
            GraphicsDevice.SetRenderTarget(target);
            GraphicsDevice.Clear(Color.Black);
            DrawScene(gameTime);
            DrawTooltipItems(gameTime);
            this.ui.Draw(gameTime.ElapsedGameTime.TotalMilliseconds);
            this.tooltip.Draw(gameTime, renderEnv);
            GraphicsDevice.SetRenderTargets(oldTarget);
            target.SaveAsPng(destination, width, height);

        }

        private void GetScreenShotMapData(SceneNode node, ref ScreenShotData screenShotData)
        {
            var itemsOnMap = screenShotData.Items;
            if (node is ContainerNode container)
            {
                foreach (var item in container.Slots)
                {
                    if (item is LifeItem life)
                    {
                        var rectangle = this.GetLifeBoundingBox(life) ?? GetCurrentFrameRectangle(life);
                        if (rectangle.HasValue)
                        {
                            var target = new TargetItem(life, rectangle.Value, RenderObjectType.Mob) { Id = life.ID };
                            PopulateMobAnimation(target, life);
                            lock (_mobStateLock)
                            {
                                if (_forcedMobState == MobRenderState.Spawning)
                                {
                                    target.IsSpawnTransparent = true;
                                    target.RenderOpacity = GetSpawnOpacity(life, _forcedMobProgress);
                                }
                            }
                            itemsOnMap.Add(target);
                        }
                    }
                }
            }
            else
            {
                for (int i = 0, total = node.Nodes.Count; i < total; ++i)
                {
                    GetScreenShotMapData(node.Nodes[i], ref screenShotData);
                }
            }
        }

        private static Microsoft.Xna.Framework.Rectangle? GetCurrentFrameRectangle(LifeItem life)
        {
            if (!(life.View?.Animator is StateMachineAnimator animator) ||
                !(animator.Data is StateMachineAnimator.FrameStateMachineData data) ||
                data.FrameAnimator?.CurrentFrame == null)
            {
                return null;
            }
            Microsoft.Xna.Framework.Rectangle raw = data.FrameAnimator.CurrentFrame.Rectangle;
            if (raw.Width <= 0 || raw.Height <= 0)
            {
                return null;
            }
            return new Microsoft.Xna.Framework.Rectangle(
                life.X + raw.X,
                life.Cy + raw.Y,
                raw.Width,
                raw.Height);
        }

        private void ApplyForcedMobState()
        {
            MobRenderState requestedState;
            double progress;
            int requestedVersion;
            lock (_mobStateLock)
            {
                requestedState = _forcedMobState;
                progress = _forcedMobProgress;
                requestedVersion = _requestedMobStateVersion;
            }
            if (requestedState == MobRenderState.Automatic || mapData?.Scene == null)
            {
                Volatile.Write(ref _appliedMobStateVersion, requestedVersion);
                return;
            }

            foreach (LifeItem life in mapData.Scene.Mobs)
            {
                if (!(life.View?.Animator is StateMachineAnimator animator))
                {
                    continue;
                }
                string action = SelectAction(animator, requestedState, life);
                if (action == null)
                {
                    continue;
                }
                animator.SetAnimation(action);
                var data = animator.Data as StateMachineAnimator.FrameStateMachineData;
                if (data?.FrameAnimator != null && data.FrameAnimator.Length > 1)
                {
                    data.FrameAnimator.Update(TimeSpan.FromMilliseconds(
                        Math.Min(data.FrameAnimator.Length - 1, data.FrameAnimator.Length * progress)));
                }
                if (requestedState == MobRenderState.Spawning &&
                    data?.FrameAnimator?.CurrentFrame != null)
                {
                    double opacity = GetSpawnOpacity(life, progress);
                    data.FrameAnimator.CurrentFrame.A0 = (int)Math.Round(
                        data.FrameAnimator.CurrentFrame.A0 * opacity);
                }
            }
            Volatile.Write(ref _appliedMobStateVersion, requestedVersion);
        }

        private static string SelectAction(
            StateMachineAnimator animator,
            MobRenderState requestedState,
            LifeItem life)
        {
            if (requestedState == MobRenderState.Spawning)
            {
                string[] spawnNames = { "regen", "spawn", "appear", "birth" };
                foreach (string name in spawnNames)
                {
                    string exact = animator.Data.States.FirstOrDefault(state =>
                        string.Equals(state, name, StringComparison.OrdinalIgnoreCase));
                    if (exact != null)
                    {
                        return exact;
                    }
                }
                string prefixed = animator.Data.States.FirstOrDefault(state =>
                    spawnNames.Any(name => state.StartsWith(name, StringComparison.OrdinalIgnoreCase)));
                return prefixed ?? SelectAliveAction(animator, life);
            }
            string prefix = requestedState == MobRenderState.Hit
                ? "hit"
                : requestedState == MobRenderState.Dying
                    ? "die"
                    : null;
            if (prefix != null)
            {
                string preferred = prefix + "1";
                return animator.Data.States.FirstOrDefault(state =>
                           string.Equals(state, preferred, StringComparison.OrdinalIgnoreCase))
                       ?? animator.Data.States.FirstOrDefault(state =>
                           state.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                       ?? SelectAliveAction(animator, life);
            }
            return SelectAliveAction(animator, life);
        }

        private static string SelectAliveAction(StateMachineAnimator animator, LifeItem life)
        {
            string[] preferred = { "stand", "move", "fly", "attack1", "jump", "regen" };
            var available = preferred
                .Where(name => animator.Data.States.Any(state =>
                    string.Equals(state, name, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            if (available.Length > 0)
            {
                string selected = available[Math.Abs(life.Index + life.ID) % available.Length];
                return animator.Data.States.First(state =>
                    string.Equals(state, selected, StringComparison.OrdinalIgnoreCase));
            }
            return animator.Data.States.FirstOrDefault(state =>
                !state.StartsWith("hit", StringComparison.OrdinalIgnoreCase) &&
                !state.StartsWith("die", StringComparison.OrdinalIgnoreCase));
        }

        private static void PopulateMobAnimation(TargetItem target, LifeItem life)
        {
            if (!(life.View?.Animator is StateMachineAnimator animator))
            {
                target.CombatState = MobCombatState.Unknown;
                return;
            }
            string action = animator.Data.SelectedState ?? string.Empty;
            target.AnimationName = action;
            target.AnimationFrame = (animator.Data as StateMachineAnimator.FrameStateMachineData)
                ?.FrameAnimator?.CurrentFrameIndex ?? -1;
            target.CombatState = action.StartsWith("die", StringComparison.OrdinalIgnoreCase)
                ? MobCombatState.Dying
                : action.StartsWith("hit", StringComparison.OrdinalIgnoreCase)
                    ? MobCombatState.Hit
                    : MobCombatState.Alive;
        }

        private static double GetSpawnOpacity(LifeItem life, double progress)
        {
            double opacity = 0.35 + Math.Max(0.0, Math.Min(1.0, progress)) * 0.40;
            int variation = Math.Abs(life.Index + life.ID) % 5 - 2;
            return Math.Max(0.28, Math.Min(0.80, opacity + variation * 0.035));
        }

    }
}
