using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using WzComparerR2.WzLib;

namespace MapRender.Invoker
{
    /// <summary>
    /// Provides lazy access to a MapleStory Data directory containing standalone
    /// binary .img files instead of packed .wz files.
    /// </summary>
    public sealed class ExtractedImgDataSource : IDisposable
    {
        private readonly string _dataRoot;
        private readonly Wz_Structure _structure;
        private readonly Wz_Node _root;
        private readonly Dictionary<string, Wz_Node> _images;
        private readonly object _syncRoot = new object();

        public ExtractedImgDataSource(string dataRoot, Encoding encoding)
        {
            _dataRoot = Path.GetFullPath(dataRoot);
            _structure = new Wz_Structure
            {
                TextEncoding = encoding,
                ImgCheckDisabled = true
            };
            _root = new Wz_Node("Data");
            _structure.WzNode = _root;
            _images = new Dictionary<string, Wz_Node>(StringComparer.OrdinalIgnoreCase);
        }

        public Wz_Image LoadMapImage(int mapId)
        {
            string path = $@"Map\Map\Map{mapId / 100000000}\{mapId:D9}.img";
            Wz_Node node = Resolve(path);
            Wz_Image image = node?.GetValueEx<Wz_Image>(null);
            if (image == null)
            {
                throw new FileNotFoundException($"Cannot find extracted map IMG: {path}", path);
            }
            return image;
        }

        public Wz_Node Resolve(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath))
            {
                return null;
            }

            string[] parts = fullPath
                .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 &&
                string.Equals(parts[0], _root.Text, StringComparison.OrdinalIgnoreCase))
            {
                parts = parts.Skip(1).ToArray();
            }
            int imagePart = Array.FindIndex(parts,
                part => part.EndsWith(".img", StringComparison.OrdinalIgnoreCase));
            if (imagePart < 0)
            {
                return null;
            }

            string relativeImagePath = Path.Combine(parts.Take(imagePart + 1).ToArray());
            Wz_Node ownerNode = GetOrLoadImage(relativeImagePath);
            if (ownerNode == null)
            {
                return ownerNode;
            }

            Wz_Image image = ownerNode.GetValueEx<Wz_Image>(null);
            if (image == null)
            {
                throw new InvalidDataException(
                    $"IMG node '{relativeImagePath}' does not contain an image.");
            }
            if (!image.TryExtract(out Exception error))
            {
                throw new InvalidDataException(
                    $"Failed to extract IMG '{relativeImagePath}'.", error);
            }

            if (imagePart == parts.Length - 1)
            {
                return image.Node;
            }

            return image.Node.FindNodeByPath(
                extractImage: true,
                ignoreCase: true,
                fullPath: parts.Skip(imagePart + 1).ToArray());
        }

        public IReadOnlyList<string> EnumerateImagePaths(string relativeDirectory)
        {
            string[] candidates =
            {
                Path.Combine(_dataRoot, relativeDirectory),
                Path.Combine(_dataRoot, "Base", relativeDirectory)
            };
            string directory = candidates.FirstOrDefault(Directory.Exists);
            if (directory == null)
            {
                return Array.Empty<string>();
            }

            string relativeRoot = string.Equals(directory, candidates[0], StringComparison.OrdinalIgnoreCase)
                ? _dataRoot
                : Path.Combine(_dataRoot, "Base");
            return Directory.EnumerateFiles(directory, "*.img", SearchOption.TopDirectoryOnly)
                .Select(path => Path.GetRelativePath(relativeRoot, path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private Wz_Node GetOrLoadImage(string relativePath)
        {
            lock (_syncRoot)
            {
                if (_images.TryGetValue(relativePath, out Wz_Node cached))
                {
                    return cached;
                }

                string imagePath = FindImagePath(relativePath);
                if (imagePath == null)
                {
                    return null;
                }

                var file = new Wz_File(imagePath, _structure)
                {
                    TextEncoding = _structure.TextEncoding,
                    Type = ParseWzType(relativePath)
                };
                var ownerNode = new Wz_Node(Path.GetFileName(imagePath));
                var image = new Wz_Image(
                    ownerNode.Text,
                    checked((int)file.FileStream.Length),
                    0,
                    0,
                    0,
                    file)
                {
                    OwnerNode = ownerNode,
                    Offset = 0
                };

                ownerNode.Value = image;
                file.Node = ownerNode;
                GetDirectoryNode(relativePath).Nodes.Add(ownerNode);
                _structure.wz_files.Add(file);
                _images.Add(relativePath, ownerNode);
                return ownerNode;
            }
        }

        private Wz_Node GetDirectoryNode(string relativeImagePath)
        {
            string[] directories = relativeImagePath
                .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                .SkipLast(1)
                .ToArray();
            Wz_Node current = _root;
            foreach (string directory in directories)
            {
                Wz_Node child = current.Nodes.FirstOrDefault(node =>
                    string.Equals(node.Text, directory, StringComparison.OrdinalIgnoreCase));
                if (child == null)
                {
                    child = new Wz_Node(directory);
                    current.Nodes.Add(child);
                }
                current = child;
            }
            return current;
        }

        private string FindImagePath(string relativePath)
        {
            string directPath = Path.Combine(_dataRoot, relativePath);
            if (File.Exists(directPath))
            {
                return directPath;
            }

            string basePath = Path.Combine(_dataRoot, "Base", relativePath);
            return File.Exists(basePath) ? basePath : null;
        }

        private static Wz_Type ParseWzType(string relativePath)
        {
            string typeName = relativePath.Split(
                new[] { '/', '\\' },
                StringSplitOptions.RemoveEmptyEntries)[0];
            return Enum.TryParse(typeName, true, out Wz_Type type)
                ? type
                : Wz_Type.Unknown;
        }

        public void Dispose()
        {
            _structure.Clear();
        }
    }
}
