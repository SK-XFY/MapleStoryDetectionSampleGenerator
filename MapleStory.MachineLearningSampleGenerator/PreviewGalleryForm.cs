using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace MapleStory.MachineLearningSampleGenerator
{
    internal sealed class PreviewGalleryForm : Form
    {
        private readonly PictureBox _mainImage;
        private readonly Label _caption;
        private readonly List<Image> _loadedImages = new List<Image>();

        public PreviewGalleryForm(IReadOnlyList<string> imagePaths, string previewDirectory)
        {
            Text = "训练样本预览";
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1180, 820);
            MinimumSize = new Size(820, 600);
            Font = new Font("Microsoft YaHei UI", 9F);

            var header = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 48,
                Padding = new Padding(10, 8, 10, 4),
                FlowDirection = FlowDirection.LeftToRight
            };
            var openFolder = new Button { Text = "打开预览文件夹", Width = 130, Height = 30 };
            openFolder.Click += (_, __) => Process.Start(new ProcessStartInfo
            {
                FileName = previewDirectory,
                UseShellExecute = true
            });
            _caption = new Label
            {
                AutoSize = true,
                Padding = new Padding(12, 7, 0, 0),
                ForeColor = Color.DimGray
            };
            header.Controls.Add(openFolder);
            header.Controls.Add(_caption);

            _mainImage = new PictureBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(28, 28, 28),
                SizeMode = PictureBoxSizeMode.Zoom
            };

            var thumbnails = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 150,
                AutoScroll = true,
                WrapContents = false,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(8)
            };

            Controls.Add(_mainImage);
            Controls.Add(thumbnails);
            Controls.Add(header);

            foreach (string path in imagePaths.Where(File.Exists))
            {
                Image image = LoadUnlocked(path);
                _loadedImages.Add(image);
                var thumb = new PictureBox
                {
                    Image = image,
                    Width = 190,
                    Height = 118,
                    SizeMode = PictureBoxSizeMode.Zoom,
                    BackColor = Color.Black,
                    BorderStyle = BorderStyle.FixedSingle,
                    Cursor = Cursors.Hand,
                    Tag = path,
                    Margin = new Padding(5)
                };
                thumb.Click += (_, __) => SelectImage(thumb);
                thumbnails.Controls.Add(thumb);
            }

            if (thumbnails.Controls.Count > 0)
            {
                SelectImage((PictureBox)thumbnails.Controls[0]);
            }
            else
            {
                _caption.Text = "没有生成可预览图片。";
            }
        }

        private void SelectImage(PictureBox pictureBox)
        {
            _mainImage.Image = pictureBox.Image;
            string path = (string)pictureBox.Tag;
            _caption.Text = Path.GetFileName(path);
        }

        private static Image LoadUnlocked(string path)
        {
            using (var source = Image.FromFile(path))
            {
                return new Bitmap(source);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _mainImage.Image = null;
                foreach (Image image in _loadedImages)
                {
                    image.Dispose();
                }
                _loadedImages.Clear();
            }
            base.Dispose(disposing);
        }
    }
}
