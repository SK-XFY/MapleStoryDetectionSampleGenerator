using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using MapRender.Invoker;

namespace MapleStory.Sampler
{
    /// <summary>
    /// Writes enlarged Mob crops as an Ultralytics classification dataset while
    /// retaining a UTF-8 CSV manifest with animation/effect provenance.
    /// </summary>
    public sealed class MobStateDatasetWriter : IDisposable
    {
        private const double ValidationPortion = 0.10;
        private readonly string _root;
        private readonly StreamWriter _manifest;
        private readonly object _writeLock = new object();
        private int _cropCount;

        public MobStateDatasetWriter(string outputPath)
        {
            _root = Path.Combine(outputPath, "mob-state");
            foreach (string split in new[] { "train", "val" })
            {
                foreach (string state in new[] { "alive", "hit", "dying", "unknown" })
                {
                    Directory.CreateDirectory(Path.Combine(_root, "images", split, state));
                }
            }
            string manifestPath = Path.Combine(_root, "manifest.csv");
            bool writeHeader = !File.Exists(manifestPath) || new FileInfo(manifestPath).Length == 0;
            _manifest = new StreamWriter(
                new FileStream(manifestPath, FileMode.Append, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(false));
            if (writeHeader)
            {
                _manifest.WriteLine(
                    "file,split,state,mob_id,animation,frame,skill_id,visible_fraction,source_sample");
                _manifest.Flush();
            }
        }

        public int CropCount => _cropCount;

        public Sample Process(Sample sample)
        {
            if (sample == null)
            {
                throw new ArgumentNullException(nameof(sample));
            }
            TargetItem[] mobs = sample.Items
                .Where(item => item.Type == ObjectClass.Mob && item.Width > 0 && item.Height > 0)
                .ToArray();
            if (mobs.Length == 0)
            {
                return sample;
            }

            sample.ImageStream.Position = 0;
            using (var source = new Bitmap(sample.ImageStream))
            {
                for (int index = 0; index < mobs.Length; index++)
                {
                    TargetItem mob = mobs[index];
                    string state = ToDirectoryName(mob.CombatState);
                    string split = IsValidation(sample.Guid, index) ? "val" : "train";
                    Rectangle cropRectangle = CreateCropRectangle(mob, source.Size);
                    if (cropRectangle.Width < 2 || cropRectangle.Height < 2)
                    {
                        continue;
                    }
                    string fileName = string.Format(CultureInfo.InvariantCulture,
                        "{0}_{1:D3}_{2}_{3}.jpg", sample.Guid, index, mob.Id, state);
                    string relativePath = Path.Combine("images", split, state, fileName);
                    string fullPath = Path.Combine(_root, relativePath);

                    using (var crop = new Bitmap(cropRectangle.Width, cropRectangle.Height))
                    using (Graphics graphics = Graphics.FromImage(crop))
                    {
                        graphics.DrawImage(source,
                            new Rectangle(Point.Empty, crop.Size),
                            cropRectangle,
                            GraphicsUnit.Pixel);
                        crop.Save(fullPath, ImageFormat.Jpeg);
                    }

                    lock (_writeLock)
                    {
                        _manifest.WriteLine(string.Join(",",
                            Csv(relativePath.Replace('\\', '/')),
                            split,
                            state,
                            mob.Id.ToString(CultureInfo.InvariantCulture),
                            Csv(mob.AnimationName ?? string.Empty),
                            mob.AnimationFrame.ToString(CultureInfo.InvariantCulture),
                            mob.EffectSkillId.ToString(CultureInfo.InvariantCulture),
                            mob.VisibleFraction.ToString("0.0000", CultureInfo.InvariantCulture),
                            sample.Guid.ToString()));
                        _manifest.Flush();
                        _cropCount++;
                    }
                }
            }
            sample.ImageStream.Position = 0;
            return sample;
        }

        private static Rectangle CreateCropRectangle(TargetItem mob, Size image)
        {
            int width = Math.Max(96, (int)Math.Round(mob.Width * 1.80));
            int height = Math.Max(96, (int)Math.Round(mob.Height * 1.80));
            int centerX = mob.X + mob.Width / 2;
            int centerY = mob.Y + mob.Height / 2;
            var desired = new Rectangle(centerX - width / 2, centerY - height / 2, width, height);
            return Rectangle.Intersect(desired, new Rectangle(Point.Empty, image));
        }

        private static bool IsValidation(Guid sampleId, int index)
        {
            byte[] bytes = sampleId.ToByteArray();
            int value = (bytes[0] + bytes[5] + index * 17) & 0xff;
            return value < 256 * ValidationPortion;
        }

        private static string ToDirectoryName(MobCombatState state)
        {
            switch (state)
            {
                case MobCombatState.Hit: return "hit";
                case MobCombatState.Dying: return "dying";
                case MobCombatState.Unknown: return "unknown";
                default: return "alive";
            }
        }

        private static string Csv(string value)
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        public void Dispose()
        {
            _manifest?.Dispose();
        }
    }
}
