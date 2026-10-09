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
    internal sealed class MapObjectOccluderGenerator
    {
        private readonly Wz_Image _mapImage;
        private readonly Random _random = new Random();

        public MapObjectOccluderGenerator(Wz_Image mapImage)
        {
            _mapImage = mapImage ?? throw new ArgumentNullException(nameof(mapImage));
        }

        public IReadOnlyList<byte[]> Create(int count)
        {
            if (count <= 0)
            {
                return Array.Empty<byte[]>();
            }
            _mapImage.TryExtract(out Exception error);
            if (error != null)
            {
                throw error;
            }

            string[] paths = GetObjectPaths()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(_ => _random.Next())
                .ToArray();
            var result = new List<byte[]>(Math.Min(count, paths.Length));
            foreach (string path in paths)
            {
                if (result.Count >= count)
                {
                    break;
                }
                try
                {
                    byte[] image = ExtractFirstFrame(path);
                    if (image != null)
                    {
                        result.Add(image);
                    }
                }
                catch
                {
                    // A map may contain unsupported spine/video objects. Skip those resources.
                }
            }

            Console.WriteLine("Generated {0} Map WZ object occluder(s).", result.Count);
            return result;
        }

        private IEnumerable<string> GetObjectPaths()
        {
            foreach (Wz_Node layer in _mapImage.Node.Nodes)
            {
                if (!int.TryParse(layer.Text, out _))
                {
                    continue;
                }
                Wz_Node objects = layer.Nodes["obj"];
                if (objects == null)
                {
                    continue;
                }
                foreach (Wz_Node item in objects.Nodes)
                {
                    string objectSet = item.Nodes["oS"].GetValueEx<string>(null);
                    string level0 = item.Nodes["l0"].GetValueEx<string>(null);
                    string level1 = item.Nodes["l1"].GetValueEx<string>(null);
                    string level2 = item.Nodes["l2"].GetValueEx<string>(null);
                    if (!string.IsNullOrWhiteSpace(objectSet) &&
                        level0 != null && level1 != null && level2 != null)
                    {
                        yield return $@"Map\Obj\{objectSet}.img\{level0}\{level1}\{level2}";
                    }
                }
            }
        }

        private static byte[] ExtractFirstFrame(string path)
        {
            Wz_Node resource = PluginManager.FindWz(path);
            if (resource == null)
            {
                return null;
            }

            IEnumerable<Wz_Node> candidates = new[] { resource }
                .Concat(resource.Nodes)
                .Concat(resource.Nodes.SelectMany(node => node.Nodes));
            foreach (Wz_Node candidate in candidates)
            {
                BitmapOrigin frame;
                try
                {
                    frame = BitmapOrigin.CreateFromNode(candidate, PluginManager.FindWz);
                }
                catch
                {
                    continue;
                }
                if (frame.Bitmap == null || frame.Bitmap.Width < 4 || frame.Bitmap.Height < 4)
                {
                    frame.Bitmap?.Dispose();
                    continue;
                }
                if (frame.Bitmap.Width > 1024 || frame.Bitmap.Height > 1024)
                {
                    frame.Bitmap.Dispose();
                    continue;
                }

                using (frame.Bitmap)
                using (var stream = new MemoryStream())
                {
                    frame.Bitmap.Save(stream, ImageFormat.Png);
                    return stream.ToArray();
                }
            }
            return null;
        }
    }
}
