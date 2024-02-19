#version 460
//? #include "features.glsl"
//? #include "utils.glsl"
//? #include "LightingConstants.glsl"
//? #include "lighting_common.glsl"
//? #include "texturing.glsl"
//? #include "pbr.glsl"

#if (D_BAKED_LIGHTING_FROM_LIGHTMAP == 1)
    in vec3 vLightmapUVScaled;
    uniform sampler2DArray g_tIrradiance;
    uniform sampler2DArray g_tDirectionalIrradiance;
    #if (LightmapGameVersionNumber == 1)
        uniform sampler2DArray g_tDirectLightIndices;
        uniform sampler2DArray g_tDirectLightStrengths;
    #elif (LightmapGameVersionNumber == 2)
        uniform sampler2DArray g_tDirectLightShadows;
    #endif
#elif (D_BAKED_LIGHTING_FROM_VERTEX_STREAM == 1)
    in vec3 vPerVertexLightingOut;
#elif (D_BAKED_LIGHTING_FROM_PROBE == 1)

    uniform sampler3D g_tLPV_Irradiance;

    #if (LightmapGameVersionNumber == 1)
        uniform sampler3D g_tLPV_Indices;
        uniform sampler3D g_tLPV_Scalars;
    #elif (LightmapGameVersionNumber == 2)
        uniform sampler3D g_tLPV_Shadows;
    #endif

    struct LightProbe
    {
        vec3 Position;
        vec3 Min; vec3 Max;
        #if (LightmapGameVersionNumber == 2)
            vec3 AtlasOffset; vec3 AtlasScale;
        #endif
    };

    LightProbe GetProbe(vec3 fragPosition)
    {
        LightProbeVolumeData data = g_vLightProbeVolumeData[nEnvMap_LpvIndex.y];

        LightProbe probe;
        probe.Position = mat4x3(data.WorldToLocalNormalizer) * vec4(fragPosition, 1.0);
        probe.Min = data.Min.xyz;
        probe.Max = data.Max.xyz;

        #if (LightmapGameVersionNumber == 2)
            probe.AtlasOffset = data.AtlasOffset.xyz;
            probe.AtlasScale = data.AtlasScale.xyz;
        #endif

        return probe;
    }

    #if (LightmapGameVersionNumber == 2)
        vec3 CalculateAtlasProbeShadowCoords(vec3 fragPosition)
        {
            LightProbe probe = GetProbe(fragPosition);

            return fma(saturate(probe.Position), probe.AtlasScale, probe.AtlasOffset);
        }

        vec3 CalculateAtlasProbeIndirectCoords(vec3 fragPosition)
        {
            LightProbe probe = GetProbe(fragPosition);

            probe.Position.z /= 6;
            probe.Position = clamp(probe.Position, probe.Min, probe.Max);

            probe.Position.z *= 6;
            probe.Position = fma(probe.Position, probe.AtlasScale, probe.AtlasOffset);

            probe.Position.z /= 6;
            return probe.Position;
        }
    #endif
#endif

vec3 getSunDir()
{
    return normalize(mat3(vLightPosition) * vec3(-1, 0, 0));
}

vec3 getSunColor()
{
    return SrgbGammaToLinear(vLightColor.rgb) * vLightColor.a;
}

// This should contain our direct lighting loop
void CalculateDirectLighting(inout LightingTerms_t lighting, inout MaterialProperties_t mat)
{
    vec3 lightVector = getSunDir();

    // Lighting
    float visibility = 1.0;

    #if (LightmapGameVersionNumber == 1)
        #if (D_BAKED_LIGHTING_FROM_LIGHTMAP == 1)
            vec4 dls = texture(g_tDirectLightStrengths, vLightmapUVScaled);
            vec4 dli = texture(g_tDirectLightIndices, vLightmapUVScaled);
        #elif (D_BAKED_LIGHTING_FROM_PROBE == 1)
            vec3 vLightProbeShadowCoords = GetProbe(mat.PositionWS).Position;
            vec4 dls = textureLod(g_tLPV_Scalars, vLightProbeShadowCoords, 0.0);
            //vec4 dli = textureLod(g_tLPV_Indices, vLightProbeShadowCoords, 0.0);
            vec4 dli = vec4(0.12, 0.34, 0.56, 0); // Indices aren't working right now, just assume sun is in alpha.
        #else
            vec4 dls = vec4(1, 0, 0, 0);
            vec4 dli = vec4(0, 0, 0, 0);
        #endif

        //lighting.DiffuseDirect = dls.arg;
        //return;

        vec4 vLightStrengths = pow2(dls);
        ivec4 vLightIndices = ivec4(dli * 255.0);
        visibility = 0.0;

        int index = 0;
        for (int i = 0; i < 4; i++)
        {
            if (vLightIndices[i] != index)
                continue;

            visibility = vLightStrengths[i];
            break;
        }

    #elif (LightmapGameVersionNumber == 2)
        #if (D_BAKED_LIGHTING_FROM_LIGHTMAP == 1)
            vec4 dlsh = texture(g_tDirectLightShadows, vLightmapUVScaled);
        #elif (D_BAKED_LIGHTING_FROM_PROBE == 1)
            vec3 vLightProbeShadowCoords = CalculateAtlasProbeShadowCoords(mat.PositionWS);
            vec4 dlsh = textureLod(g_tLPV_Shadows, vLightProbeShadowCoords, 0.0);
            //if (sin(g_flTime * 3) > 0)
            //    dlsh = vec4(0);
        #else
            vec4 dlsh = vec4(1, 0, 0, 0);
        #endif

        int index = 0;
        visibility = 1.0 - dlsh[index];
    #endif


    if (visibility > 0.0001)
    {
        CalculateShading(lighting, lightVector, visibility * getSunColor(), mat);
    }
}


#if (D_BAKED_LIGHTING_FROM_LIGHTMAP == 1)

#define UseLightmapDirectionality 1

uniform float g_flDirectionalLightmapStrength = 1.0;
uniform float g_flDirectionalLightmapMinZ = 0.05;
const vec4 g_vLightmapParams = vec4(0.0); // ???? directional non-intensity?? it's set to 0.0 in all places ive looked

const float colorSpaceMul = 254 / 255;

// I don't actually understand much of this, but it's Valve's code.
vec3 ComputeLightmapShading(vec3 irradianceColor, vec4 irradianceDirection, vec3 normalMap)
{

#if UseLightmapDirectionality == 1
    vec3 vTangentSpaceLightVector;

    vTangentSpaceLightVector.xy = UnpackFromColor(irradianceDirection.xy);

    float sinTheta = dot(vTangentSpaceLightVector.xy, vTangentSpaceLightVector.xy);

#if LightmapGameVersionNumber == 1
    // Error in HLA code, fixed in DeskJob
    float cosTheta = 1.0 - sqrt(sinTheta);
#else
    vTangentSpaceLightVector *= (colorSpaceMul / max(colorSpaceMul, length(vTangentSpaceLightVector.xy)));

    float cosTheta = sqrt(1.0 - sinTheta);
#endif
    vTangentSpaceLightVector.z = cosTheta;

    float flDirectionality = mix(irradianceDirection.z, 1.0, g_flDirectionalLightmapStrength);
    vec3 vNonDirectionalLightmap = irradianceColor * saturate(flDirectionality + g_vLightmapParams.x);

    float NoL = ClampToPositive(dot(vTangentSpaceLightVector, normalMap));

    float LightmapZ = max(vTangentSpaceLightVector.z, g_flDirectionalLightmapMinZ);

    irradianceColor = (NoL * (irradianceColor - vNonDirectionalLightmap) / LightmapZ) + vNonDirectionalLightmap;
#endif

    return irradianceColor;
}

#endif


void CalculateIndirectLighting(inout LightingTerms_t lighting, inout MaterialProperties_t mat)
{
    lighting.DiffuseIndirect = vec3(0.3);

    // Indirect Lighting
#if (D_BAKED_LIGHTING_FROM_LIGHTMAP == 1)
    vec3 irradiance = texture(g_tIrradiance, vLightmapUVScaled).rgb;
    vec4 vAHDData = texture(g_tDirectionalIrradiance, vLightmapUVScaled);

    lighting.DiffuseIndirect = ComputeLightmapShading(irradiance, vAHDData, mat.NormalMap);

    lighting.SpecularOcclusion = vAHDData.a;

#elif (D_BAKED_LIGHTING_FROM_VERTEX_STREAM == 1)
    lighting.DiffuseIndirect = vPerVertexLightingOut.rgb;
#elif (D_BAKED_LIGHTING_FROM_PROBE == 1)
    #if (LightmapGameVersionNumber == 0 || LightmapGameVersionNumber == 1)
        vec3 vIndirectSampleCoords = GetProbe(mat.PositionWS).Position;
        vIndirectSampleCoords.z /= 6;
        // clamp(vIndirectSampleCoords, probe.Min, probe.Max);
    #elif (LightmapGameVersionNumber == 2)
        vec3 vIndirectSampleCoords = CalculateAtlasProbeIndirectCoords(mat.PositionWS);
    #endif

    // Take up to 3 samples along the normal direction
    vec3 vDepthSliceOffsets = mix(vec3(0, 1, 2) / 6.0, vec3(3, 4, 5) / 6.0, step(mat.AmbientNormal, vec3(0.0)));
    vec3 vAmbient[3];

    vec3 vNormalSquared = pow2(mat.AmbientNormal);

    lighting.DiffuseIndirect = vec3(0.0);

    for (int i = 0; i < 3; i++)
    {
        vAmbient[i] = textureLod(g_tLPV_Irradiance, vIndirectSampleCoords + vec3(0, 0, vDepthSliceOffsets[i]), 0.0).rgb;
        lighting.DiffuseIndirect += vAmbient[i] * vNormalSquared[i];
    }

    //if (sin(g_flTime * 3) > 0)
    //    lighting.DiffuseIndirect = vec3(0.3);

#else
    #define NoBakeLighting 1
#endif

    // Environment Maps
#if defined(S_SPECULAR) && (S_SPECULAR == 1)
    vec3 ambientDiffuse;
    float normalizationTerm = GetEnvMapNormalization(GetIsoRoughness(mat.Roughness), mat.AmbientNormal, lighting.DiffuseIndirect);

    lighting.SpecularIndirect = GetEnvironment(mat) * normalizationTerm;
#endif
}


uniform float g_flAmbientOcclusionDirectDiffuse = 1.0;
uniform float g_flAmbientOcclusionDirectSpecular = 1.0;

// AO Proxies would be merged here
void ApplyAmbientOcclusion(inout LightingTerms_t o, MaterialProperties_t mat)
{
#if defined(DIFFUSE_AO_COLOR_BLEED)
    SetDiffuseColorBleed(mat);
#endif

    // In non-lightmap shaders, SpecularAO always does a min(1.0, specularAO) in the same place where lightmap
    // shaders does min(bakedAO, specularAO). That means that bakedAO exists and is a constant 1.0 in those shaders!
    mat.SpecularAO = min(o.SpecularOcclusion, mat.SpecularAO);

    vec3 DirectAODiffuse = mix(vec3(1.0), mat.DiffuseAO, g_flAmbientOcclusionDirectDiffuse);
    float DirectAOSpecular = mix(1.0, mat.SpecularAO, g_flAmbientOcclusionDirectSpecular);

    o.DiffuseDirect *= DirectAODiffuse;
    o.DiffuseIndirect *= mat.DiffuseAO;
    o.SpecularDirect *= DirectAOSpecular;
    o.SpecularIndirect *= mat.SpecularAO;
}
