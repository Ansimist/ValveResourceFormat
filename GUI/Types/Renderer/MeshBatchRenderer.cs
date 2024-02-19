using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using GUI.Utils;
using OpenTK.Graphics.OpenGL;

namespace GUI.Types.Renderer
{
    static class MeshBatchRenderer
    {
        public struct Request
        {
            public Matrix4x4 Transform;
            public RenderableMesh Mesh;
            public DrawCall Call;
            public float DistanceFromCamera;
            public int RenderOrder;
            public SceneNode Node;
        }

        public static int ComparePipeline(Request a, Request b)
        {
            if (a.Call.Material.Shader.Program == b.Call.Material.Shader.Program)
            {
                return a.Call.Material.SortId - b.Call.Material.SortId;
            }

            return a.Call.Material.Shader.Program - b.Call.Material.Shader.Program;
        }

        public static int CompareRenderOrderThenPipeline(Request a, Request b)
        {
            if (a.RenderOrder == b.RenderOrder)
            {
                return ComparePipeline(a, b);
            }

            return a.RenderOrder - b.RenderOrder;
        }

        public static int CompareCameraDistance_BackToFront(Request a, Request b)
        {
            return -a.DistanceFromCamera.CompareTo(b.DistanceFromCamera);
        }

        public static int CompareCameraDistance_FrontToBack(Request a, Request b)
        {
            return a.DistanceFromCamera.CompareTo(b.DistanceFromCamera);
        }

        public static int CompareVaoThenDistance(Request a, Request b)
        {
            var vaoCompare = a.Call.VertexArrayObject.CompareTo(b.Call.VertexArrayObject);
            if (vaoCompare == 0)
            {
                return CompareCameraDistance_FrontToBack(a, b);
            }

            return vaoCompare;
        }

        public static int CompareAABBSize(Request a, Request b)
        {
            var aSize = MathF.Max(a.Node.BoundingBox.Size.X, MathF.Max(a.Node.BoundingBox.Size.Y, a.Node.BoundingBox.Size.Z));
            var bSize = MathF.Max(b.Node.BoundingBox.Size.X, MathF.Max(b.Node.BoundingBox.Size.Y, b.Node.BoundingBox.Size.Z));
            return aSize.CompareTo(bSize);
        }

        public static void Render(List<Request> requestsList, Scene.RenderContext context)
        {
            var requests = CollectionsMarshal.AsSpan(requestsList);

            DrawBatch(requests, context);
        }

        private ref struct Uniforms
        {
            public int Animated = -1;
            public int AnimationTexture = -1;
            public int EnvmapTexture = -1;
            public bool UseLightProbeLighting;
            public int LPVIrradianceTexture = -1;
            public int LPVIndicesTexture = -1;
            public int LPVScalarsTexture = -1;
            public int LPVShadowsTexture = -1;
            public int Transform = -1;
            public int Tint = -1;
            public int ObjectId = -1;
            public int MeshId = -1;
            public int ShaderId = -1;
            public int ShaderProgramId = -1;
            public int MorphCompositeTexture = -1;
            public int MorphCompositeTextureSize = -1;
            public int MorphVertexIdOffset = -1;

            public Uniforms() { }
        }

        private ref struct Config
        {
            public bool NeedsCubemapBinding;
            public int LightmapGameVersionNumber;
            public Scene.LightProbeType LightProbeType;
        }

        private static readonly Queue<int> instanceBoundTextures = new(capacity: 4);

        private static void SetInstanceTexture(Shader shader, ReservedTextureSlots slot, int location, RenderTexture texture)
        {
            var slotIndex = (int)slot;
            instanceBoundTextures.Enqueue(slotIndex);
            shader.SetTexture(slotIndex, location, texture);
        }

        private static void UnbindInstanceTextures()
        {
            while (instanceBoundTextures.TryDequeue(out var slot))
            {
                GL.BindTextureUnit(slot, 0);
            }
        }

        public static void DrawBatch(ReadOnlySpan<Request> requests, Scene.RenderContext context)
        {
            var vao = -1;
            Shader shader = null;
            RenderMaterial material = null;
            Uniforms uniforms = new();
            Config config = new()
            {
                NeedsCubemapBinding = context.Scene.LightingInfo.CubemapType == Scene.CubemapType.IndividualCubemaps,
                LightmapGameVersionNumber = context.Scene.LightingInfo.LightmapGameVersionNumber,
                LightProbeType = context.Scene.LightingInfo.LightProbeType,
            };

            foreach (var request in requests)
            {
                if (vao != request.Call.VertexArrayObject)
                {
                    vao = request.Call.VertexArrayObject;
                    GL.BindVertexArray(vao);
                }

                if (material != request.Call.Material)
                {
                    material?.PostRender();

                    var requestShader = context.ReplacementShader ?? request.Call.Material.Shader;

                    // If the material did not change, shader could not have changed
                    if (shader != requestShader)
                    {
                        shader = requestShader;
                        uniforms = new Uniforms
                        {
                            Animated = shader.GetUniformLocation("bAnimated"),
                            AnimationTexture = shader.GetUniformLocation("animationTexture"),
                            Transform = shader.GetUniformLocation("transform"),
                            Tint = shader.GetUniformLocation("vTint"),
                        };

                        if (shader.Parameters.ContainsKey("SCENE_CUBEMAP_TYPE"))
                        {
                            uniforms.EnvmapTexture = shader.GetUniformLocation("g_tEnvironmentMap");
                        }

                        if (shader.Parameters.ContainsKey("F_MORPH_SUPPORTED"))
                        {
                            uniforms.MorphCompositeTexture = shader.GetUniformLocation("morphCompositeTexture");
                            uniforms.MorphCompositeTextureSize = shader.GetUniformLocation("morphCompositeTextureSize");
                            uniforms.MorphVertexIdOffset = shader.GetUniformLocation("morphVertexIdOffset");
                        }

                        if (shader.Parameters.ContainsKey("D_BAKED_LIGHTING_FROM_PROBE"))
                        {
                            uniforms.UseLightProbeLighting = true;
                            uniforms.LPVIrradianceTexture = shader.GetUniformLocation("g_tLPV_Irradiance");
                            uniforms.LPVIndicesTexture = shader.GetUniformLocation("g_tLPV_Indices");
                            uniforms.LPVScalarsTexture = shader.GetUniformLocation("g_tLPV_Scalars");
                            uniforms.LPVShadowsTexture = shader.GetUniformLocation("g_tLPV_Shadows");
                        }

                        if (shader.Name == "vrf.picking")
                        {
                            uniforms.ObjectId = shader.GetUniformLocation("sceneObjectId");
                            uniforms.MeshId = shader.GetUniformLocation("meshId");
                            uniforms.ShaderId = shader.GetUniformLocation("shaderId");
                            uniforms.ShaderProgramId = shader.GetUniformLocation("shaderProgramId");
                        }

                        GL.UseProgram(shader.Program);

                        foreach (var (slot, name, texture) in context.View.Textures)
                        {
                            shader.SetTexture((int)slot, name, texture);
                        }

                        context.Scene.LightingInfo.SetLightmapTextures(shader);
                    }

                    material = request.Call.Material;
                    material.Render(shader);
                }

                Draw(shader, ref uniforms, ref config, request);
            }

            if (vao > -1)
            {
                material.PostRender();
                GL.BindVertexArray(0);
                GL.UseProgram(0);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Draw(Shader shader, ref Uniforms uniforms, ref Config config, Request request)
        {
            var transformTk = request.Transform.ToOpenTK();
            GL.ProgramUniformMatrix4(shader.Program, uniforms.Transform, false, ref transformTk);

            if (uniforms.ObjectId != -1)
            {
                GL.ProgramUniform1((uint)shader.Program, uniforms.ObjectId, request.Node.Id);
                GL.ProgramUniform1((uint)shader.Program, uniforms.MeshId, (uint)request.Mesh.MeshIndex);
                GL.ProgramUniform1((uint)shader.Program, uniforms.ShaderId, (uint)request.Call.Material.Shader.NameHash);
                GL.ProgramUniform1((uint)shader.Program, uniforms.ShaderProgramId, (uint)request.Call.Material.Shader.Program);
            }

            if (config.NeedsCubemapBinding && request.Node.EnvMap != null)
            {
                var envmap = request.Node.EnvMap.EnvMapTexture;
                SetInstanceTexture(shader, ReservedTextureSlots.EnvironmentMap, uniforms.EnvmapTexture, envmap);
            }

            if (config.LightProbeType != Scene.LightProbeType.ProbeAtlas && uniforms.UseLightProbeLighting
                && request.Node.LightProbeBinding is { } lightProbe)
            {
                SetInstanceTexture(shader, ReservedTextureSlots.Probe1, uniforms.LPVIrradianceTexture, lightProbe.Irradiance);

                if (config.LightmapGameVersionNumber == 1)
                {
                    SetInstanceTexture(shader, ReservedTextureSlots.Probe2, uniforms.LPVIndicesTexture, lightProbe.DirectLightIndices);
                    SetInstanceTexture(shader, ReservedTextureSlots.Probe3, uniforms.LPVScalarsTexture, lightProbe.DirectLightScalars);
                }
                else if (request.Node.Scene.LightingInfo.LightmapGameVersionNumber == 2)
                {
                    SetInstanceTexture(shader, ReservedTextureSlots.Probe2, uniforms.LPVShadowsTexture, lightProbe.DirectLightShadows);
                }
            }

            if (uniforms.Animated != -1)
            {
                var bAnimated = request.Mesh.AnimationTexture != null;
                GL.ProgramUniform1((uint)shader.Program, uniforms.Animated, bAnimated ? 1u : 0u);

                if (bAnimated && uniforms.AnimationTexture != -1)
                {
                    SetInstanceTexture(shader, ReservedTextureSlots.AnimationTexture, uniforms.AnimationTexture, request.Mesh.AnimationTexture);
                }
            }

            if (uniforms.MorphVertexIdOffset != -1)
            {
                var morphComposite = request.Mesh.FlexStateManager?.MorphComposite;
                if (morphComposite != null)
                {
                    SetInstanceTexture(shader, ReservedTextureSlots.MorphCompositeTexture, uniforms.MorphCompositeTexture, morphComposite.CompositeTexture);
                    GL.ProgramUniform2(shader.Program, uniforms.MorphCompositeTextureSize, (float)morphComposite.CompositeTexture.Width, (float)morphComposite.CompositeTexture.Height);
                }

                GL.ProgramUniform1(shader.Program, uniforms.MorphVertexIdOffset, morphComposite != null ? request.Call.VertexIdOffset : -1);
            }

            if (uniforms.Tint > -1)
            {
                var instanceTint = (request.Node is SceneAggregate.Fragment fragment) ? fragment.Tint : Vector4.One;
                var tint = request.Mesh.Tint * request.Call.TintColor * instanceTint;

                GL.ProgramUniform4(shader.Program, uniforms.Tint, tint.X, tint.Y, tint.Z, tint.W);
            }

            GL.VertexAttrib1(/*uniforms.ObjectId*/ 5, BitConverter.UInt32BitsToSingle(request.Node.Id));

            GL.DrawElementsBaseVertex(
                request.Call.PrimitiveType,
                request.Call.IndexCount,
                request.Call.IndexType,
                request.Call.StartIndex,
                request.Call.BaseVertex
            );

            UnbindInstanceTextures();
        }
    }
}
