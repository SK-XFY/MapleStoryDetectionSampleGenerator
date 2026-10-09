using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using MapRender.Invoker;

namespace MapleStory.Sampler.PostProcessor
{
    public sealed class SkillHitEffectProcessor : IPostProcessor
    {
        private readonly SkillEffectAsset[] _effects;
        private readonly double _hitProbability;
        private readonly double _dyingProbability;
        private readonly double _minimumVisible;
        private readonly Random _seedGenerator = new Random();
        private readonly object _randomLock = new object();
        private int _appliedEffects;

        public SkillHitEffectProcessor(
            IEnumerable<SkillEffectAsset> effects,
            double hitProbability = 0.80,
            double dyingProbability = 1.0,
            double minimumVisible = 0.55)
        {
            _effects = (effects ?? throw new ArgumentNullException(nameof(effects))).ToArray();
            if (_effects.Length == 0)
            {
                throw new ArgumentException("At least one Skill WZ effect is required.", nameof(effects));
            }
            ValidateRatio(hitProbability, nameof(hitProbability));
            ValidateRatio(dyingProbability, nameof(dyingProbability));
            ValidateRatio(minimumVisible, nameof(minimumVisible));
            _hitProbability = hitProbability;
            _dyingProbability = dyingProbability;
            _minimumVisible = minimumVisible;
        }

        public int AppliedEffects => Volatile.Read(ref _appliedEffects);

        public Sample Process(Sample sample)
        {
            var targets = sample.Items
                .Where(item => item.Type == ObjectClass.Mob &&
                               item.Width > 0 && item.Height > 0 &&
                               (item.CombatState == MobCombatState.Hit ||
                                item.CombatState == MobCombatState.Dying))
                .ToArray();
            if (targets.Length == 0)
            {
                return sample;
            }

            int seed;
            lock (_randomLock)
            {
                seed = _seedGenerator.Next();
            }
            var random = new Random(seed);
            sample.ImageStream.Position = 0;
            using (var result = new Bitmap(sample.ImageStream))
            using (Graphics graphics = Graphics.FromImage(result))
            {
                graphics.CompositingMode = CompositingMode.SourceOver;
                graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                graphics.PixelOffsetMode = PixelOffsetMode.Half;

                foreach (TargetItem target in targets)
                {
                    double probability = target.CombatState == MobCombatState.Dying
                        ? _dyingProbability
                        : _hitProbability;
                    if (random.NextDouble() > probability)
                    {
                        continue;
                    }

                    SkillEffectAsset effect = _effects[random.Next(_effects.Length)];
                    using (var stream = new MemoryStream(effect.PngBytes, writable: false))
                    using (var bitmap = new Bitmap(stream))
                    {
                        Rectangle draw = CreatePlacement(random, target, bitmap.Size, result.Size);
                        // Death screenshots commonly contain a fading skill frame. Varying alpha
                        // keeps the effect present in every dying sample while preserving enough
                        // Mob pixels for the state classifier in most samples.
                        double opacity = target.CombatState == MobCombatState.Dying
                            ? 0.35 + random.NextDouble() * 0.65
                            : 0.55 + random.NextDouble() * 0.45;
                        double coverage = EstimateAlphaCoverage(bitmap, draw,
                            new Rectangle(target.X, target.Y, target.Width, target.Height), opacity);

                        // Natural foreground occlusion runs before skill effects. Reserve enough
                        // visible Mob pixels across both layers instead of allowing their coverage
                        // to compound until only a tiny fragment remains.
                        double currentVisible = Math.Max(0.0001, target.VisibleFraction);
                        double maximumCoverage = Math.Max(0.0,
                            1.0 - _minimumVisible / currentVisible);
                        if (coverage > maximumCoverage && coverage > 0.0)
                        {
                            opacity *= maximumCoverage / coverage;
                            coverage = maximumCoverage;
                        }
                        DrawWithOpacity(graphics, bitmap, draw, opacity);
                        target.EffectSkillId = effect.SkillId;
                        target.VisibleFraction = Math.Max(0.0,
                            target.VisibleFraction * (1.0 - coverage));
                        if (target.VisibleFraction + 0.0001 < _minimumVisible)
                        {
                            target.DetectionEnabled = false;
                        }
                        if (coverage >= 0.88)
                        {
                            target.CombatState = MobCombatState.Unknown;
                        }
                        Interlocked.Increment(ref _appliedEffects);
                    }
                }

                var replacement = new MemoryStream();
                result.Save(replacement, ImageFormat.Png);
                replacement.Position = 0;
                sample.ImageStream = replacement;
            }
            return sample;
        }

        private static Rectangle CreatePlacement(
            Random random,
            TargetItem target,
            Size effect,
            Size image)
        {
            double scale = 0.85 + random.NextDouble() * 0.30;
            int width = Math.Max(2, (int)Math.Round(effect.Width * scale));
            int height = Math.Max(2, (int)Math.Round(effect.Height * scale));
            double limitScale = Math.Min(
                image.Width * 0.80 / Math.Max(1, width),
                image.Height * 0.80 / Math.Max(1, height));
            if (limitScale < 1.0)
            {
                width = Math.Max(2, (int)Math.Round(width * limitScale));
                height = Math.Max(2, (int)Math.Round(height * limitScale));
            }
            int jitterX = (int)Math.Round((random.NextDouble() - 0.5) * target.Width * 0.30);
            int jitterY = (int)Math.Round((random.NextDouble() - 0.5) * target.Height * 0.25);
            return new Rectangle(
                target.X + target.Width / 2 - width / 2 + jitterX,
                target.Y + target.Height / 2 - height / 2 + jitterY,
                width,
                height);
        }

        private static double EstimateAlphaCoverage(
            Bitmap source,
            Rectangle draw,
            Rectangle target,
            double opacity)
        {
            Rectangle intersection = Rectangle.Intersect(draw, target);
            if (intersection.Width <= 0 || intersection.Height <= 0 || target.Width * target.Height <= 0)
            {
                return 0.0;
            }
            double covered = 0;
            for (int y = intersection.Top; y < intersection.Bottom; y++)
            {
                int sourceY = Math.Max(0, Math.Min(source.Height - 1,
                    (int)((long)(y - draw.Top) * source.Height / Math.Max(1, draw.Height))));
                for (int x = intersection.Left; x < intersection.Right; x++)
                {
                    int sourceX = Math.Max(0, Math.Min(source.Width - 1,
                        (int)((long)(x - draw.Left) * source.Width / Math.Max(1, draw.Width))));
                    covered += source.GetPixel(sourceX, sourceY).A / 255.0 * opacity;
                }
            }
            return Math.Min(1.0, (double)covered / (target.Width * target.Height));
        }

        private static void DrawWithOpacity(
            Graphics graphics,
            Bitmap bitmap,
            Rectangle destination,
            double opacity)
        {
            using (var attributes = new ImageAttributes())
            {
                var matrix = new ColorMatrix { Matrix33 = (float)opacity };
                attributes.SetColorMatrix(matrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
                graphics.DrawImage(
                    bitmap,
                    destination,
                    0,
                    0,
                    bitmap.Width,
                    bitmap.Height,
                    GraphicsUnit.Pixel,
                    attributes);
            }
        }

        private static void ValidateRatio(double value, string name)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0.0 || value > 1.0)
            {
                throw new ArgumentOutOfRangeException(name, "Value must be between 0 and 1.");
            }
        }
    }
}
