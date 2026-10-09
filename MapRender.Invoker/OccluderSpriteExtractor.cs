using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.IO;
using WzComparerR2;
using WzComparerR2.PluginBase;
using WzComparerR2.WzLib;

namespace MapRender.Invoker
{
    internal static class OccluderSpriteExtractor
    {
        public static IEnumerable<Wz_Node> Descendants(Wz_Node root, int maximumDepth)
        {
            if (root == null)
            {
                yield break;
            }

            var queue = new Queue<(Wz_Node Node, int Depth)>();
            queue.Enqueue((root, 0));
            while (queue.Count > 0)
            {
                (Wz_Node node, int depth) = queue.Dequeue();
                yield return node;
                if (depth >= maximumDepth)
                {
                    continue;
                }
                foreach (Wz_Node child in node.Nodes)
                {
                    queue.Enqueue((child, depth + 1));
                }
            }
        }

        public static byte[] TryExtractPng(
            Wz_Node node,
            int minimumDimension = 4,
            int maximumDimension = 1024)
        {
            if (node == null)
            {
                return null;
            }

            BitmapOrigin frame;
            try
            {
                frame = BitmapOrigin.CreateFromNode(node, PluginManager.FindWz);
            }
            catch
            {
                return null;
            }
            if (frame.Bitmap == null ||
                frame.Bitmap.Width < minimumDimension || frame.Bitmap.Height < minimumDimension ||
                frame.Bitmap.Width > maximumDimension || frame.Bitmap.Height > maximumDimension)
            {
                frame.Bitmap?.Dispose();
                return null;
            }

            using (frame.Bitmap)
            using (var stream = new MemoryStream())
            {
                frame.Bitmap.Save(stream, ImageFormat.Png);
                return stream.ToArray();
            }
        }
    }
}
