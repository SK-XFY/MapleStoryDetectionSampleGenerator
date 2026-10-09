using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using WzComparerR2.Avatar;
using WzComparerR2.PluginBase;
using WzComparerR2.WzLib;

namespace MapRender.Invoker
{
    internal sealed class RandomAvatarGenerator
    {
        private static readonly string[] Actions = { "stand1", "stand2", "walk1", "walk2", "alert" };
        private readonly ExtractedImgDataSource _extractedData;
        private readonly Random _random = new Random();
        private readonly Dictionary<string, string[]> _paths =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        private (string Body, string Head)[] _skinPairs = Array.Empty<(string, string)>();

        public RandomAvatarGenerator(ExtractedImgDataSource extractedData)
        {
            _extractedData = extractedData;
        }

        public IReadOnlyList<byte[]> Create(int count)
        {
            if (count <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count), "Avatar count must be positive.");
            }

            LoadCandidatePaths();
            var result = new List<byte[]>(count);
            int attempts = Math.Max(48, count * 18);
            for (int attempt = 0; attempt < attempts && result.Count < count; attempt++)
            {
                try
                {
                    byte[] avatar = TryCreateAvatar();
                    if (avatar != null)
                    {
                        result.Add(avatar);
                    }
                }
                catch (Exception ex)
                {
                    if (result.Count == 0 && attempt == attempts - 1)
                    {
                        throw new InvalidDataException(
                            "Unable to assemble an avatar from the Character WZ/IMG resources.", ex);
                    }
                }
            }

            if (result.Count == 0)
            {
                throw new InvalidDataException(
                    "No compatible avatars could be assembled from the Character WZ/IMG resources.");
            }

            Console.WriteLine("Generated {0} random Character WZ avatar occluder(s).", result.Count);
            return result;
        }

        private byte[] TryCreateAvatar()
        {
            bool flashyFashion = _random.NextDouble() < 0.75;
            var canvas = new AvatarCanvas
            {
                ActionName = Pick(Actions),
                EmotionName = "default",
                HairCover = _random.NextDouble() < 0.70,
                ShowHairShade = true
            };
            if (!canvas.LoadZ() || !canvas.LoadActions())
            {
                throw new InvalidDataException("Character z-map or body actions are unavailable.");
            }

            (string body, string head) = Pick(_skinPairs);
            AddRequired(canvas, body);
            AddRequired(canvas, head);
            AddRequired(canvas, PickPath("Face"));
            AddRequired(canvas, PickPath("Hair"));

            bool useLongcoat = HasPaths("Longcoat") &&
                               _random.NextDouble() < (flashyFashion ? 0.65 : 0.35);
            if (useLongcoat)
            {
                AddRequired(canvas, PickPath("Longcoat"));
            }
            else
            {
                AddRequired(canvas, PickPath("Coat"));
                AddRequired(canvas, PickPath("Pants"));
            }

            TryAddOptional(canvas, "Shoes", flashyFashion ? 0.98 : 0.85);
            TryAddOptional(canvas, "Cap", flashyFashion ? 0.92 : 0.55);
            TryAddOptional(canvas, "Glove", flashyFashion ? 0.70 : 0.35);
            TryAddOptional(canvas, "Cape", flashyFashion ? 0.75 : 0.25);
            TryAddOptional(canvas, "Weapon", flashyFashion ? 0.96 : 0.70);
            TryAddOptional(canvas, "Shield", flashyFashion ? 0.42 : 0.15);
            int accessoryAttempts = flashyFashion ? 3 : 1;
            for (int index = 0; index < accessoryAttempts; index++)
            {
                TryAddOptional(canvas, "Accessory", flashyFashion ? 0.88 : 0.35);
            }

            if (canvas.Weapon != null)
            {
                List<int> cashWeaponTypes = canvas.GetCashWeaponTypes();
                if (cashWeaponTypes.Count > 0)
                {
                    canvas.WeaponType = Pick(cashWeaponTypes);
                }
                canvas.WeaponIndex = _random.Next(5);
            }

            canvas.LoadEmotions();
            if (canvas.Emotions.Count > 0)
            {
                canvas.EmotionName = Pick(canvas.Emotions);
            }
            var actionFrames = canvas.GetActionFrames(canvas.ActionName)
                .Where(frame => frame.Delay != 0)
                .ToArray();
            if (actionFrames.Length == 0)
            {
                return null;
            }
            var faceFrames = canvas.GetFaceFrames(canvas.EmotionName);
            var bone = canvas.CreateFrame(
                actionFrames[_random.Next(actionFrames.Length)],
                faceFrames.Length == 0 ? null : faceFrames[_random.Next(faceFrames.Length)],
                null);
            var rendered = canvas.DrawFrame(bone);
            if (rendered.Bitmap == null || rendered.Bitmap.Width <= 1 || rendered.Bitmap.Height <= 1)
            {
                return null;
            }
            if (rendered.Bitmap.Width > 512 || rendered.Bitmap.Height > 512)
            {
                rendered.Bitmap.Dispose();
                return null;
            }

            using (rendered.Bitmap)
            using (var stream = new MemoryStream())
            {
                rendered.Bitmap.Save(stream, ImageFormat.Png);
                return stream.ToArray();
            }
        }

        private void TryAddOptional(AvatarCanvas canvas, string category, double probability)
        {
            if (HasPaths(category) && _random.NextDouble() < probability)
            {
                try
                {
                    AddRequired(canvas, PickPath(category));
                }
                catch
                {
                    // Some cash/custom equipment only supports specific jobs or actions.
                    // Keep the rest of the outfit instead of discarding the whole avatar.
                }
            }
        }

        private static void AddRequired(AvatarCanvas canvas, string path)
        {
            Wz_Node node = PluginManager.FindWz(path);
            if (node == null || canvas.AddPart(node) == null)
            {
                throw new InvalidDataException($"Cannot load avatar part: {path}");
            }
        }

        private void LoadCandidatePaths()
        {
            foreach (string category in new[]
                     {
                         "Face", "Hair", "Cap", "Coat", "Longcoat", "Pants", "Shoes", "Glove",
                         "Cape", "Weapon", "Shield", "Accessory"
                     })
            {
                _paths[category] = EnumerateCategory(category).ToArray();
            }

            _skinPairs = EnumerateSkinPairs().ToArray();

            foreach (string required in new[] { "Face", "Hair", "Coat", "Pants" })
            {
                if (!HasPaths(required))
                {
                    throw new InvalidDataException($"Character WZ has no usable {required} IMG resources.");
                }
            }
            if (_skinPairs.Length == 0)
            {
                throw new InvalidDataException("Character WZ has no matching body/head skin pairs.");
            }
        }

        private IEnumerable<(string Body, string Head)> EnumerateSkinPairs()
        {
            IEnumerable<string> paths;
            if (_extractedData != null)
            {
                paths = _extractedData.EnumerateImagePaths("Character");
            }
            else
            {
                Wz_Node root = PluginManager.FindWz(Wz_Type.Character);
                paths = root?.Nodes
                            .Where(node => node.Text.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
                            .Select(node => $@"Character\{node.Text}")
                        ?? Enumerable.Empty<string>();
            }

            var byId = paths
                .Select(path => (Path: path, Name: Path.GetFileNameWithoutExtension(path)))
                .Where(item => int.TryParse(item.Name, out _))
                .ToDictionary(item => int.Parse(item.Name), item => item.Path);
            foreach (KeyValuePair<int, string> item in byId.OrderBy(item => item.Key))
            {
                if (item.Key < 2000 || item.Key >= 3000)
                {
                    continue;
                }
                if (byId.TryGetValue(item.Key + 10000, out string head))
                {
                    yield return (item.Value, head);
                }
            }
        }

        private IEnumerable<string> EnumerateCategory(string category)
        {
            if (_extractedData != null)
            {
                return _extractedData.EnumerateImagePaths(Path.Combine("Character", category));
            }

            Wz_Node root = PluginManager.FindWz(Wz_Type.Character);
            Wz_Node directory = root?.Nodes.FirstOrDefault(node =>
                string.Equals(node.Text, category, StringComparison.OrdinalIgnoreCase));
            return directory?.Nodes
                       .Where(node => node.Text.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
                       .Select(node => $@"Character\{category}\{node.Text}")
                   ?? Enumerable.Empty<string>();
        }

        private bool HasPaths(string category)
        {
            return _paths.TryGetValue(category, out string[] paths) && paths.Length > 0;
        }

        private string PickPath(string category)
        {
            string[] paths = _paths[category];
            return paths[_random.Next(paths.Length)];
        }

        private T Pick<T>(IReadOnlyList<T> values)
        {
            return values[_random.Next(values.Count)];
        }
    }
}
