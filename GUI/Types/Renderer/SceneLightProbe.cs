using System.Buffers;
using System.Runtime.InteropServices;
using GUI.Types.Renderer.UniformBuffers;
using OpenTK.Graphics.OpenGL;

namespace GUI.Types.Renderer;

class SceneLightProbe : SceneNode
{
    public int HandShake { get; set; }

    /// <remarks>
    /// Used in lighting version 6 and 8.x
    /// </remarks>
    public RenderTexture Irradiance { get; set; }

    /// <remarks>
    /// Used in lighting version 8.1
    /// </remarks>
    public RenderTexture DirectLightIndices { get; set; }

    /// <remarks>
    /// Used in lighting version 8.1
    /// </remarks>
    public RenderTexture DirectLightScalars { get; set; }

    /// <remarks>
    /// Used in lighting version 8.2
    /// </remarks>
    public RenderTexture DirectLightShadows { get; set; }

    /// <remarks>
    /// Used in lighting version 8.2
    /// </remarks>
    public Vector3 AtlasSize { get; set; }

    /// <remarks>
    /// Used in lighting version 8.2
    /// </remarks>
    public Vector3 AtlasOffset { get; set; }

    /// <summary>
    /// If multiple volumes contain an object, the highest priority volume takes precedence.
    /// </summary>
    public int IndoorOutdoorLevel { get; init; }

    public SceneLightProbe(Scene scene, AABB bounds) : base(scene)
    {
        LocalBoundingBox = bounds;
    }

    public override void Render(Scene.RenderContext context)
    {
    }

    public override void Update(Scene.UpdateContext context)
    {
    }

    public void SetGpuProbeData(bool isProbeAtlas, ref LightingConstants.LPVData lpv)
    {
        Matrix4x4.Invert(Transform, out var worldToLocal);

        var normalizedScale = Vector3.One / LocalBoundingBox.Size;
        lpv.WorldToLocalNormalized = (Matrix4x4.CreateScale(normalizedScale) * worldToLocal) with
        {
            Translation = (worldToLocal.Translation - LocalBoundingBox.Min) * normalizedScale,
        };

        if (isProbeAtlas)
        {
            var half = Vector3.One * 0.5f;
            var depthDivide = Vector3.One with { Z = 1f / 6 };

            var textureDims = new Vector3(DirectLightShadows.Width, DirectLightShadows.Height, DirectLightShadows.Depth);
            var atlasDims = AtlasOffset + AtlasSize;

            var borderMin = half * depthDivide / textureDims;
            var borderMax = (textureDims - half) * depthDivide / textureDims;
            var scale = AtlasSize / textureDims;
            var offset = AtlasOffset / textureDims;

            lpv.Min = new Vector4(borderMin, 0);
            lpv.Max = new Vector4(borderMax, 0);
            lpv.Scale = new Vector4(scale, 0);
            lpv.Offset = new Vector4(offset, 0);
        }
        else
        {
            lpv.Min = Vector4.Zero; // Layer0TextureMin
            lpv.Max = Vector4.Zero; // Layer0TextureMax
        }
    }
}
