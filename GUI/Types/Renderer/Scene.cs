using System.Linq;
using GUI.Types.Renderer.UniformBuffers;
using GUI.Utils;
using OpenTK.Graphics.OpenGL;

namespace GUI.Types.Renderer
{
    partial class Scene
    {
        public readonly struct UpdateContext
        {
            public float Timestep { get; }

            public UpdateContext(float timestep)
            {
                Timestep = timestep;
            }
        }

        public enum RenderPassFlags
        {
            None,
            DepthPassAllowed = 1 << 0,
            WireframeAllowed = 1 << 1,
            All = DepthPassAllowed | WireframeAllowed
        }

        public struct RenderContext
        {
            public GLSceneViewer View { get; init; }
            public Scene Scene { get; set; }
            public Camera Camera { get; set; }
            public Framebuffer Framebuffer { get; set; }
            public RenderPass RenderPass { get; set; }
            public RenderPassFlags Flags { get; set; }
            public Shader ReplacementShader { get; set; }
        }

        public WorldLightingInfo LightingInfo { get; }
        public WorldFogInfo FogInfo { get; set; } = new();
        public Dictionary<string, byte> RenderAttributes { get; } = [];
        public VrfGuiContext GuiContext { get; }
        public Octree<SceneNode> StaticOctree { get; }
        public Octree<SceneNode> DynamicOctree { get; }

        public bool ShowToolsMaterials { get; set; }
        public bool DepthPassEnabled { get; set; } = true;
        public bool FogEnabled { get; set; } = true;

        private StorageBuffer instanceBuffer;
        private StorageBuffer transformBuffer;
        private StorageBuffer envMapBindingBuffer;
        private UniformBuffer<LightingConstants> lightingBuffer;

        public IEnumerable<SceneNode> AllNodes => staticNodes.Concat(dynamicNodes);
        public uint NodeCount { get; private set; }

        private readonly List<SceneNode> staticNodes = [];
        private readonly List<SceneNode> dynamicNodes = [];

        public Scene(VrfGuiContext context, float sizeHint = 32768)
        {
            GuiContext = context;
            StaticOctree = new Octree<SceneNode>(sizeHint);
            DynamicOctree = new Octree<SceneNode>(sizeHint);

            LightingInfo = new(this);
        }

        public void Add(SceneNode node, bool dynamic)
        {
            if (dynamic)
            {
                dynamicNodes.Add(node);
                DynamicOctree.Insert(node, node.BoundingBox);
            }
            else
            {
                staticNodes.Add(node);
                StaticOctree.Insert(node, node.BoundingBox);
            }

            NodeCount++;
        }

        public void UpdateNodeIndices()
        {
            var i = 1u;
            foreach (var node in staticNodes.Concat(dynamicNodes))
            {
                node.Id = i++;
            }
        }

        public SceneNode Find(uint id)
        {
            if (id == 0)
            {
                return null;
            }

            var index = (int)id - 1;
            if (index < staticNodes.Count)
            {
                return staticNodes[index];
            }

            index -= staticNodes.Count;
            if (index < dynamicNodes.Count)
            {
                return dynamicNodes[index];
            }

            return null;
        }

        public void Update(float timestep)
        {
            var updateContext = new UpdateContext(timestep);

            foreach (var node in staticNodes)
            {
                node.Update(updateContext);
            }

            foreach (var node in dynamicNodes)
            {
                var oldBox = node.BoundingBox;
                node.Update(updateContext);
                DynamicOctree.Update(node, oldBox, node.BoundingBox);
            }

        }

        private readonly List<SceneNode> CullResults = [];
        private int StaticCount;
        private int LastFrustum = -1;

        public List<SceneNode> GetFrustumCullResults(Frustum frustum)
        {
            var currentFrustum = frustum.GetHashCode();
            if (LastFrustum != currentFrustum)
            {
                LastFrustum = currentFrustum;

                CullResults.Clear();
                CullResults.Capacity = staticNodes.Count + dynamicNodes.Count + 100;

                StaticOctree.Root.Query(frustum, CullResults);
                StaticCount = CullResults.Count;
            }
            else
            {
                CullResults.RemoveRange(StaticCount, CullResults.Count - StaticCount);
            }

            DynamicOctree.Root.Query(frustum, CullResults);
            return CullResults;
        }

        private readonly List<MeshBatchRenderer.Request> renderLooseNodes = [];
        private readonly List<MeshBatchRenderer.Request> renderOpaqueDrawCalls = [];
        private readonly List<MeshBatchRenderer.Request> depthPassOpaqueCalls = [];
        private readonly List<MeshBatchRenderer.Request> depthPassAlphaTestCalls = [];
        private readonly List<MeshBatchRenderer.Request> renderStaticOverlays = [];
        private readonly List<MeshBatchRenderer.Request> renderTranslucentDrawCalls = [];

        private void Add(MeshBatchRenderer.Request request, RenderPass renderPass)
        {
            if (renderPass != RenderPass.AfterOpaque && !ShowToolsMaterials && request.Call.Material.IsToolsMaterial)
            {
                return;
            }

            var queueList = renderPass switch
            {
                RenderPass.Opaque => renderOpaqueDrawCalls,
                RenderPass.StaticOverlay => renderStaticOverlays,
                RenderPass.Translucent => renderTranslucentDrawCalls,
                _ => renderLooseNodes,
            };

            if (DepthPassEnabled && renderPass == RenderPass.Opaque)
            {
                if (!request.Call.Material.NoZPrepass && request.Mesh.AnimationTexture == null && request.Mesh.FlexStateManager?.MorphComposite == null
                 && !request.Call.Material.IsAlphaTest)
                {
                    queueList = request.Call.Material.IsAlphaTest ? depthPassAlphaTestCalls : depthPassOpaqueCalls;
                }
            }

            queueList.Add(request);
        }

        public void CollectSceneDrawCalls(Camera camera, Frustum cullFrustum = null)
        {
            depthPassOpaqueCalls.Clear();
            renderOpaqueDrawCalls.Clear();
            depthPassAlphaTestCalls.Clear();
            renderStaticOverlays.Clear();
            renderTranslucentDrawCalls.Clear();
            renderLooseNodes.Clear();

            cullFrustum ??= camera.ViewFrustum;
            var cullResults = GetFrustumCullResults(cullFrustum);

            // Collect mesh calls
            foreach (var node in cullResults)
            {
                if (node is IRenderableMeshCollection meshCollection)
                {
                    foreach (var mesh in meshCollection.RenderableMeshes)
                    {
                        foreach (var call in mesh.DrawCallsOpaque)
                        {
                            Add(new MeshBatchRenderer.Request
                            {
                                Transform = node.Transform,
                                Mesh = mesh,
                                Call = call,
                                Node = node,
                            }, RenderPass.Opaque);
                        }

                        foreach (var call in mesh.DrawCallsOverlay)
                        {
                            Add(new MeshBatchRenderer.Request
                            {
                                Transform = node.Transform,
                                Mesh = mesh,
                                Call = call,
                                RenderOrder = node.OverlayRenderOrder,
                                Node = node,
                            }, RenderPass.StaticOverlay);
                        }

                        foreach (var call in mesh.DrawCallsBlended)
                        {
                            Add(new MeshBatchRenderer.Request
                            {
                                Transform = node.Transform,
                                Mesh = mesh,
                                Call = call,
                                DistanceFromCamera = (node.BoundingBox.Center - camera.Location).LengthSquared(),
                                Node = node,
                            }, RenderPass.Translucent);
                        }
                    }
                }
                else if (node is SceneAggregate aggregate)
                {
                }
                else if (node is SceneAggregate.Fragment fragment)
                {
                    Add(new MeshBatchRenderer.Request
                    {
                        Transform = fragment.Transform,
                        Mesh = fragment.RenderMesh,
                        Call = fragment.DrawCall,
                        Node = node,
                    }, RenderPass.Opaque);
                }
                else
                {
                    Add(new MeshBatchRenderer.Request
                    {
                        DistanceFromCamera = (node.BoundingBox.Center - camera.Location).LengthSquared(),
                        Node = node,
                    }, RenderPass.AfterOpaque);
                }
            }


            renderOpaqueDrawCalls.Sort(MeshBatchRenderer.ComparePipeline);
            renderStaticOverlays.Sort(MeshBatchRenderer.CompareRenderOrderThenPipeline);
            renderTranslucentDrawCalls.Sort(MeshBatchRenderer.CompareCameraDistance_BackToFront);
            renderLooseNodes.Sort(MeshBatchRenderer.CompareCameraDistance_BackToFront);
            depthPassOpaqueCalls.Sort(MeshBatchRenderer.CompareVaoThenDistance);
            depthPassAlphaTestCalls.Sort(MeshBatchRenderer.CompareCameraDistance_FrontToBack);
        }

        public void DepthPassOpaque(RenderContext renderContext)
        {
            renderContext.RenderPass = RenderPass.Opaque;

            MeshBatchRenderer.Render(depthPassOpaqueCalls, renderContext);
        }

        public void RenderOpaqueLayer(RenderContext renderContext, bool depthPrepassed = false)
        {
            var camera = renderContext.Camera;

            using (new GLDebugGroup("Opaque Render"))
            {
                if (depthPrepassed)
                {
                    using var _ = new GLDebugGroup("Render Depth Prepassed");
                    GL.DepthMask(false);
                    GL.DepthFunc(DepthFunction.Gequal);

                    MeshBatchRenderer.Render(depthPassOpaqueCalls, renderContext);

                    GL.DepthMask(true);
                    GL.DepthFunc(DepthFunction.Greater);
                }
                else
                {
                    MeshBatchRenderer.Render(depthPassOpaqueCalls, renderContext);
                }

                MeshBatchRenderer.Render(renderOpaqueDrawCalls, renderContext);
            }

            using (new GLDebugGroup("StaticOverlay Render"))
            {
                renderContext.RenderPass = RenderPass.StaticOverlay;
                MeshBatchRenderer.Render(renderStaticOverlays, renderContext);
            }

            using (new GLDebugGroup("AfterOpaque RenderLoose"))
            {
                renderContext.RenderPass = RenderPass.AfterOpaque;
                foreach (var request in renderLooseNodes)
                {
                    request.Node.Render(renderContext);
                }
            }
        }

        public void RenderTranslucentLayer(RenderContext renderContext)
        {
            using (new GLDebugGroup("Translucent RenderLoose"))
            {
                renderContext.RenderPass = RenderPass.Translucent;
                foreach (var request in renderLooseNodes)
                {
                    request.Node.Render(renderContext);
                }
            }

            using (new GLDebugGroup("Translucent Render"))
            {
                MeshBatchRenderer.Render(renderTranslucentDrawCalls, renderContext);
            }
        }

        public void SetEnabledLayers(HashSet<string> layers)
        {
            foreach (var renderer in AllNodes)
            {
                renderer.LayerEnabled = layers.Contains(renderer.LayerName);
            }

            UpdateOctrees();
        }

        public void UpdateOctrees()
        {
            LastFrustum = -1;
            StaticOctree.Clear();
            DynamicOctree.Clear();

            foreach (var node in staticNodes)
            {
                if (node.LayerEnabled)
                {
                    StaticOctree.Insert(node, node.BoundingBox);
                }
            }

            foreach (var node in dynamicNodes)
            {
                if (node.LayerEnabled)
                {
                    DynamicOctree.Insert(node, node.BoundingBox);
                }
            }
        }

        public void CreateBuffers()
        {
            lightingBuffer = new(ReservedBufferSlots.Lighting)
            {
                Data = LightingInfo.LightingData
            };

            envMapBindingBuffer = new(ReservedBufferSlots.EnvmapBinding);
            instanceBuffer = new(ReservedBufferSlots.InstanceBuffer);
            transformBuffer = new(ReservedBufferSlots.TransformBuffer);
        }

        [System.Runtime.CompilerServices.InlineArray(SizeInBytes / 4)]
        public struct PerInstancePackedData
        {
            public const int SizeInBytes = 8 * 4;
            private uint data;

            public Color32 TintAlpha { readonly get => new(this[0]); set => this[0] = value.PackedValue; }
            public int TransformBufferIndex { readonly get => (int)this[1]; set => this[1] = (uint)value; }
            public int LightProbeBinding { readonly get => (int)this[2]; set => this[2] = (uint)value; }

            //public int EnvMapCount { readonly get => (int)this[2]; set => this[2] = (uint)value; }
            //public bool CustomLightingOrigin { readonly get => (this[3] & 1) != 0; set => PackBit(ref this[3], value); }

            private static void PackBit(ref uint @uint, bool value)
            {
                @uint = (@uint & ~1u) | (value ? 1u : 0u);
            }
        }

        public void UpdateInstanceBuffers()
        {
            var transformData = new List<Matrix4x4>() { Matrix4x4.Identity };

            var instanceBufferData = new PerInstancePackedData[NodeCount + 1];

            foreach (var node in AllNodes)
            {
                if (node.Id > NodeCount || node.Id < 0)
                {
                    continue;
                }

                ref var instanceData = ref instanceBufferData[node.Id];

                if (node.Transform.IsIdentity)
                {
                    instanceData.TransformBufferIndex = 0;
                }
                else
                {
                    instanceData.TransformBufferIndex = transformData.Count;
                    transformData.Add(node.Transform);
                }

                //instanceData.TintAlpha = node.TintAlpha;
                //instanceData.EnvMapCount = node.EnvMapCount;
                // TODO: numbones
                instanceData.LightProbeBinding = LightingInfo.LightProbes.IndexOf(node.LightProbeBinding);
            }

            instanceBuffer.Create(instanceBufferData, PerInstancePackedData.SizeInBytes);
            transformBuffer.Create(transformData.ToArray(), 64);

            envMapBindingBuffer.Create(LightingInfo.EnvMapBindings, 1);
        }

        public void CalculateLightProbeBindings()
        {
            var ProbeList = LightingInfo.LightProbes;
            if (ProbeList.Count == 0)
            {
                return;
            }

            ProbeList.Sort((a, b) => a.HandShake.CompareTo(b.HandShake));

            foreach (var node in AllNodes)
            {
                var precomputedHandshake = node.LightProbeVolumePrecomputedHandshake;
                if (precomputedHandshake == 0)
                {
                    continue;
                }

                if (LightingInfo.LightmapGameVersionNumber == 0 && precomputedHandshake <= ProbeList.Count)
                {
                    // SteamVR Home node handshake as probe index
                    node.LightProbeBinding = ProbeList[precomputedHandshake - 1];
                    continue;
                }

                if (LightingInfo.ProbeHandshakes.TryGetValue(precomputedHandshake, out var precomputedProbe))
                {
                    node.LightProbeBinding = precomputedProbe;
                    continue;
                }
            }

            var sortedLightProbes = ProbeList
                .OrderByDescending(static lpv => lpv.IndoorOutdoorLevel)
                .ThenBy(static lpv => lpv.AtlasSize.LengthSquared());

            foreach (var probe in sortedLightProbes)
            {
                var nodes = new List<SceneNode>();
                StaticOctree.Root.Query(probe.BoundingBox, nodes);
                DynamicOctree.Root.Query(probe.BoundingBox, nodes);

                foreach (var node in nodes)
                {
                    node.LightProbeBinding ??= probe;
                }

                var index = ProbeList.IndexOf(probe);

                probe.SetGpuProbeData(
                    LightingInfo.LightProbeType == LightProbeType.ProbeAtlas,
                    ref LightingInfo.LightingData.LightProbeVolume[index]
                );
            }
        }

        public void CalculateEnvironmentMaps()
        {
            var EnvMapList = LightingInfo.EnvMaps;
            if (EnvMapList.Count == 0)
            {
                return;
            }

            var firstTexture = EnvMapList.First().EnvMapTexture;

            LightingInfo.LightingData.EnvMapSizeConstants = new Vector4(
                firstTexture.NumMipLevels - 1,
                firstTexture.Depth,
                LightingInfo.CubemapType == CubemapType.CubemapArray ? 1 : 0,
                0
            );

            int ArrayIndexCompare(SceneEnvMap a, SceneEnvMap b) => a.ArrayIndex.CompareTo(b.ArrayIndex);
            int HandShakeCompare(SceneEnvMap a, SceneEnvMap b) => a.HandShake.CompareTo(b.HandShake);

            EnvMapList.Sort(LightingInfo.CubemapType switch
            {
                CubemapType.CubemapArray => ArrayIndexCompare,
                _ => HandShakeCompare
            });

            var i = 0;
            Span<int> gpuDataToTextureIndex = stackalloc int[LightingConstants.MAX_ENVMAPS];
            var envMapsById = EnvMapList.OrderByDescending((envMap) => envMap.IndoorOutdoorLevel).ToList();

            LightingInfo.EnvMapBindings = new byte[(NodeCount + 1) * LightingConstants.MAX_ENVMAPS];
            var queryList = new List<SceneNode>();

            foreach (var envMap in envMapsById)
            {
                if (envMap.ArrayIndex >= LightingConstants.MAX_ENVMAPS || envMap.ArrayIndex < 0)
                {
                    Log.Error(nameof(WorldLoader), $"Envmap array index {i} is too large, skipping! Max: {LightingConstants.MAX_ENVMAPS}");
                    continue;
                }

                if (LightingInfo.CubemapType == CubemapType.CubemapArray)
                {
                    StaticOctree.Root.Query(envMap.BoundingBox, queryList);
                    DynamicOctree.Root.Query(envMap.BoundingBox, queryList); // TODO: This should actually be done dynamically

                    foreach (var node in queryList)
                    {
                        // First act as a visibility buffer, then we trim and sort it
                        LightingInfo.EnvMapBindings[node.Id * LightingConstants.MAX_ENVMAPS + i] = 1;
                    }

                    queryList.Clear();
                }

                UpdateGpuEnvmapData(envMap, i, envMap.ArrayIndex);
                gpuDataToTextureIndex[i] = envMap.ArrayIndex;
                i++;
            }

            Span<byte> envMapVisibility = stackalloc byte[LightingConstants.MAX_ENVMAPS];

            foreach (var node in AllNodes)
            {
                var precomputedHandshake = node.CubeMapPrecomputedHandshake;
                SceneEnvMap preComputed = default;
                var fixedEnvMapIndex = -1;

                if (precomputedHandshake > 0)
                {
                    if (LightingInfo.CubemapType == CubemapType.IndividualCubemaps
                        && precomputedHandshake <= EnvMapList.Count)
                    {
                        // SteamVR Home node handshake as envmap index
                        node.EnvMap = preComputed = EnvMapList[precomputedHandshake - 1];
                    }
                    else if (LightingInfo.EnvMapHandshakes.TryGetValue(precomputedHandshake, out preComputed))
                    {
                        preComputed = LightingInfo.EnvMapHandshakes.GetValueOrDefault(precomputedHandshake);
                    }

                    if (preComputed is not null)
                    {
                        fixedEnvMapIndex = gpuDataToTextureIndex.IndexOf(preComputed.ArrayIndex);
                    }
                    else
                    {
#if DEBUG
                        Log.Debug(nameof(Scene), $"A envmap with handshake [{precomputedHandshake}] does not exist for node at {node.BoundingBox.Center}");
#endif
                    }

                }
                else if (node.LightingOrigin.HasValue || LightingInfo.CubemapType == CubemapType.IndividualCubemaps)
                {
                    var objectCenter = node.LightingOrigin ?? node.BoundingBox.Center;
                    var closest = EnvMapList.OrderBy(e => Vector3.Distance(e.BoundingBox.Center, objectCenter)).First();
                    node.EnvMap = closest;
                    fixedEnvMapIndex = gpuDataToTextureIndex.IndexOf(closest.ArrayIndex);
                }

                // Change visibility buffer to sorted indices
                var nodeEnvmapBindings = LightingInfo.EnvMapBindings.AsSpan().Slice((int)node.Id * LightingConstants.MAX_ENVMAPS, LightingConstants.MAX_ENVMAPS);

                if (nodeEnvmapBindings.Length == 0)
                {
                    continue;
                }

                if (fixedEnvMapIndex != -1)
                {
                    nodeEnvmapBindings[0] = 1;
                    nodeEnvmapBindings[1] = (byte)fixedEnvMapIndex;
                    continue;
                }

                nodeEnvmapBindings.CopyTo(envMapVisibility);
                nodeEnvmapBindings.Clear();

                var j = 0; // appended envmaps
                for (var k = 0; k < LightingConstants.MAX_ENVMAPS; k++)
                {
                    var visible = envMapVisibility[k] == 1;

                    if (visible)
                    {

                        nodeEnvmapBindings[j] = (byte)k;
                        j++;
                    }
                }

                nodeEnvmapBindings = nodeEnvmapBindings[..(j + 1)];

                // shift by one
                for (var k = j; k > 0; k--)
                {
                    nodeEnvmapBindings[k] = nodeEnvmapBindings[k - 1];
                }

                // store count
                nodeEnvmapBindings[0] = (byte)j;

                nodeEnvmapBindings[1..].Sort(
                    (aId, bId) =>
                    {
                        var (a, b) = (envMapsById[aId], envMapsById[bId]);

                        if (a.IndoorOutdoorLevel == b.IndoorOutdoorLevel)
                        {
                            var aDist = Vector3.DistanceSquared(node.BoundingBox.Min, a.BoundingBox.Min);
                            var bDist = Vector3.DistanceSquared(node.BoundingBox.Min, b.BoundingBox.Min);
                            return aDist - bDist > 0 ? 1 : -1;
                        }

                        return b.IndoorOutdoorLevel - a.IndoorOutdoorLevel;
                    });
            }

            var strideOld = LightingConstants.MAX_ENVMAPS;
            var strideNew = 16;
            var bindings = LightingInfo.EnvMapBindings.AsSpan();

            for (var obj = 0; obj < LightingInfo.EnvMapBindings.Length / strideOld; obj++)
            {
                var count = bindings[obj * strideOld];

                if (count > strideNew - 1)
                {
                    Log.Info(nameof(Scene), $"Envmap count {count} is too large, truncating to {strideNew - 1}");
                }

                bindings.Slice(obj * strideOld, strideNew).CopyTo(bindings.Slice(obj * strideNew, strideNew));
            }

            LightingInfo.EnvMapBindings = LightingInfo.EnvMapBindings[..(LightingInfo.EnvMapBindings.Length / strideOld * strideNew)];
        }

        private void UpdateGpuEnvmapData(SceneEnvMap envMap, int index, int arrayTextureIndex)
        {
            Matrix4x4.Invert(envMap.Transform, out var invertedTransform);

            LightingInfo.LightingData.EnvMapWorldToLocal[index] = invertedTransform;
            LightingInfo.LightingData.EnvMapBoxMins[index] = new Vector4(envMap.LocalBoundingBox.Min, arrayTextureIndex);
            LightingInfo.LightingData.EnvMapBoxMaxs[index] = new Vector4(envMap.LocalBoundingBox.Max, 0);
            LightingInfo.LightingData.EnvMapEdgeInvEdgeWidth[index] = new Vector4(Vector3.One / envMap.EdgeFadeDists, 0);
            LightingInfo.LightingData.EnvMapProxySphere[index] = new Vector4(envMap.Transform.Translation, envMap.ProjectionMode);
            LightingInfo.LightingData.EnvMapColorRotated[index] = new Vector4(envMap.Tint, 0);

            // TODO
            LightingInfo.LightingData.EnvMapNormalizationSH[index] = new Vector4(0, 0, 0, 1);
        }

        public void SetFogConstants(ViewConstants viewConstants)
        {
            FogInfo.SetFogUniforms(viewConstants, FogEnabled);
        }

        public void SetSceneBuffers()
        {
            lightingBuffer.BindBufferBase();
            envMapBindingBuffer.BindBufferBase();
            instanceBuffer.BindBufferBase();
            transformBuffer.BindBufferBase();
        }

        public void Dispose()
        {
            lightingBuffer?.Dispose();
            envMapBindingBuffer?.Dispose();
            instanceBuffer?.Dispose();
            transformBuffer?.Dispose();
        }
    }
}
