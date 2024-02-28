#version 460
//? #include "features.glsl"
//? #include "utils.glsl"
//? #include "LightingConstants.glsl"
//? #include "lighting_common.glsl"
//? #include "texturing.glsl"
//? #include "pbr.glsl"

#define SCENE_PROBE_TYPE 0 // 1 = Individual, 2 = Atlas

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
#elif (D_BAKED_LIGHTING_FROM_PROBE == 1)

    uniform sampler3D g_tLPV_Irradiance;

    #if (LightmapGameVersionNumber == 1)
        uniform sampler3D g_tLPV_Indices;
        uniform sampler3D g_tLPV_Scalars;
    #elif (LightmapGameVersionNumber == 2)
        uniform sampler3D g_tLPV_Shadows;
    #endif

    layout(std140, binding = 2) uniform LightProbeVolume
    {
        uniform mat4 g_matLightProbeVolumeWorldToLocal;
        #if (SCENE_PROBE_TYPE == 1)
            vec4 g_vLightProbeVolumeLayer0TextureMin;
            vec4 g_vLightProbeVolumeLayer0TextureMax;
        #elif (SCENE_PROBE_TYPE == 2)
            vec4 g_vLightProbeVolumeBorderMin;
            vec4 g_vLightProbeVolumeBorderMax;
            vec4 g_vLightProbeVolumeAtlasScale;
            vec4 g_vLightProbeVolumeAtlasOffset;
        #endif
    };

    vec3 CalculateProbeSampleCoords(vec3 fragPosition)
    {
        vec3 vLightProbeLocalPos = mat4x3(g_matLightProbeVolumeWorldToLocal) * vec4(fragPosition, 1.0);
        return vLightProbeLocalPos;
    }

    vec3 CalculateProbeShadowCoords(vec3 fragPosition)
    {
        vec3 vLightProbeLocalPos = CalculateProbeSampleCoords(fragPosition);

        #if (SCENE_PROBE_TYPE == 2)
            vLightProbeLocalPos = fma(saturate(vLightProbeLocalPos), g_vLightProbeVolumeAtlasScale.xyz, g_vLightProbeVolumeAtlasOffset.xyz);
        #endif

        return vLightProbeLocalPos;
    }

    vec3 CalculateProbeIndirectCoords(vec3 fragPosition)
    {
        vec3 indirectCoords = CalculateProbeSampleCoords(fragPosition);

        #if (SCENE_PROBE_TYPE == 1)
            indirectCoords.z /= 6;
            // clamp(indirectCoords, g_vLightProbeVolumeLayer0TextureMin.xyz, g_vLightProbeVolumeLayer0TextureMax.xyz);
        #elif (SCENE_PROBE_TYPE == 2)
            indirectCoords.z /= 6;
            indirectCoords = clamp(indirectCoords, g_vLightProbeVolumeBorderMin.xyz, g_vLightProbeVolumeBorderMax.xyz);

            indirectCoords.z *= 6;
            indirectCoords = fma(indirectCoords, g_vLightProbeVolumeAtlasScale.xyz, g_vLightProbeVolumeAtlasOffset.xyz);

            indirectCoords.z /= 6;
        #endif

        return indirectCoords;
    }
#elif (D_BAKED_LIGHTING_FROM_VERTEX_STREAM == 1)
    in vec3 vPerVertexLightingOut;
#endif

uniform sampler2DShadow g_tShadowDepthBufferDepth;

float CalculateSunShadowMapVisibility(vec3 vPosition, vec3 direction)
{
    vec4 projCoords = g_matWorldToShadow * vec4(vPosition, 1.0);
    projCoords.xyz /= projCoords.w;

    vec2 shadowCoords = clamp(projCoords.xy * 0.5 + 0.5, vec2(-1), vec2(2));
    float currentDepth = saturate(projCoords.z + 0.001);

    // To skip PCF
    // return 1 - textureLod(g_tShadowDepthBufferDepth, vec3(shadowCoords, currentDepth), 0).r;

    float shadow = 0.0;
    vec2 texelSize = 1.0 / textureSize(g_tShadowDepthBufferDepth, 0);
    for(int x = -1; x <= 1; ++x)
    {
        for(int y = -1; y <= 1; ++y)
        {
            float pcfDepth = textureLod(g_tShadowDepthBufferDepth, vec3(shadowCoords + vec2(x, y) * texelSize, currentDepth), 0).r;
            shadow += pcfDepth;
        }
    }

    shadow /= 9.0;
    return 1 - shadow;
}

struct Light
{
    // The color is an RGB value in the linear sRGB color space.
    vec3 Color;

    // The normalized light vector, in world space (direction from the
    // current fragment's position to the light).
    vec3 Direction;

    // The position of the light in world space. This value is the same as
    // Direction for directional lights.
    vec3 Position;

    // Attenuation of the light based on the distance from the current
    // fragment to the light in world space. This value between 0.0 and 1.0
    // is computed differently for each type of light (it's always 1.0 for
    // directional lights).
    float Attenuation;

    // Visibility factor computed from shadow maps or other occlusion data
    // specific to the light being evaluated. This value is between 0.0 and
    // 1.0.
    float Visibility;
};

/*
struct StaticLight
{
    Light Light;
    uint LocalIndex;
    uint GlobalIndex;
};
*/

vec3 GetEnvLightDirection(uint nLightIndex)
{
    //mat4 lightToWorld = g_matLightToWorld[nLightIndex];
    //return lightToWorld[2].xyz;
    return normalize(mat3(g_matLightToWorld[nLightIndex]) * vec3(-1, 0, 0));
}

vec3 GetLightPositionWs(uint nLightIndex)
{
    // directional light
    if (g_vLightPosition_Type[nLightIndex].a == 0.0)
    {
        //return GetEnvLightDirection(nLightIndex);
    }

    return g_vLightPosition_Type[nLightIndex].xyz;
}

bool IsLightDirectional(uint nLightIndex)
{
    return g_vLightPosition_Type[nLightIndex].a == 0.0;
}

vec3 GetLightDirection(vec3 vPositionWs, uint nLightIndex)
{
    if (IsLightDirectional(nLightIndex))
    {
        return GetEnvLightDirection(nLightIndex);
    }

    vec3 lightPosition = GetLightPositionWs(nLightIndex);
    vec3 lightVector = normalize(lightPosition - vPositionWs);

    return lightVector;
}

float GetLightRangeInverse(uint nLightIndex)
{
    return g_vLightDirection_InvRange[nLightIndex].a;
}

vec3 GetLightColor(uint nLightIndex)
{
    vec3 vColor = g_vLightColor_Brightness[nLightIndex].rgb;
    float flBrightness = g_vLightColor_Brightness[nLightIndex].a;

    return SrgbGammaToLinear(vColor) * flBrightness;
}

Light GetLight(float flStrength, float flVisibility)
{
    Light light;

    light.Attenuation = pow2(flStrength);
    light.Visibility = flVisibility;

    return light;
}

#if (D_BAKED_LIGHTING_FROM_LIGHTMAP == 1)

    #if (LightmapGameVersionNumber == 1)

        ivec4 GetLightmappedLightIndices()
        {
            vec4 vLightIndexFloats = texture(g_tDirectLightIndices, vLightmapUVScaled).rgba;

            ivec4 vLightIndices = ivec4( vLightIndexFloats.xyzw * 255 );
            return vLightIndices;
        }

        vec4 GetLightmappedLightStrengths()
        {
            return texture(g_tDirectLightStrengths, vLightmapUVScaled).rgba;
        }

    #elif (LightmapGameVersionNumber == 2)

        vec4 GetLightmappedLightShadow()
        {
            return texture(g_tDirectLightShadows, vLightmapUVScaled).rgba;
        }

    #endif

    Light GetLightmappedLight(vec3 vPositionWs, uint nLocalIndex)
    {
        Light light = GetLight(1.0, 0.0);
        uint nGlobalIndex = 0;

        float flStrength = 1.0;
        float flVisibility = 1.0;

        #if (LightmapGameVersionNumber == 1)
            ivec4 vLightIndices = GetLightmappedLightIndices();
            vec4 vLightStrengths = GetLightmappedLightStrengths();

            flStrength = vLightStrengths[nLocalIndex];
            nGlobalIndex = uint(vLightIndices[nLocalIndex]);

        #elif (LightmapGameVersionNumber == 2)
            vec4 vLightShadow = GetLightmappedLightShadow();

            flVisibility = 1.0 - vLightShadow[nLocalIndex];

            uint nLightBufferOffset = nLocalIndex == 0 ? 0 : uint(g_nNumLights[nLocalIndex - 1]);
            uint nLightBufferCount = uint(g_nNumLights[nLocalIndex]);

            nGlobalIndex = nLightBufferOffset;
            //if (nLightBufferCount == 0) return light;

            for(uint globalIndex = nLightBufferOffset; globalIndex < nLightBufferCount; ++globalIndex)
            {
                //nGlobalIndex = int(globalIndex);
                break;
                //light.Light = GetLight(flStrength, flVisibility);
                // some idea
            }

        #endif

        //if (nGlobalIndex != 0) return light; // todo: impement lights other than sun

        light.Color = GetLightColor(nGlobalIndex);
        light.Direction = GetLightDirection(vPositionWs, nGlobalIndex);
        light.Position = GetLightPositionWs(nGlobalIndex);

        light.Visibility = flVisibility;
        return light;
    }

#endif


Light GetStaticLight(vec3 vPositionWs, uint nLocalIndex)
{
    #if (D_BAKED_LIGHTING_FROM_LIGHTMAP == 1)
        return GetLightmappedLight(vPositionWs, nLocalIndex);
    #elif (D_BAKED_LIGHTING_FROM_LIGHTPROBE == 1)
        return GetLight(1.0, 1.0);
    #elif (D_BAKED_LIGHTING_FROM_VERTEX_STREAM == 1)
        return GetLight(1.0, 1.0);
    #endif

    // todo: empty light.
    return GetLight(1.0, 0.2);;
}

/*
vec3 CalcBaseLight(int layer, vec3 normals, BaseLight base, float attenuation, bool global)
{
    //return CalcLight(layer, normals, base.Position, base.Color.rgb * base.Intensity, attenuation, global);
}

vec3 CalcPointLight(int layer, vec3 normals, Light light)
{
    float distanceToLight = length(light.Base.Position - vPositionWs);
    float attenuation = 1.0 / (1.0 + light.Linear * distanceToLight + light.Quadratic * pow(distanceToLight, 2));
    return CalcBaseLight(layer, normals, light.Base, attenuation, true);
}

float CalcSpotLightAtten(vec3 vPositionWs, Light light)
{
    vec3 v = normalize(light.Position - vPositionWs);
    float inner = cos(radians(light.InnerConeAngle));
    float outer = cos(radians(light.OuterConeAngle));

    float distanceToLight = length(light.Base.Position - vPositionWs);
    float theta = dot(v, normalize(-vec3(0, -1, 0)));
    float epsilon = inner - outer;
    float attenuation = 1.0 / (1.0 + light.Attenuation * pow(distanceToLight, 2));
    light.Base.Intensity *= smoothstep(0.0, 1.0, (theta - outer) / epsilon);

    if(theta > outer)
    {
        return attenuation;
    }
    else
    {
        return 0.0;
    }
}
*/

// https://lisyarus.github.io/blog/graphics/2022/07/30/point-light-attenuation.html
float attenuate_cusp(float s, float falloff)
{
    if (s >= 1.0)
        return 0.0;

    float s2 = pow2(s);

    //return 1.0;
    return pow2(1 - s2) / (1 + falloff * s);
}

#define CalculateDirectLighting CalculateDirectLightingNew

void CalculateDirectLightingNew(inout LightingTerms_t lighting, inout MaterialProperties_t mat)
{
    vec4 dlsh = vec4(1, 0, 0, 0);

    #if (D_BAKED_LIGHTING_FROM_LIGHTMAP == 1)
        dlsh = texture(g_tDirectLightShadows, vLightmapUVScaled);
    #elif (D_BAKED_LIGHTING_FROM_PROBE == 1)
        vec3 vLightProbeShadowCoords = CalculateProbeShadowCoords(mat.PositionWS);
        dlsh = textureLod(g_tLPV_Shadows, vLightProbeShadowCoords, 0.0);
    #endif

    const uint StaticLightCount = 4;
    for(uint uLightIndex = 0; uLightIndex < StaticLightCount; ++uLightIndex)
    {
        float visibility = 1.0 - dlsh[uLightIndex];
        if (visibility > 0.0001)
        {
            vec3 lightVector = GetLightDirection(mat.PositionWS, uLightIndex);

            if (IsLightDirectional(uLightIndex))
            {
                visibility *= CalculateSunShadowMapVisibility(mat.PositionWS, lightVector);
            }
            else
            {
                float flInvRange = GetLightRangeInverse(uLightIndex) * 0.5;
                float flDistance = length(GetLightPositionWs(uLightIndex) - mat.PositionWS);
                float flFallOff = 0.12;
                visibility *= attenuate_cusp(flDistance * flInvRange, flFallOff);
            }

            CalculateShading(lighting, lightVector, visibility * GetLightColor(0), mat);
        }
    }
}

// This should contain our direct lighting loop
void CalculateDirectLightingOld(inout LightingTerms_t lighting, inout MaterialProperties_t mat)
{
    vec3 lightVector = GetEnvLightDirection(0);
    //const uint StaticLightCount = 4;
    //for(uint uLightIndex = 0; uLightIndex < StaticLightCount; ++uLightIndex)

    vec3 vPositionWs = mat.PositionWS;
    float visibility = 1.0;

    #if (LightmapGameVersionNumber == 1)
        #if (D_BAKED_LIGHTING_FROM_LIGHTMAP == 1)
            vec4 dls = texture(g_tDirectLightStrengths, vLightmapUVScaled);
            vec4 dli = texture(g_tDirectLightIndices, vLightmapUVScaled);
        #elif (D_BAKED_LIGHTING_FROM_PROBE == 1)
            vec3 vLightProbeShadowCoords = CalculateProbeShadowCoords(mat.PositionWS);
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
            vec3 vLightProbeShadowCoords = CalculateProbeShadowCoords(mat.PositionWS);
            vec4 dlsh = textureLod(g_tLPV_Shadows, vLightProbeShadowCoords, 0.0);
        #else
            vec4 dlsh = vec4(1, 0, 0, 0);
        #endif

        int index = 0;
        visibility = 1.0 - dlsh[index];
    #endif

    if (visibility > 0.0001)
    {
        visibility *= CalculateSunShadowMapVisibility(mat.PositionWS, lightVector);

        CalculateShading(lighting, lightVector, visibility * GetLightColor(0), mat);
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

#elif (D_BAKED_LIGHTING_FROM_PROBE == 1)
    vec3 vIndirectSampleCoords = CalculateProbeIndirectCoords(mat.PositionWS);

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

#elif (D_BAKED_LIGHTING_FROM_VERTEX_STREAM == 1)
    lighting.DiffuseIndirect = vPerVertexLightingOut.rgb;
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
