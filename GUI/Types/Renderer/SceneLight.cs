using System;
using System.Numerics;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Utils;

namespace GUI.Types.Renderer;

class SceneLight(Scene scene) : SceneNode(scene)
{
    /// <summary>
    /// Light index to a baked lightmap.
    /// Range: 0..255 for GameLightmapVersion 1 and 0..3 for GameLightmapVersion 2.
    /// </summary>
    public int StationaryLightIndex { get; set; }

    public enum LightType
    {
        Directional,
        Point,
        Spot,
    }

    public enum EntityType
    {
        Environment,
        Omni,
        Spot,
        Omni2,
        Barn,
        Rect,
    }

    public Vector3 Color { get; set; } = Vector3.One;
    public float Brightness { get; set; } = 1.0f;
    public float FallOff { get; set; } = 1.0f;
    public LightType Type { get; set; }
    public EntityType Entity { get; set; }

    public static (bool Accepted, EntityType Type) IsAccepted(string classname)
    {
        if (!classname.StartsWith("light_", StringComparison.OrdinalIgnoreCase))
        {
            return (false, EntityType.Environment);
        }

        var accepted = Enum.TryParse(classname[6..], true, out EntityType entityType);
        return (accepted, entityType);
    }

    public Vector3 Direction { get; set; }
    public float Range { get; set; } = 512.0f;

    public static SceneLight FromEntityProperties(Scene scene, EntityType type, EntityLump.Entity entity)
    {
        var light = new SceneLight(scene)
        {
            StationaryLightIndex = Convert.ToInt32(entity.GetProperty("bakedshadowindex")?.Data ?? -1),
            Entity = type,
            Type = type switch
            {
                EntityType.Environment => LightType.Directional,
                EntityType.Omni => LightType.Point,
                EntityType.Omni2 => LightType.Point,
                EntityType.Barn => LightType.Spot,
                EntityType.Rect => LightType.Spot,
                EntityType.Spot => LightType.Spot,
                _ => throw new NotImplementedException()
            },

            Color = entity.GetProperty("color").Data switch
            {
                byte[] bytes => new Vector3(bytes[0], bytes[1], bytes[2]),
                Vector3 vec => vec,
                Vector4 vec4 => new Vector3(vec4.X, vec4.Y, vec4.Z),
                _ => throw new NotImplementedException()
            } / 255.0f,

            Brightness = entity.GetProperty("brightness")?.Data switch
            {
                object nonFloat => Convert.ToSingle(nonFloat),
                null => 1.0f
            },

            Range = Convert.ToSingle(entity.GetProperty("range")?.Data ?? 512.0f),
            FallOff = Convert.ToSingle(entity.GetProperty("skirt")?.Data ?? 0.1f),
        };

        var angles = EntityTransformHelper.GetPitchYawRoll(entity);

        light.Direction = new Vector3(
            MathF.Cos(angles.Y) * MathF.Cos(angles.X),
            MathF.Sin(angles.Y) * MathF.Cos(angles.X),
            MathF.Sin(angles.X)
        );

        return light;
    }

    public override void Update(Scene.UpdateContext context)
    {
        //throw new NotImplementedException();
    }

    public override void Render(Scene.RenderContext context)
    {
        //throw new NotImplementedException();
    }
}
