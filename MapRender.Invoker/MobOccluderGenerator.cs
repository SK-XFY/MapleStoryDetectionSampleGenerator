using System;
using System.Collections.Generic;
using System.Linq;
using WzComparerR2.PluginBase;
using WzComparerR2.WzLib;

namespace MapRender.Invoker
{
    /// <summary>
    /// Extracts transparent, alive Mob animation frames from the current map. These
    /// frames are used to create labelled Mob-on-Mob overlap without copying map pixels.
    /// </summary>
    internal sealed class MobOccluderGenerator
    {
        private static readonly string[] PreferredActions =
            { "stand", "move", "fly", "jump", "attack1", "attack2", "hit1" };

        private readonly Wz_Image _mapImage;
        private readonly Random _random = new Random();

        public MobOccluderGenerator(Wz_Image mapImage)
        {
            _mapImage = mapImage;
        }

        public IReadOnlyList<MobOccluderAsset> Create(int count)
        {
            if (count <= 0)
            {
                return Array.Empty<MobOccluderAsset>();
            }

            int[] mobIds = GetCurrentMapMobIds()
                .Distinct()
                .OrderBy(_ => _random.Next())
                .ToArray();
            var result = new List<MobOccluderAsset>(count);
            foreach (int mobId in mobIds)
            {
                foreach (byte[] frame in ExtractMobFrames(mobId))
                {
                    result.Add(new MobOccluderAsset(mobId, frame));
                    if (result.Count >= count)
                    {
                        break;
                    }
                }
                if (result.Count >= count)
                {
                    break;
                }
            }

            Console.WriteLine("Generated {0} labelled Mob WZ overlap occluder(s).", result.Count);
            return result;
        }

        private IEnumerable<int> GetCurrentMapMobIds()
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
                if (!string.Equals(entry.Nodes["type"].GetValueEx<string>(null), "m",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                int id = entry.Nodes["id"].GetValueEx<int>(0);
                if (id > 0)
                {
                    yield return id;
                }
            }
        }

        private IEnumerable<byte[]> ExtractMobFrames(int mobId)
        {
            Wz_Node root = PluginManager.FindWz($@"Mob\{mobId:D7}.img");
            if (root == null)
            {
                yield break;
            }

            int linkedId = root.FindNodeByPath(@"info\link").GetValueEx<int>(-1);
            if (linkedId > 0)
            {
                Wz_Node linked = PluginManager.FindWz($@"Mob\{linkedId:D7}.img");
                if (linked != null)
                {
                    root = linked;
                }
            }

            IEnumerable<Wz_Node> actions = root.Nodes
                .Where(node => !string.Equals(node.Text, "info", StringComparison.OrdinalIgnoreCase) &&
                               !node.Text.StartsWith("die", StringComparison.OrdinalIgnoreCase) &&
                               !node.Text.StartsWith("regen", StringComparison.OrdinalIgnoreCase))
                .OrderBy(node => ActionPriority(node.Text));
            foreach (Wz_Node action in actions)
            {
                foreach (Wz_Node candidate in OccluderSpriteExtractor.Descendants(action, 3)
                             .OrderBy(_ => _random.Next()))
                {
                    byte[] png = OccluderSpriteExtractor.TryExtractPng(candidate, 8, 512);
                    if (png != null)
                    {
                        yield return png;
                        break;
                    }
                }
            }
        }

        private static int ActionPriority(string action)
        {
            int index = Array.FindIndex(PreferredActions,
                item => string.Equals(item, action, StringComparison.OrdinalIgnoreCase));
            return index < 0 ? PreferredActions.Length : index;
        }
    }
}
