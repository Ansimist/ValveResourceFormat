using System.Buffers;
using GUI.Types.Renderer;
using GUI.Utils;
using OpenTK.Graphics.OpenGL;
using ValveResourceFormat;

namespace GUI.Types.ParticleRenderer.Renderers
{
    internal class RenderTrails : ParticleFunctionRenderer
    {
        private const string ShaderName = "vrf.particle.sprite";
        private const int VertexSize = 9;

        private Shader shader;
        private readonly VrfGuiContext guiContext;
        private readonly int vaoHandle;
        private readonly RenderTexture texture;

        private readonly float animationRate = 0.1f;
        private readonly ParticleAnimationType animationType = ParticleAnimationType.ANIMATION_TYPE_FIXED_RATE;

        private readonly ParticleBlendMode blendMode = ParticleBlendMode.PARTICLE_OUTPUT_BLEND_MODE_ALPHA;
        private readonly INumberProvider overbrightFactor = new LiteralNumberProvider(1);
        private readonly ParticleOrientation orientationType;
        private readonly ParticleField prevPositionSource = ParticleField.PositionPrevious; // this is a real thing

        private readonly float finalTextureScaleU = 1f;
        private readonly float finalTextureScaleV = 1f;

        private readonly float maxLength = 2000f;
        private readonly float lengthFadeInTime;
        private int vertexBufferHandle;

        public RenderTrails(ParticleDefinitionParser parse, VrfGuiContext vrfGuiContext) : base(parse)
        {
            guiContext = vrfGuiContext;
            shader = vrfGuiContext.ShaderLoader.LoadShader(ShaderName);

            // The same quad is reused for all particles
            vaoHandle = SetupQuadBuffer();

            string textureName = null;

            if (parse.Data.ContainsKey("m_hTexture"))
            {
                textureName = parse.Data.GetProperty<string>("m_hTexture");
            }
            else
            {
                var textures = parse.Array("m_vecTexturesInput");
                if (textures.Length > 0)
                {
                    // TODO: Support more than one texture
                    textureName = textures[0].Data.GetProperty<string>("m_hTexture");

                    // TODO: Read m_TextureControls for m_flFinalTextureScaleU and m_flFinalTextureScaleV
                }
            }

            if (textureName == null)
            {
                texture = vrfGuiContext.MaterialLoader.GetErrorTexture();
            }
            else
            {
                texture = vrfGuiContext.MaterialLoader.GetTexture(textureName);
            }

#if DEBUG
            var vaoLabel = $"{nameof(RenderTrails)}: {System.IO.Path.GetFileName(textureName)}";
            GL.ObjectLabel(ObjectLabelIdentifier.VertexArray, vaoHandle, vaoLabel.Length, vaoLabel);
#endif

            blendMode = parse.Enum<ParticleBlendMode>("m_nOutputBlendMode", blendMode);
            overbrightFactor = parse.NumberProvider("m_flOverbrightFactor", overbrightFactor);
            orientationType = parse.Enum("m_nOrientationType", orientationType);
            animationRate = parse.Float("m_flAnimationRate", animationRate);
            finalTextureScaleU = parse.Float("m_flFinalTextureScaleU", finalTextureScaleU);
            finalTextureScaleV = parse.Float("m_flFinalTextureScaleV", finalTextureScaleV);
            maxLength = parse.Float("m_flMaxLength", maxLength);
            lengthFadeInTime = parse.Float("m_flLengthFadeInTime", lengthFadeInTime);
            animationType = parse.Enum<ParticleAnimationType>("m_nAnimationType", animationType);
            prevPositionSource = parse.ParticleField("m_nPrevPntSource", prevPositionSource);
        }

        public override void SetWireframe(bool isWireframe)
        {
            shader.SetUniform1("isWireframe", isWireframe ? 1 : 0);
        }

        private int SetupQuadBuffer()
        {
            const int stride = sizeof(float) * VertexSize;

            GL.CreateVertexArrays(1, out int vao);
            GL.CreateBuffers(1, out vertexBufferHandle);
            GL.VertexArrayVertexBuffer(vao, 0, vertexBufferHandle, 0, stride);
            GL.VertexArrayElementBuffer(vao, guiContext.MeshBufferCache.QuadIndices.GLHandle);

            var positionAttributeLocation = GL.GetAttribLocation(shader.Program, "aVertexPosition");
            var colorAttributeLocation = GL.GetAttribLocation(shader.Program, "aVertexColor");
            var uvAttributeLocation = GL.GetAttribLocation(shader.Program, "aTexCoords");

            GL.EnableVertexArrayAttrib(vao, positionAttributeLocation);
            GL.EnableVertexArrayAttrib(vao, colorAttributeLocation);
            GL.EnableVertexArrayAttrib(vao, uvAttributeLocation);

            GL.VertexArrayAttribFormat(vao, positionAttributeLocation, 3, VertexAttribType.Float, false, 0);
            GL.VertexArrayAttribFormat(vao, colorAttributeLocation, 4, VertexAttribType.Float, false, sizeof(float) * 3);
            GL.VertexArrayAttribFormat(vao, uvAttributeLocation, 2, VertexAttribType.Float, false, sizeof(float) * 7);

            GL.VertexArrayAttribBinding(vao, positionAttributeLocation, 0);
            GL.VertexArrayAttribBinding(vao, colorAttributeLocation, 0);
            GL.VertexArrayAttribBinding(vao, uvAttributeLocation, 0);

            return vao;
        }

        private void UpdateVertices(ParticleCollection particles, ParticleSystemRenderState systemRenderState, Matrix4x4 modelViewMatrix)
        {
            // Create billboarding rotation (always facing camera)
            Matrix4x4.Decompose(modelViewMatrix, out _, out var modelViewRotation, out _);
            modelViewRotation = Quaternion.Inverse(modelViewRotation);
            var billboardMatrix = Matrix4x4.CreateFromQuaternion(modelViewRotation);

            // Update vertex buffer
            var rawVertices = ArrayPool<float>.Shared.Rent(particles.Count * VertexSize * 4);

            try
            {
                var i = 0;
                foreach (ref var particle in particles.Current)
                {
                    var position = particle.Position;
                    var previousPosition = particle.GetVector(prevPositionSource);
                    var difference = previousPosition - position;
                    var direction = Vector3.Normalize(difference);

                    var midPoint = position + (0.5f * difference);

                    // Trail width = radius
                    // Trail length = distance between current and previous times trail length divided by 2 (because the base particle is 2 wide)
                    var length = Math.Min(maxLength, particle.TrailLength * difference.Length() / 2f);
                    var t = particle.NormalizedAge;
                    var animatedLength = t >= lengthFadeInTime
                        ? length
                        : t * length / lengthFadeInTime;
                    var scaleMatrix = Matrix4x4.CreateScale(particle.Radius, animatedLength, 1);

                    // Center the particle at the midpoint between the two points
                    var translationMatrix = Matrix4x4.CreateTranslation(Vector3.UnitY * animatedLength);

                    // Calculate rotation matrix

                    var axis = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, direction));
                    var angle = MathF.Acos(direction.Y);
                    var rotationMatrix = Matrix4x4.CreateFromAxisAngle(axis, angle);

                    //var radiusScale = this.radiusScale.NextNumber(ref particle, systemRenderState); // trails can have this too i think

                    var modelMatrix = orientationType == ParticleOrientation.PARTICLE_ORIENTATION_SCREEN_ALIGNED
                        ? scaleMatrix * translationMatrix * rotationMatrix
                        : particle.GetTransformationMatrix();

                    var tl = Vector4.Transform(new Vector4(-1, -1, 0, 1), modelMatrix);
                    var bl = Vector4.Transform(new Vector4(-1, 1, 0, 1), modelMatrix);
                    var br = Vector4.Transform(new Vector4(1, 1, 0, 1), modelMatrix);
                    var tr = Vector4.Transform(new Vector4(1, -1, 0, 1), modelMatrix);

                    var quadStart = i * VertexSize * 4;
                    rawVertices[quadStart + 0] = tl.X;
                    rawVertices[quadStart + 1] = tl.Y;
                    rawVertices[quadStart + 2] = tl.Z;
                    rawVertices[quadStart + (VertexSize * 1) + 0] = bl.X;
                    rawVertices[quadStart + (VertexSize * 1) + 1] = bl.Y;
                    rawVertices[quadStart + (VertexSize * 1) + 2] = bl.Z;
                    rawVertices[quadStart + (VertexSize * 2) + 0] = br.X;
                    rawVertices[quadStart + (VertexSize * 2) + 1] = br.Y;
                    rawVertices[quadStart + (VertexSize * 2) + 2] = br.Z;
                    rawVertices[quadStart + (VertexSize * 3) + 0] = tr.X;
                    rawVertices[quadStart + (VertexSize * 3) + 1] = tr.Y;
                    rawVertices[quadStart + (VertexSize * 3) + 2] = tr.Z;

                    //var alphaScale = this.alphaScale.NextNumber(ref particle, systemRenderState);
                    var alphaScale = 1f;

                    // Colors
                    for (var j = 0; j < 4; ++j)
                    {
                        rawVertices[quadStart + (VertexSize * j) + 3] = particle.Color.X;
                        rawVertices[quadStart + (VertexSize * j) + 4] = particle.Color.Y;
                        rawVertices[quadStart + (VertexSize * j) + 5] = particle.Color.Z;
                        rawVertices[quadStart + (VertexSize * j) + 6] = particle.Alpha * particle.AlphaAlternate * alphaScale;
                    }

                    // UVs
                    var spriteSheetData = texture.SpriteSheetData;
                    if (spriteSheetData != null && spriteSheetData.Sequences.Length > 0 && spriteSheetData.Sequences[0].Frames.Length > 0)
                    {
                        var sequence = spriteSheetData.Sequences[particle.Sequence % spriteSheetData.Sequences.Length];

                        var animationTime = animationType switch
                        {
                            ParticleAnimationType.ANIMATION_TYPE_FIXED_RATE => particle.Age,
                            ParticleAnimationType.ANIMATION_TYPE_FIT_LIFETIME => particle.NormalizedAge,
                            ParticleAnimationType.ANIMATION_TYPE_MANUAL_FRAMES => particle.Age, // literally dont know what to do with this one
                            _ => particle.Age,
                        };

                        /*
                        var frameId = 0;

                        if (sequence.Frames.Length > 1)
                        {
                            var animateInFps = false; // Trails can probably have this too
                            if (animateInFps)
                            {
                                frameId = (int)Math.Floor(animationRate * animationTime);
                            }
                            else
                            {
                                frameId = (int)Math.Floor(sequence.Frames.Length * animationRate * animationTime);
                            }

                            if (sequence.Clamp)
                            {
                                frameId = Math.Min(frameId, sequence.Frames.Length - 1);
                            }
                            else
                            {
                                frameId %= sequence.Frames.Length;
                            }
                        }
                        */

                        var frame = animationTime * sequence.FramesPerSecond * animationRate;
                        var frameId = (int)MathF.Floor(frame) % sequence.Frames.Length;

                        var currentFrame = sequence.Frames[frameId];
                        var currentImage = currentFrame.Images[0]; // TODO: Support more than one image per frame?

                        // Lerp frame coords and size
                        var subFrameTime = frame % 1.0f;
                        var offset = MathUtils.Lerp(subFrameTime, currentImage.CroppedMin, currentImage.UncroppedMin);
                        var scale = MathUtils.Lerp(subFrameTime, currentImage.CroppedMax - currentImage.CroppedMin,
                            currentImage.UncroppedMax - currentImage.UncroppedMin);

                        scale *= new Vector2(finalTextureScaleU, finalTextureScaleV);

                        rawVertices[quadStart + (VertexSize * 0) + 7] = offset.X;
                        rawVertices[quadStart + (VertexSize * 0) + 8] = offset.Y + scale.Y;
                        rawVertices[quadStart + (VertexSize * 1) + 7] = offset.X;
                        rawVertices[quadStart + (VertexSize * 1) + 8] = offset.Y;
                        rawVertices[quadStart + (VertexSize * 2) + 7] = offset.X + scale.X;
                        rawVertices[quadStart + (VertexSize * 2) + 8] = offset.Y;
                        rawVertices[quadStart + (VertexSize * 3) + 7] = offset.X + scale.X;
                        rawVertices[quadStart + (VertexSize * 3) + 8] = offset.Y + scale.Y;
                    }
                    else
                    {
                        rawVertices[quadStart + (VertexSize * 0) + 7] = 0;
                        rawVertices[quadStart + (VertexSize * 0) + 8] = 1;
                        rawVertices[quadStart + (VertexSize * 1) + 7] = 0;
                        rawVertices[quadStart + (VertexSize * 1) + 8] = 0;
                        rawVertices[quadStart + (VertexSize * 2) + 7] = 1;
                        rawVertices[quadStart + (VertexSize * 2) + 8] = 0;
                        rawVertices[quadStart + (VertexSize * 3) + 7] = 1;
                        rawVertices[quadStart + (VertexSize * 3) + 8] = 1;
                    }

                    i++;
                }

                GL.NamedBufferData(vertexBufferHandle, particles.Count * VertexSize * 4 * sizeof(float), rawVertices, BufferUsageHint.DynamicDraw);
            }
            finally
            {
                ArrayPool<float>.Shared.Return(rawVertices);
            }
        }

        public override void Render(ParticleCollection particleBag, ParticleSystemRenderState systemRenderState, Matrix4x4 modelViewMatrix)
        {
            UpdateVertices(particleBag, systemRenderState, modelViewMatrix);

            if (blendMode == ParticleBlendMode.PARTICLE_OUTPUT_BLEND_MODE_ADD)
            {
                GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
            }
            else /* if (blendMode == ParticleBlendMode.PARTICLE_OUTPUT_BLEND_MODE_ALPHA) */
            {
                GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            }

            GL.UseProgram(shader.Program);

            GL.BindVertexArray(vaoHandle);

            // set texture unit 0 as uTexture uniform
            shader.SetTexture(0, "uTexture", texture);

            // TODO: This formula is a guess but still seems too bright compared to valve particles
            // also todo: pass all of these as vertex parameters (probably just color/alpha combined)
            shader.SetUniform1("uOverbrightFactor", (float)overbrightFactor.NextNumber());

            GL.DrawElements(BeginMode.Triangles, particleBag.Count * 6, DrawElementsType.UnsignedShort, 0);

            /*
            // Todo: this could be adapted into renderropes without much difficulty
            foreach (ref var particle in particles)
            {
                var position = particle.Position;
                var previousPosition = particle.GetVector(prevPositionSource);
                var difference = previousPosition - position;
                var direction = Vector3.Normalize(difference);

                var midPoint = position + (0.5f * difference);

                // Trail width = radius
                // Trail length = distance between current and previous times trail length divided by 2 (because the base particle is 2 wide)
                var length = Math.Min(maxLength, particle.TrailLength * difference.Length() / 2f);
                var t = particle.NormalizedAge;
                var animatedLength = t >= lengthFadeInTime
                    ? length
                    : t * length / lengthFadeInTime;
                var scaleMatrix = Matrix4x4.CreateScale(particle.Radius, animatedLength, 1);

                // Center the particle at the midpoint between the two points
                var translationMatrix = Matrix4x4.CreateTranslation(Vector3.UnitY * animatedLength);

                // Calculate rotation matrix

                var axis = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, direction));
                var angle = MathF.Acos(direction.Y);
                var rotationMatrix = Matrix4x4.CreateFromAxisAngle(axis, angle);

                var modelMatrix = orientationType == ParticleOrientation.PARTICLE_ORIENTATION_SCREEN_ALIGNED
                    ? Matrix4x4.Multiply(scaleMatrix, Matrix4x4.Multiply(translationMatrix, rotationMatrix))
                    : particle.GetTransformationMatrix();

                // Position/Radius uniform
                shader.SetUniform4x4("uModelMatrix", modelMatrix);

                var spriteSheetData = texture.SpriteSheetData;
                if (spriteSheetData != null && spriteSheetData.Sequences.Length > 0 && spriteSheetData.Sequences[0].Frames.Length > 0)
                {
                    var sequence = spriteSheetData.Sequences[0];

                    var animationTime = animationType switch
                    {
                        ParticleAnimationType.ANIMATION_TYPE_FIXED_RATE => particle.Age,
                        ParticleAnimationType.ANIMATION_TYPE_FIT_LIFETIME => particle.NormalizedAge,
                        _ => particle.Age,
                    };
                    var frame = animationTime * sequence.FramesPerSecond * animationRate;

                    var currentFrame = sequence.Frames[(int)MathF.Floor(frame) % sequence.Frames.Length];
                    var currentImage = currentFrame.Images[0]; // TODO: Support more than one image per frame?

                    // Lerp frame coords and size
                    var subFrameTime = frame % 1.0f;
                    var offset = MathUtils.Lerp(subFrameTime, currentImage.CroppedMin, currentImage.UncroppedMin);
                    var scale = MathUtils.Lerp(subFrameTime, currentImage.CroppedMax - currentImage.CroppedMin,
                        currentImage.UncroppedMax - currentImage.UncroppedMin);

                    shader.SetUniform2("uUvOffset", offset);
                    shader.SetUniform2("uUvScale", scale * new Vector2(finalTextureScaleU, finalTextureScaleV));
                }
                else
                {
                    shader.SetUniform2("uUvOffset", Vector2.One);
                    shader.SetUniform2("uUvScale", new Vector2(finalTextureScaleU, finalTextureScaleV));
                }

                // Color uniform
                shader.SetUniform3("uColor", particle.Color);
                shader.SetUniform1("uAlpha", particle.Alpha * particle.AlphaAlternate);

                GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
            }
            */

            GL.UseProgram(0);
            GL.BindVertexArray(0);
        }

        public override IEnumerable<string> GetSupportedRenderModes() => shader.RenderModes;

        public override void SetRenderMode(string renderMode)
        {
            var parameters = new Dictionary<string, byte>();

            if (renderMode != null && shader.RenderModes.Contains(renderMode))
            {
                parameters.Add(string.Concat(ShaderLoader.RenderModeDefinePrefix, renderMode), 1);
            }

            shader = guiContext.ShaderLoader.LoadShader(ShaderName, parameters);
        }
    }
}
