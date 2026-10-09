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
    /// <summary>
    /// Draws real Character-WZ avatars and Map-WZ objects over Mob targets without adding another class.
    /// </summary>
    public sealed class MobOcclusionProcessor : IPostProcessor
    {
        private readonly byte[][] _avatarOccluders;
        private readonly byte[][] _npcOccluders;
        private readonly byte[][] _dropItemOccluders;
        private readonly MobOccluderAsset[] _mobOccluders;
        private readonly byte[][] _mapOccluders;
        private readonly double _sampleProbability;
        private readonly double _minimumOcclusion;
        private readonly double _maximumOcclusion;
        private readonly double _minimumVisible;
        private readonly Random _seedGenerator = new Random();
        private readonly object _randomLock = new object();
        private readonly Queue<PreviewDecision> _previewDecisions = new Queue<PreviewDecision>();
        private int _processedSamples;
        private int _occludedMobs;
        private int _droppedLabels;

        public MobOcclusionProcessor(
            IEnumerable<byte[]> avatarOccluders,
            IEnumerable<byte[]> npcOccluders,
            IEnumerable<byte[]> dropItemOccluders,
            IEnumerable<MobOccluderAsset> mobOccluders,
            IEnumerable<byte[]> mapOccluders,
            double sampleProbability = 0.30,
            double minimumOcclusion = 0.08,
            double maximumOcclusion = 0.35,
            double minimumVisible = 0.55)
        {
            _avatarOccluders = CopyAssets(avatarOccluders, nameof(avatarOccluders));
            _npcOccluders = CopyAssets(npcOccluders, nameof(npcOccluders));
            _dropItemOccluders = CopyAssets(dropItemOccluders, nameof(dropItemOccluders));
            _mobOccluders = CopyMobAssets(mobOccluders, nameof(mobOccluders));
            _mapOccluders = CopyAssets(mapOccluders, nameof(mapOccluders));
            if (_avatarOccluders.Length == 0 && _npcOccluders.Length == 0 &&
                _dropItemOccluders.Length == 0 && _mobOccluders.Length == 0 &&
                _mapOccluders.Length == 0)
            {
                throw new ArgumentException(
                    "At least one avatar, NPC, drop-item, Mob, or map occluder is required.");
            }
            ValidateRatio(sampleProbability, nameof(sampleProbability), allowZero: true);
            ValidateRatio(minimumOcclusion, nameof(minimumOcclusion), allowZero: false);
            ValidateRatio(maximumOcclusion, nameof(maximumOcclusion), allowZero: false);
            ValidateRatio(minimumVisible, nameof(minimumVisible), allowZero: false);
            if (minimumOcclusion >= maximumOcclusion)
            {
                throw new ArgumentException("Minimum occlusion must be less than maximum occlusion.");
            }

            _sampleProbability = sampleProbability;
            _minimumOcclusion = minimumOcclusion;
            _maximumOcclusion = maximumOcclusion;
            _minimumVisible = minimumVisible;
        }

        public int ProcessedSamples => Volatile.Read(ref _processedSamples);
        public int OccludedMobs => Volatile.Read(ref _occludedMobs);
        public int DroppedLabels => Volatile.Read(ref _droppedLabels);

        public void ConfigurePreviewSchedule(int sampleCount)
        {
            if (sampleCount <= 0)
            {
                return;
            }

            lock (_randomLock)
            {
                _previewDecisions.Clear();
                List<(OccluderKind Kind, double Weight)> candidates = GetCandidates();
                List<OccluderKind> available = candidates.Select(candidate => candidate.Kind).ToList();
                int occludedCount = Math.Max(0, Math.Min(sampleCount,
                    (int)Math.Round(sampleCount * _sampleProbability,
                        MidpointRounding.AwayFromZero)));
                var forcedKinds = new List<OccluderKind>(occludedCount);
                if (occludedCount >= available.Count)
                {
                    forcedKinds.AddRange(available);
                }
                var kindCounts = forcedKinds
                    .GroupBy(kind => kind)
                    .ToDictionary(group => group.Key, group => group.Count());
                double totalWeight = candidates.Sum(candidate => candidate.Weight);
                while (forcedKinds.Count < occludedCount)
                {
                    OccluderKind next = candidates
                        .OrderByDescending(candidate =>
                            occludedCount * candidate.Weight / totalWeight -
                            (kindCounts.TryGetValue(candidate.Kind, out int current) ? current : 0))
                        .First().Kind;
                    forcedKinds.Add(next);
                    kindCounts[next] = kindCounts.TryGetValue(next, out int count) ? count + 1 : 1;
                }
                forcedKinds = forcedKinds.OrderBy(_ => _seedGenerator.Next()).ToList();

                var scheduled = new Dictionary<int, OccluderKind>();
                for (int index = 0; index < forcedKinds.Count; index++)
                {
                    int position = (int)Math.Floor(
                        (index + 0.5) * sampleCount / forcedKinds.Count);
                    scheduled[position] = forcedKinds[index];
                }
                for (int index = 0; index < sampleCount; index++)
                {
                    _previewDecisions.Enqueue(scheduled.TryGetValue(index, out OccluderKind kind)
                        ? new PreviewDecision(true, kind)
                        : new PreviewDecision(false, default));
                }
            }
        }

        public Sample Process(Sample sample)
        {
            if (sample == null)
            {
                throw new ArgumentNullException(nameof(sample));
            }

            var mobs = sample.Items.Where(item => item.Type == ObjectClass.Mob && item.Width > 0 && item.Height > 0)
                .ToList();
            if (mobs.Count == 0)
            {
                return sample;
            }

            int seed;
            PreviewDecision? previewDecision = null;
            lock (_randomLock)
            {
                seed = _seedGenerator.Next();
                if (_previewDecisions.Count > 0)
                {
                    previewDecision = _previewDecisions.Dequeue();
                }
            }
            var random = new Random(seed);
            if (previewDecision.HasValue
                ? !previewDecision.Value.Occlude
                : random.NextDouble() >= _sampleProbability)
            {
                return sample;
            }

            int targetCount = mobs.Count > 1 && random.NextDouble() < 0.30 ? 2 : 1;
            var targets = mobs.OrderBy(_ => random.Next()).Take(targetCount).ToArray();
            sample.ImageStream.Position = 0;
            using (var result = new Bitmap(sample.ImageStream))
            using (Graphics graphics = Graphics.FromImage(result))
            {
                graphics.CompositingMode = CompositingMode.SourceOver;
                graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                graphics.PixelOffsetMode = PixelOffsetMode.Half;

                foreach (TargetItem mob in targets)
                {
                    OccluderKind kind = previewDecision?.Kind ?? PickOccluderKind(random);
                    MobOccluderAsset mobAsset = null;
                    byte[] bytes;
                    if (kind == OccluderKind.Mob)
                    {
                        mobAsset = _mobOccluders[random.Next(_mobOccluders.Length)];
                        bytes = mobAsset.ImageBytes;
                    }
                    else
                    {
                        byte[][] source = GetAssets(kind);
                        bytes = source[random.Next(source.Length)];
                    }
                    using (var occluderStream = new MemoryStream(bytes, writable: false))
                    using (var sourceBitmap = new Bitmap(occluderStream))
                    using (var occluder = TrimTransparent(sourceBitmap))
                    {
                        Rectangle mobRectangle = new Rectangle(mob.X, mob.Y, mob.Width, mob.Height);
                        double alphaDensity = GetAlphaDensity(occluder);
                        Placement placement;
                        switch (kind)
                        {
                            case OccluderKind.Avatar:
                            case OccluderKind.Npc:
                            case OccluderKind.Mob:
                                placement = CreateCharacterPlacement(
                                    random, mobRectangle, occluder.Size, result.Size, alphaDensity);
                                break;
                            case OccluderKind.DropItem:
                                placement = CreateDropItemPlacement(
                                    random, mobRectangle, occluder.Size, result.Size, alphaDensity);
                                break;
                            default:
                                placement = CreateMapPlacement(
                                    random, mobRectangle, occluder.Size, result.Size, alphaDensity);
                                break;
                        }
                        if (placement.FlipX)
                        {
                            occluder.RotateFlip(RotateFlipType.RotateNoneFlipX);
                        }
                        graphics.DrawImage(occluder, placement.DrawRectangle);

                        // A large cash outfit, weapon aura, or NPC frame can overlap nearby
                        // Mobs as well as the selected target. Update every affected label so
                        // those neighboring boxes are not incorrectly recorded as fully visible.
                        foreach (TargetItem affected in mobs)
                        {
                            var affectedRectangle = new Rectangle(
                                affected.X, affected.Y, affected.Width, affected.Height);
                            double alphaCoverage = EstimateAlphaCoverage(
                                occluder, placement.DrawRectangle, affectedRectangle);
                            if (alphaCoverage < 0.005)
                            {
                                continue;
                            }

                            double visible = affected.VisibleFraction * (1.0 - alphaCoverage);
                            affected.OcclusionSource = GetSourceName(kind);
                            if (visible < _minimumVisible && affected.DetectionEnabled)
                            {
                                affected.DetectionEnabled = false;
                                affected.CombatState = MobCombatState.Unknown;
                                Interlocked.Increment(ref _droppedLabels);
                            }
                            affected.VisibleFraction = Math.Max(0.0, visible);
                            Interlocked.Increment(ref _occludedMobs);
                        }

                        if (kind == OccluderKind.Mob && mobAsset != null)
                        {
                            Rectangle visibleMob = Rectangle.Intersect(
                                placement.DrawRectangle,
                                new Rectangle(Point.Empty, result.Size));
                            if (visibleMob.Width > 1 && visibleMob.Height > 1)
                            {
                                sample.Items.Add(new TargetItem
                                {
                                    Id = mobAsset.MobId,
                                    X = visibleMob.X,
                                    Y = visibleMob.Y,
                                    Width = visibleMob.Width,
                                    Height = visibleMob.Height,
                                    Type = ObjectClass.Mob,
                                    CombatState = MobCombatState.Alive,
                                    AnimationName = "synthetic-overlap",
                                    AnimationFrame = 0,
                                    DetectionEnabled = true,
                                    VisibleFraction = 1.0
                                });
                            }
                        }
                    }
                }

                var replacement = new MemoryStream();
                result.Save(replacement, ImageFormat.Png);
                replacement.Position = 0;
                sample.ImageStream = replacement;
            }

            Interlocked.Increment(ref _processedSamples);
            return sample;
        }

        private Placement CreateCharacterPlacement(
            Random random,
            Rectangle mob,
            Size avatar,
            Size image,
            double alphaDensity)
        {
            double desiredRatio = _minimumOcclusion +
                                  random.NextDouble() * (_maximumOcclusion - _minimumOcclusion);

            // Character-WZ frames are already rendered at the same pixel scale as the map.
            // Keep that native scale instead of shrinking the player to the selected Mob.
            double scale = 0.95 + random.NextDouble() * 0.10;
            int drawWidth = Math.Max(2, (int)Math.Round(avatar.Width * scale));
            int drawHeight = Math.Max(2, (int)Math.Round(avatar.Height * scale));

            int verticalOverlap = Math.Max(1, Math.Min(drawHeight, mob.Height));
            double desiredArea = mob.Width * mob.Height * desiredRatio /
                                 Math.Max(0.15, alphaDensity);
            int overlapWidth = Clamp(
                (int)Math.Round(desiredArea / verticalOverlap),
                1,
                Math.Min(drawWidth, mob.Width));

            int roomOnLeft = mob.Left;
            int roomOnRight = image.Width - mob.Right;
            bool standOnLeft;
            if (roomOnLeft < drawWidth - overlapWidth && roomOnRight >= drawWidth - overlapWidth)
            {
                standOnLeft = false;
            }
            else if (roomOnRight < drawWidth - overlapWidth && roomOnLeft >= drawWidth - overlapWidth)
            {
                standOnLeft = true;
            }
            else
            {
                standOnLeft = random.NextDouble() < 0.5;
            }

            int x = standOnLeft
                ? mob.Left - drawWidth + overlapWidth
                : mob.Right - overlapWidth;

            // A melee attacker and the Mob stand on the same platform: align the
            // bottom of the cropped avatar frame with the bottom of the Mob frame.
            int y = mob.Bottom - drawHeight + random.Next(-2, 3);
            var draw = new Rectangle(x, y, drawWidth, drawHeight);
            return CreatePlacement(draw, mob, image, flipX: !standOnLeft);
        }

        private Placement CreateDropItemPlacement(
            Random random,
            Rectangle mob,
            Size item,
            Size image,
            double alphaDensity)
        {
            double desiredRatio = _minimumOcclusion +
                                  random.NextDouble() * (_maximumOcclusion - _minimumOcclusion);

            // Item/Equipment iconRaw frames are close to their in-game drop size.
            // Keep them near native scale and let part of the icon sit below the Mob box,
            // exactly like loot lying on the foothold in front of a Mob's feet.
            double scale = 0.85 + random.NextDouble() * 0.25;
            int drawWidth = Math.Max(2, (int)Math.Round(item.Width * scale));
            int drawHeight = Math.Max(2, (int)Math.Round(item.Height * scale));
            int maximumDimension = Math.Max(12, (int)Math.Round(Math.Max(mob.Width, mob.Height) * 0.90));
            if (drawWidth > maximumDimension || drawHeight > maximumDimension)
            {
                double limit = Math.Min(
                    (double)maximumDimension / drawWidth,
                    (double)maximumDimension / drawHeight);
                drawWidth = Math.Max(2, (int)Math.Round(drawWidth * limit));
                drawHeight = Math.Max(2, (int)Math.Round(drawHeight * limit));
            }

            int overlapWidth = Math.Max(1, Math.Min(drawWidth, mob.Width));
            double desiredArea = mob.Width * mob.Height * desiredRatio /
                                 Math.Max(0.15, alphaDensity);
            int overlapHeight = Clamp(
                (int)Math.Round(desiredArea / overlapWidth),
                1,
                Math.Min(drawHeight, mob.Height));
            int horizontalJitter = Math.Max(1, (int)Math.Round(mob.Width * 0.22));
            int x = mob.Left + (mob.Width - drawWidth) / 2 +
                    random.Next(-horizontalJitter, horizontalJitter + 1);
            int y = mob.Bottom - overlapHeight + random.Next(-1, 3);
            return CreatePlacement(
                new Rectangle(x, y, drawWidth, drawHeight),
                mob,
                image,
                flipX: random.NextDouble() < 0.5);
        }

        private Placement CreateMapPlacement(
            Random random,
            Rectangle mob,
            Size avatar,
            Size image,
            double alphaDensity)
        {
            double desiredRatio = _minimumOcclusion +
                                  random.NextDouble() * (_maximumOcclusion - _minimumOcclusion);
            double targetHeight = mob.Height * (0.85 + random.NextDouble() * 0.75);
            double scale = targetHeight / Math.Max(1, avatar.Height);
            int drawWidth = Math.Max(2, (int)Math.Round(avatar.Width * scale));
            int drawHeight = Math.Max(2, (int)Math.Round(avatar.Height * scale));
            int maximumWidth = Math.Max(mob.Width, (int)Math.Round(mob.Width * 2.25));
            if (drawWidth > maximumWidth)
            {
                scale *= (double)maximumWidth / drawWidth;
                drawWidth = maximumWidth;
                drawHeight = Math.Max(2, (int)Math.Round(avatar.Height * scale));
            }

            double desiredArea = mob.Width * mob.Height * desiredRatio /
                                 Math.Max(0.15, alphaDensity);
            int x;
            int y;
            double direction = random.NextDouble();
            if (direction < 0.50) // feet and foreground objects most often hide the lower portion
            {
                int overlapWidth = Math.Max(1, Math.Min(drawWidth, mob.Width));
                int overlapHeight = Clamp((int)Math.Round(desiredArea / overlapWidth), 1,
                    Math.Min(drawHeight, mob.Height));
                x = mob.Left + (mob.Width - drawWidth) / 2 + Jitter(random, mob.Width);
                y = mob.Bottom - overlapHeight;
            }
            else if (direction < 0.70) // left side
            {
                int overlapHeight = Math.Max(1, Math.Min(drawHeight, mob.Height));
                int overlapWidth = Clamp((int)Math.Round(desiredArea / overlapHeight), 1,
                    Math.Min(drawWidth, mob.Width));
                x = mob.Left - drawWidth + overlapWidth;
                y = mob.Top + (mob.Height - drawHeight) / 2 + Jitter(random, mob.Height);
            }
            else if (direction < 0.90) // right side
            {
                int overlapHeight = Math.Max(1, Math.Min(drawHeight, mob.Height));
                int overlapWidth = Clamp((int)Math.Round(desiredArea / overlapHeight), 1,
                    Math.Min(drawWidth, mob.Width));
                x = mob.Right - overlapWidth;
                y = mob.Top + (mob.Height - drawHeight) / 2 + Jitter(random, mob.Height);
            }
            else // occasional top-side occlusion such as effects or platforms
            {
                int overlapWidth = Math.Max(1, Math.Min(drawWidth, mob.Width));
                int overlapHeight = Clamp((int)Math.Round(desiredArea / overlapWidth), 1,
                    Math.Min(drawHeight, mob.Height));
                x = mob.Left + (mob.Width - drawWidth) / 2 + Jitter(random, mob.Width);
                y = mob.Top - drawHeight + overlapHeight;
            }

            var draw = new Rectangle(x, y, drawWidth, drawHeight);
            return CreatePlacement(draw, mob, image, flipX: random.NextDouble() < 0.5);
        }

        private static Placement CreatePlacement(Rectangle draw, Rectangle mob, Size image, bool flipX)
        {
            Rectangle visibleDraw = Rectangle.Intersect(draw, new Rectangle(Point.Empty, image));
            Rectangle intersection = Rectangle.Intersect(mob, visibleDraw);
            double ratio = mob.Width * mob.Height == 0
                ? 0
                : (double)(intersection.Width * intersection.Height) / (mob.Width * mob.Height);
            return new Placement(draw, ratio, flipX);
        }

        private OccluderKind PickOccluderKind(Random random)
        {
            // Ground loot and NPC/player side occlusion are the common in-game cases.
            // Real Mob overlap is common at spawn points. Generic map objects remain rare.
            List<(OccluderKind Kind, double Weight)> candidates = GetCandidates();

            double choice = random.NextDouble() * candidates.Sum(candidate => candidate.Weight);
            foreach ((OccluderKind kind, double weight) in candidates)
            {
                choice -= weight;
                if (choice <= 0)
                {
                    return kind;
                }
            }
            return candidates[candidates.Count - 1].Kind;
        }

        private List<(OccluderKind Kind, double Weight)> GetCandidates()
        {
            var candidates = new List<(OccluderKind Kind, double Weight)>();
            if (_dropItemOccluders.Length > 0) candidates.Add((OccluderKind.DropItem, 0.30));
            if (_mobOccluders.Length > 0) candidates.Add((OccluderKind.Mob, 0.25));
            if (_npcOccluders.Length > 0) candidates.Add((OccluderKind.Npc, 0.20));
            if (_avatarOccluders.Length > 0) candidates.Add((OccluderKind.Avatar, 0.20));
            if (_mapOccluders.Length > 0) candidates.Add((OccluderKind.Map, 0.05));
            return candidates;
        }

        private byte[][] GetAssets(OccluderKind kind)
        {
            switch (kind)
            {
                case OccluderKind.Avatar: return _avatarOccluders;
                case OccluderKind.Npc: return _npcOccluders;
                case OccluderKind.DropItem: return _dropItemOccluders;
                default: return _mapOccluders;
            }
        }

        private static byte[][] CopyAssets(IEnumerable<byte[]> assets, string name)
        {
            return (assets ?? throw new ArgumentNullException(name))
                .Where(bytes => bytes != null && bytes.Length > 0)
                .Select(bytes => bytes.ToArray())
                .ToArray();
        }

        private static MobOccluderAsset[] CopyMobAssets(
            IEnumerable<MobOccluderAsset> assets,
            string name)
        {
            return (assets ?? throw new ArgumentNullException(name))
                .Where(asset => asset != null && asset.MobId > 0 &&
                                asset.ImageBytes != null && asset.ImageBytes.Length > 0)
                .Select(asset => new MobOccluderAsset(asset.MobId, asset.ImageBytes.ToArray()))
                .ToArray();
        }

        private static double EstimateAlphaCoverage(Bitmap source, Rectangle draw, Rectangle target)
        {
            Rectangle intersection = Rectangle.Intersect(draw, target);
            if (intersection.Width <= 0 || intersection.Height <= 0 || target.Width * target.Height <= 0)
            {
                return 0.0;
            }

            double covered = 0.0;
            for (int y = intersection.Top; y < intersection.Bottom; y++)
            {
                int sourceY = Clamp(
                    (int)((long)(y - draw.Top) * source.Height / Math.Max(1, draw.Height)),
                    0,
                    source.Height - 1);
                for (int x = intersection.Left; x < intersection.Right; x++)
                {
                    int sourceX = Clamp(
                        (int)((long)(x - draw.Left) * source.Width / Math.Max(1, draw.Width)),
                        0,
                        source.Width - 1);
                    covered += source.GetPixel(sourceX, sourceY).A / 255.0;
                }
            }
            return Math.Min(1.0, covered / (target.Width * target.Height));
        }

        private static Bitmap TrimTransparent(Bitmap source)
        {
            int left = source.Width;
            int top = source.Height;
            int right = -1;
            int bottom = -1;
            for (int y = 0; y < source.Height; y++)
            {
                for (int x = 0; x < source.Width; x++)
                {
                    if (source.GetPixel(x, y).A <= 8)
                    {
                        continue;
                    }
                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x);
                    bottom = Math.Max(bottom, y);
                }
            }
            if (right < left || bottom < top)
            {
                return new Bitmap(source);
            }
            return source.Clone(
                new Rectangle(left, top, right - left + 1, bottom - top + 1),
                PixelFormat.Format32bppArgb);
        }

        private static double GetAlphaDensity(Bitmap bitmap)
        {
            if (bitmap.Width <= 0 || bitmap.Height <= 0)
            {
                return 0.0;
            }
            double alpha = 0.0;
            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    alpha += bitmap.GetPixel(x, y).A / 255.0;
                }
            }
            return alpha / (bitmap.Width * bitmap.Height);
        }

        private static string GetSourceName(OccluderKind kind)
        {
            switch (kind)
            {
                case OccluderKind.Avatar: return "PLAYER";
                case OccluderKind.Npc: return "NPC";
                case OccluderKind.DropItem: return "DROP";
                case OccluderKind.Mob: return "MOB";
                default: return "MAP";
            }
        }

        private static int Jitter(Random random, int dimension)
        {
            int range = Math.Max(1, (int)Math.Round(dimension * 0.12));
            return random.Next(-range, range + 1);
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private static void ValidateRatio(double value, string name, bool allowZero)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value > 1.0 ||
                (allowZero ? value < 0.0 : value <= 0.0))
            {
                throw new ArgumentOutOfRangeException(name, "Value must be a ratio between 0 and 1.");
            }
        }

        private readonly struct Placement
        {
            public Placement(Rectangle drawRectangle, double occlusionRatio, bool flipX)
            {
                DrawRectangle = drawRectangle;
                OcclusionRatio = occlusionRatio;
                FlipX = flipX;
            }

            public Rectangle DrawRectangle { get; }
            public double OcclusionRatio { get; }
            public bool FlipX { get; }
        }

        private enum OccluderKind
        {
            Avatar,
            Npc,
            DropItem,
            Mob,
            Map
        }

        private readonly struct PreviewDecision
        {
            public PreviewDecision(bool occlude, OccluderKind kind)
            {
                Occlude = occlude;
                Kind = kind;
            }

            public bool Occlude { get; }
            public OccluderKind Kind { get; }
        }
    }
}
