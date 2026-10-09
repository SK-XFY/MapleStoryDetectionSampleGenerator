using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using WzComparerR2;
using WzComparerR2.PluginBase;
using WzComparerR2.WzLib;

namespace MapRender.Invoker
{
    internal sealed class SkillEffectGenerator
    {
        private readonly ExtractedImgDataSource _extractedData;
        private readonly Random _random = new Random();

        public SkillEffectGenerator(ExtractedImgDataSource extractedData)
        {
            _extractedData = extractedData;
        }

        public IReadOnlyList<SkillEffectAsset> Create(
            int count,
            IEnumerable<int> requestedSkillIds,
            int characterLevel = 0)
        {
            if (count <= 0)
            {
                return Array.Empty<SkillEffectAsset>();
            }

            int[] skillIds = (requestedSkillIds ?? Enumerable.Empty<int>())
                .Where(id => id > 0)
                .Distinct()
                .ToArray();
            List<Wz_Node> skillNodes = skillIds.Length > 0
                ? skillIds.Select(ResolveSkillNode).Where(node => node != null).ToList()
                : EnumerateRandomSkillNodes(Math.Max(count * 3, 96), characterLevel).ToList();

            if (skillIds.Length > 0 && skillNodes.Count != skillIds.Length)
            {
                int[] found = skillNodes.Select(ParseSkillId).Where(id => id > 0).Distinct().ToArray();
                int[] missing = skillIds.Except(found).ToArray();
                throw new FileNotFoundException(
                    "Cannot find Skill IMG node(s): " + string.Join(", ", missing));
            }

            var result = new List<SkillEffectAsset>(count);
            int perSkillLimit = Math.Max(1,
                (int)Math.Ceiling((double)count / Math.Max(1, skillNodes.Count)));
            foreach (Wz_Node skillNode in skillNodes.OrderBy(_ => _random.Next()))
            {
                int skillId = ParseSkillId(skillNode);
                int addedForSkill = 0;
                Wz_Node visualRoot = SelectVisualLevelRoot(skillNode, characterLevel);
                if (visualRoot != skillNode)
                {
                    Console.WriteLine(
                        "Skill {0}: using visual branch {1} (requested character level: {2}).",
                        skillId,
                        visualRoot.FullPathToFile,
                        characterLevel == 0 ? "highest" : characterLevel.ToString());
                }
                foreach (Wz_Node branch in GetEffectBranches(visualRoot).OrderBy(_ => _random.Next()))
                {
                    foreach (Wz_Node candidate in EnumerateDescendants(branch, 6)
                                 .OrderBy(_ => _random.Next()))
                    {
                        byte[] bytes = TryExtractFrame(candidate);
                        if (bytes == null)
                        {
                            continue;
                        }
                        result.Add(new SkillEffectAsset(skillId, bytes,
                            candidate.FullPathToFile));
                        addedForSkill++;
                        if (result.Count >= count)
                        {
                            Console.WriteLine("Generated {0} Skill WZ hit-effect frame(s) from skill(s): {1}",
                                result.Count,
                                string.Join(", ", result.Select(item => item.SkillId).Distinct()));
                            return result;
                        }
                        if (addedForSkill >= perSkillLimit)
                        {
                            break;
                        }
                    }
                    if (addedForSkill >= perSkillLimit)
                    {
                        break;
                    }
                }
            }

            if (skillIds.Length > 0)
            {
                int[] missingEffects = skillIds
                    .Except(result.Select(item => item.SkillId).Distinct())
                    .ToArray();
                if (missingEffects.Length > 0)
                {
                    throw new InvalidDataException(
                        "Skill node(s) contain no usable hit/effect frame: " +
                        string.Join(", ", missingEffects));
                }
            }

            Console.WriteLine("Generated {0} Skill WZ hit-effect frame(s) from skill(s): {1}",
                result.Count,
                string.Join(", ", result.Select(item => item.SkillId).Distinct()));
            return result;
        }

        private Wz_Node ResolveSkillNode(int skillId)
        {
            int jobId = skillId / 10000;
            string[] imageNames =
            {
                jobId.ToString(),
                jobId.ToString("D3"),
                jobId.ToString("D4")
            };
            foreach (string imageName in imageNames.Distinct())
            {
                string path = $@"Skill\{imageName}.img\skill\{skillId}";
                Wz_Node node = PluginManager.FindWz(path);
                if (node != null)
                {
                    return node;
                }
            }
            return null;
        }

        private IEnumerable<Wz_Node> EnumerateRandomSkillNodes(int maximum, int characterLevel)
        {
            IEnumerable<Wz_Node> imageRoots;
            if (_extractedData != null)
            {
                imageRoots = _extractedData.EnumerateImagePaths("Skill")
                    .OrderBy(_ => _random.Next())
                    .Select(path => _extractedData.Resolve(path))
                    .Where(node => node != null);
            }
            else
            {
                Wz_Node root = PluginManager.FindWz(Wz_Type.Skill);
                imageRoots = root == null
                    ? Enumerable.Empty<Wz_Node>()
                    : EnumerateImageRoots(root, 4).OrderBy(_ => _random.Next());
            }

            int yielded = 0;
            foreach (Wz_Node imageRoot in imageRoots)
            {
                Wz_Node skillRoot = imageRoot.Nodes["skill"];
                if (skillRoot == null)
                {
                    continue;
                }
                foreach (Wz_Node skill in skillRoot.Nodes.OrderBy(_ => _random.Next()))
                {
                    if (!GetEffectBranches(SelectVisualLevelRoot(skill, characterLevel)).Any())
                    {
                        continue;
                    }
                    yield return skill;
                    if (++yielded >= maximum)
                    {
                        yield break;
                    }
                }
            }
        }

        private static IEnumerable<Wz_Node> EnumerateImageRoots(Wz_Node node, int depth)
        {
            Wz_Image image = node.GetValueEx<Wz_Image>(null);
            if (image != null)
            {
                if (image.TryExtract())
                {
                    yield return image.Node;
                }
                yield break;
            }
            if (depth <= 0)
            {
                yield break;
            }
            foreach (Wz_Node child in node.Nodes)
            {
                foreach (Wz_Node result in EnumerateImageRoots(child, depth - 1))
                {
                    yield return result;
                }
            }
        }

        private static IEnumerable<Wz_Node> GetEffectBranches(Wz_Node skillNode)
        {
            Wz_Node[] hitBranches = skillNode.Nodes.Where(node =>
                    node.Text.StartsWith("hit", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (hitBranches.Length > 0)
            {
                return hitBranches;
            }
            string[] fallbackPrefixes = { "ball", "effect" };
            return skillNode.Nodes.Where(node => fallbackPrefixes.Any(prefix =>
                node.Text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
        }

        private static Wz_Node SelectVisualLevelRoot(Wz_Node skillNode, int characterLevel)
        {
            foreach (string containerName in new[] { "CharLevel", "SkillLevel" })
            {
                Wz_Node levelContainer = skillNode.Nodes.FirstOrDefault(node =>
                    string.Equals(node.Text, containerName, StringComparison.OrdinalIgnoreCase));
                if (levelContainer == null)
                {
                    continue;
                }
                var levels = levelContainer.Nodes
                    .Select(node => new
                    {
                        Node = node,
                        Parsed = int.TryParse(node.Text, out int level),
                        Level = int.TryParse(node.Text, out int value) ? value : -1
                    })
                    .Where(item => item.Parsed && item.Level >= 0)
                    .OrderBy(item => item.Level)
                    .ToArray();
                if (levels.Length == 0)
                {
                    continue;
                }

                var selected = characterLevel <= 0
                    ? levels.Last()
                    : levels.LastOrDefault(item => item.Level <= characterLevel);
                if (selected != null)
                {
                    return selected.Node;
                }
            }
            return skillNode;
        }

        private static IEnumerable<Wz_Node> EnumerateDescendants(Wz_Node node, int depth)
        {
            yield return node;
            if (depth <= 0)
            {
                yield break;
            }
            foreach (Wz_Node child in node.Nodes)
            {
                foreach (Wz_Node nested in EnumerateDescendants(child, depth - 1))
                {
                    yield return nested;
                }
            }
        }

        private static byte[] TryExtractFrame(Wz_Node node)
        {
            BitmapOrigin frame;
            try
            {
                frame = BitmapOrigin.CreateFromNode(node, PluginManager.FindWz);
            }
            catch
            {
                return null;
            }
            if (frame.Bitmap == null)
            {
                return null;
            }
            using (frame.Bitmap)
            {
                if (frame.Bitmap.Width < 8 || frame.Bitmap.Height < 8 ||
                    frame.Bitmap.Width > 1024 || frame.Bitmap.Height > 1024)
                {
                    return null;
                }
                using (var stream = new MemoryStream())
                {
                    frame.Bitmap.Save(stream, ImageFormat.Png);
                    return stream.ToArray();
                }
            }
        }

        private static int ParseSkillId(Wz_Node node)
        {
            return int.TryParse(node?.Text, out int skillId) ? skillId : 0;
        }

    }
}
