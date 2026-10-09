using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using WzComparerR2.MapRender.Patches;
using WzComparerR2.MapRender.Patches2;

namespace MapRender.Invoker
{
    //Object detection interests item
    public class TargetItem : SceneItem
    {
        public int Id { get; set; }

        public int X { get; set; }

        public int Y { get; set; }

        public ObjectClass Type { get; set; }

        public int Width { get; set; }

        public int Height { get; set; }

        /// <summary>
        /// Current Mob animation state at capture time. Non-Mob targets keep None.
        /// </summary>
        public MobCombatState CombatState { get; set; }

        public string AnimationName { get; set; }

        public int AnimationFrame { get; set; } = -1;

        public int EffectSkillId { get; set; }

        /// <summary>
        /// Estimated visible fraction after synthetic foreground/effect overlays.
        /// </summary>
        public double VisibleFraction { get; set; } = 1.0;

        /// <summary>
        /// Synthetic foreground source used by preview diagnostics (PLAYER/NPC/DROP/MAP).
        /// Dataset writers intentionally ignore this field.
        /// </summary>
        public string OcclusionSource { get; set; }

        /// <summary>
        /// True when the renderer intentionally captured the Mob's translucent spawn moment.
        /// It remains an Alive/Mob label; this flag is for preview diagnostics only.
        /// </summary>
        public bool IsSpawnTransparent { get; set; }

        public double RenderOpacity { get; set; } = 1.0;

        /// <summary>
        /// False keeps the item available to the state-crop writer while excluding it
        /// from object-detection annotations.
        /// </summary>
        public bool DetectionEnabled { get; set; } = true;

        public TargetItem() { }

        public TargetItem(SceneItem item)
        {
            Name = item.Name;
            Index = item.Index;
            Tags = item.Tags;
        }

        public TargetItem(SceneItem item, Rectangle rectangle) : this(item)
        {
            X = rectangle.X;
            Y = rectangle.Y;
            Width = rectangle.Width;
            Height = rectangle.Height;
        }

        public TargetItem(SceneItem item, Rectangle rectangle, RenderObjectType type) : this(item, rectangle)
        {
            Type = RenderTypeToObjectClass(type);
        }

        public static ObjectClass RenderTypeToObjectClass(RenderObjectType type)
        {
            switch (type)
            {
                case RenderObjectType.Mob:
                    return ObjectClass.Mob;
                case RenderObjectType.Foothold:
                    return ObjectClass.Foothold;
                case RenderObjectType.Npc:
                    return ObjectClass.Npc;
                case RenderObjectType.LadderRope:
                    return ObjectClass.LadderRope;
                case RenderObjectType.Portal:
                    return ObjectClass.CrossMapPortal; //TODO: Classify portal
            }
            return ObjectClass.Unknown;
        }

    }

    public enum MobCombatState
    {
        None = 0,
        Alive,
        Hit,
        Dying,
        Unknown
    }

    public enum MobRenderState
    {
        Automatic = 0,
        Alive,
        Hit,
        Dying,
        Spawning
    }
}
