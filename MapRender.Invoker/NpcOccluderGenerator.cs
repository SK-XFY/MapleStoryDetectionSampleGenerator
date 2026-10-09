using System;
using System.Collections.Generic;
using System.Linq;
using WzComparerR2.PluginBase;
using WzComparerR2.WzLib;

namespace MapRender.Invoker
{
    /// <summary>
    /// Extracts real NPC animation frames. NPCs from the current map are preferred;
    /// random NPCs fill the remaining cache so maps without NPCs still receive this augmentation.
    /// </summary>
    internal sealed class NpcOccluderGenerator
    {
        private static readonly string[] PreferredActions =
            { "stand", "stand0", "stand1", "default", "move", "walk" };

        private readonly ExtractedImgDataSource _extractedData;
        private readonly Wz_Image _mapImage;
        private readonly Random _random = new Random();

        public NpcOccluderGenerator(ExtractedImgDataSource extractedData, Wz_Image mapImage)
        {
            _extractedData = extractedData;
            _mapImage = mapImage;
        }

        public IReadOnlyList<byte[]> Create(int count)
        {
            if (count <= 0)
            {
                return Array.Empty<byte[]>();
            }

            string[] mapPaths = GetCurrentMapNpcIds()
                .Select(id => $@"Npc\{id:D7}.img")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(_ => _random.Next())
                .ToArray();
            string[] fallbackPaths = GetAllNpcPaths()
                .Except(mapPaths, StringComparer.OrdinalIgnoreCase)
                .OrderBy(_ => _random.Next())
                .ToArray();

            var result = new List<byte[]>(count);
            foreach (string path in mapPaths.Concat(fallbackPaths))
            {
                if (result.Count >= count)
                {
                    break;
                }
                byte[] image = ExtractNpcFrame(path);
                if (image != null)
                {
                    result.Add(image);
                }
            }

            Console.WriteLine("Generated {0} NPC WZ occluder(s).", result.Count);
            return result;
        }

        private IEnumerable<int> GetCurrentMapNpcIds()
        {
            if (_mapImage == null)
            {
                yield break;
            }
            _mapImage.TryExtract(out Exception error);
            if (error != null)
            {
                yield break;
            }

            Wz_Node life = _mapImage.Node.Nodes["life"];
            if (life == null)
            {
                yield break;
            }
            bool isCategory = life.Nodes["isCategory"].GetValueEx<int>(0) != 0;
            IEnumerable<Wz_Node> entries = isCategory
                ? life.Nodes.Where(node => node.Text != "isCategory").SelectMany(node => node.Nodes)
                : life.Nodes.Where(node => node.Text != "isCategory");
            foreach (Wz_Node entry in entries)
            {
                if (string.Equals(entry.Nodes["type"].GetValueEx<string>(null), "n",
                        StringComparison.OrdinalIgnoreCase))
                {
                    int id = entry.Nodes["id"].GetValueEx<int>(0);
                    if (id >= 0)
                    {
                        yield return id;
                    }
                }
            }
        }

        private IEnumerable<string> GetAllNpcPaths()
        {
            if (_extractedData != null)
            {
                return _extractedData.EnumerateImagePaths("Npc");
            }

            Wz_Node root = PluginManager.FindWz(Wz_Type.Npc);
            return root?.Nodes
                       .Where(node => node.Text.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
                       .Select(node => $@"Npc\{node.Text}")
                   ?? Enumerable.Empty<string>();
        }

        private static byte[] ExtractNpcFrame(string path)
        {
            Wz_Node root = PluginManager.FindWz(path);
            if (root == null)
            {
                return null;
            }

            int link = root.FindNodeByPath(@"info\link").GetValueEx<int>(-1);
            if (link >= 0)
            {
                Wz_Node linked = PluginManager.FindWz($@"Npc\{link:D7}.img");
                if (linked != null)
                {
                    root = linked;
                }
            }

            IEnumerable<Wz_Node> actions = root.Nodes
                .Where(node => !string.Equals(node.Text, "info", StringComparison.OrdinalIgnoreCase) &&
                               !node.Text.StartsWith("condition", StringComparison.OrdinalIgnoreCase))
                .OrderBy(node => ActionPriority(node.Text));
            foreach (Wz_Node action in actions)
            {
                foreach (Wz_Node candidate in OccluderSpriteExtractor.Descendants(action, 3))
                {
                    byte[] png = OccluderSpriteExtractor.TryExtractPng(candidate, 8, 512);
                    if (png != null)
                    {
                        return png;
                    }
                }
            }
            return null;
        }

        private static int ActionPriority(string action)
        {
            int index = Array.FindIndex(PreferredActions,
                item => string.Equals(item, action, StringComparison.OrdinalIgnoreCase));
            return index < 0 ? PreferredActions.Length : index;
        }
    }
}
