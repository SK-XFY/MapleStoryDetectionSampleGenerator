using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WzComparerR2.PluginBase;
using WzComparerR2.WzLib;

namespace MapRender.Invoker
{
    /// <summary>
    /// Extracts Item/Character iconRaw sprites that match equipment and item drops seen on the ground.
    /// </summary>
    internal sealed class DropItemOccluderGenerator
    {
        private static readonly string[] ItemDirectories =
            { "Consume", "Etc", "Install" };
        private static readonly string[] EquipmentDirectories =
        {
            "Cap", "Coat", "Longcoat", "Pants", "Shoes", "Glove", "Cape", "Weapon", "Shield", "Accessory"
        };

        private readonly ExtractedImgDataSource _extractedData;
        private readonly Random _random = new Random();

        public DropItemOccluderGenerator(ExtractedImgDataSource extractedData)
        {
            _extractedData = extractedData;
        }

        public IReadOnlyList<byte[]> Create(int count)
        {
            if (count <= 0)
            {
                return Array.Empty<byte[]>();
            }

            string[] paths = GetImagePaths()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(_ => _random.Next())
                .ToArray();
            var result = new List<byte[]>(count);
            foreach (string path in paths)
            {
                if (result.Count >= count)
                {
                    break;
                }
                Wz_Node root = PluginManager.FindWz(path);
                if (root == null)
                {
                    continue;
                }

                Wz_Node[] icons = OccluderSpriteExtractor.Descendants(root, 4)
                    .Where(node => string.Equals(node.Text, "iconRaw", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(node.Text, "icon", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(node => string.Equals(node.Text, "iconRaw", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(_ => _random.Next())
                    .Take(4)
                    .ToArray();
                foreach (Wz_Node icon in icons)
                {
                    byte[] png = OccluderSpriteExtractor.TryExtractPng(icon, 6, 128);
                    if (png != null)
                    {
                        result.Add(png);
                    }
                    if (result.Count >= count)
                    {
                        break;
                    }
                }
            }

            Console.WriteLine("Generated {0} dropped item/equipment WZ occluder(s).", result.Count);
            return result;
        }

        private IEnumerable<string> GetImagePaths()
        {
            if (_extractedData != null)
            {
                return ItemDirectories
                    .SelectMany(directory => _extractedData.EnumerateImagePaths(Path.Combine("Item", directory)))
                    .Concat(EquipmentDirectories.SelectMany(directory =>
                        _extractedData.EnumerateImagePaths(Path.Combine("Character", directory))));
            }

            var paths = new List<string>();
            Wz_Node itemRoot = PluginManager.FindWz(Wz_Type.Item);
            foreach (string directory in ItemDirectories)
            {
                Wz_Node category = itemRoot?.Nodes.FirstOrDefault(node =>
                    string.Equals(node.Text, directory, StringComparison.OrdinalIgnoreCase));
                if (category != null)
                {
                    paths.AddRange(category.Nodes
                        .Where(node => node.Text.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
                        .Select(node => $@"Item\{directory}\{node.Text}"));
                }
            }

            Wz_Node characterRoot = PluginManager.FindWz(Wz_Type.Character);
            foreach (string directory in EquipmentDirectories)
            {
                Wz_Node category = characterRoot?.Nodes.FirstOrDefault(node =>
                    string.Equals(node.Text, directory, StringComparison.OrdinalIgnoreCase));
                if (category != null)
                {
                    paths.AddRange(category.Nodes
                        .Where(node => node.Text.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
                        .Select(node => $@"Character\{directory}\{node.Text}"));
                }
            }
            return paths;
        }
    }
}
