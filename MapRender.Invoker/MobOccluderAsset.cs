namespace MapRender.Invoker
{
    public sealed class MobOccluderAsset
    {
        public MobOccluderAsset(int mobId, byte[] imageBytes)
        {
            MobId = mobId;
            ImageBytes = imageBytes;
        }

        public int MobId { get; }

        public byte[] ImageBytes { get; }
    }
}
