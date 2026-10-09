using System;

namespace MapRender.Invoker
{
    public sealed class SkillEffectAsset
    {
        public SkillEffectAsset(int skillId, byte[] pngBytes, string sourcePath)
        {
            SkillId = skillId;
            PngBytes = pngBytes ?? throw new ArgumentNullException(nameof(pngBytes));
            SourcePath = sourcePath ?? string.Empty;
        }

        public int SkillId { get; }
        public byte[] PngBytes { get; }
        public string SourcePath { get; }
    }
}
