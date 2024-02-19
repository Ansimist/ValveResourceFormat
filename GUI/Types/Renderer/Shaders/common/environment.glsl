#version 460
//? #include "features.glsl"
//? #include "utils.glsl"
//? #include "texturing.glsl"
//? #include "LightingConstants.glsl"
//? #include "lighting_common.glsl"
//? #include "pbr.glsl"
//? #include "instancing.glsl"
//? #include "lighting.glsl"

#define SCENE_CUBEMAP_TYPE 0 // 0 = None, 1 = Per-batch cube map, 2 = Per-scene cube map array

#if (SCENE_CUBEMAP_TYPE == 0)
    // ...
#elif (SCENE_CUBEMAP_TYPE == 1)
    uniform samplerCube g_tEnvironmentMap;
#elif (SCENE_CUBEMAP_TYPE == 2)
    uniform samplerCubeArray g_tEnvironmentMap;
#endif

layout(std430, binding = 3) buffer g_envmapInstanceBuffer
{
    uint g_nEnvMapIndices[];
};

const int ENVMAP_DATA_STRIDE = 16;
const int ENVMAP_MAX_COUNT = ENVMAP_DATA_STRIDE - 1;

int _GetInstanceEnvmapDataIndex(int nEnvmap)
{
    int indexStride = nEnvmap / 4;
    int indexBitwiseOffset = nEnvmap % 4;

    uint fourIndices = g_nEnvMapIndices[(nEnvMap_LpvIndex.x * (ENVMAP_DATA_STRIDE / 4)) + indexStride];

    return int((fourIndices >> (indexBitwiseOffset * 8)) & 0xff);
}

int GetInstanceEnvmapCount()
{
    return _GetInstanceEnvmapDataIndex(0);
}

int GetInstanceEnvmapDataIndex(int nEnvmap)
{
    return _GetInstanceEnvmapDataIndex(nEnvmap); // Index 0 is the count
}

struct AABB
{
    vec3 Min; vec3 Max;
};

struct EnvMap
{
    AABB Bounds;
    mat4x3 WorldToLocal;
    vec3 ProxySphere;
    bool IsBoxProjection;
    vec3 EdgeFadeDistsInverse;
    int ArrayTextureIndex;

    // Position as seen by the current fragment.
    vec3 LocalPosition;
};

EnvMap GetEnvMap(int dataIndex, vec3 vFragPosition)
{
    EnvMap envMapData;
    envMapData.WorldToLocal = mat4x3(g_matEnvMapWorldToLocal[dataIndex]);
    envMapData.ArrayTextureIndex = int(g_vEnvMapBoxMins[dataIndex].w);
    envMapData.Bounds.Min = g_vEnvMapBoxMins[dataIndex].xyz;
    envMapData.Bounds.Max = g_vEnvMapBoxMaxs[dataIndex].xyz;
    envMapData.ProxySphere = g_vEnvMapProxySphere[dataIndex].xyz;
    envMapData.IsBoxProjection = g_vEnvMapProxySphere[dataIndex].w == 1.0;
    envMapData.EdgeFadeDistsInverse = g_vEnvMapEdgeFadeDistsInv[dataIndex].xyz;

    envMapData.LocalPosition = envMapData.WorldToLocal * vec4(vFragPosition, 1.0);

    return envMapData;
}

vec3 CubemapParallaxCorrection(in EnvMap envMapData, vec3 localReflectionVector)
{
    // https://seblagarde.wordpress.com/2012/09/29/image-based-lighting-approaches-and-parallax-corrected-cubemap/
    // Following is the parallax-correction code
    // Find the ray intersection with box plane
    vec3 FirstPlaneIntersect = (envMapData.Bounds.Min - envMapData.LocalPosition) / localReflectionVector;
    vec3 SecondPlaneIntersect = (envMapData.Bounds.Max - envMapData.LocalPosition) / localReflectionVector;
    // Get the furthest of these intersections along the ray
    // (Ok because x/0 give +inf and -x/0 give -inf )
    vec3 FurthestPlane = max(FirstPlaneIntersect, SecondPlaneIntersect);
    // Find the closest far intersection
    float Distance = abs(min3(FurthestPlane));

    // Get the intersection position
    return normalize(envMapData.LocalPosition + localReflectionVector * Distance);
}


float GetEnvMapLOD(float roughness, vec3 R, float clothMask)
{
    const float EnvMapMipCount = g_vEnvMapSizeConstants.x;

    #if (renderMode_Cubemaps == 1)
        return ((sin(g_flTime) + 1) / 2) * EnvMapMipCount;
    #endif

    #if F_CLOTH_SHADING == 1
        float lod = mix(roughness, pow(roughness, 0.125), clothMask);
        return lod * EnvMapMipCount;
    #endif

    return roughness * EnvMapMipCount;
}


// Cubemap Normalization
#define bUseCubemapNormalization 0
const vec2 CubemapNormalizationParams = vec2(34.44445, -2.44445); // Normalization value in Alyx. Haven't checked the other games

float GetEnvMapNormalization(float rough, vec3 N, vec3 irradiance)
{
    #if (bUseCubemapNormalization == 1 && renderMode_Cubemaps == 0)
        // Cancel out lighting
        // SH is currently fully 1.0, for temporary reasons.
        // We don't know how to get the greyscale SH that they use here, because the radiance coefficients in the cubemap texture are rgb.
        float NormalizationTerm = GetLuma(irradiance);// / dot(vec4(N, 1.0), g_vEnvironmentMapNormalizationSH[EnvMapIndex]);

        // Reduce cancellation on glossier surfaces
        float NormalizationClamp = max(1.0, rough * CubemapNormalizationParams.x + CubemapNormalizationParams.y);
        return min(NormalizationTerm, NormalizationClamp);
    #else
        return 1.0;
    #endif
}


// BRDF
uniform sampler2D g_tBRDFLookup;

vec3 EnvBRDF(vec3 specColor, float rough, vec3 N, vec3 V)
{
    float NdotV = ClampToPositive(dot(N, V));
    vec2 lookupCoords = vec2(NdotV, sqrt(rough));

    vec2 GGXLut = textureLod(g_tBRDFLookup, lookupCoords, 0.0).xy;
    GGXLut = pow2(GGXLut);

    return specColor * GGXLut.x + GGXLut.y;
}

#if (F_CLOTH_SHADING == 1)
    float EnvBRDFCloth(float roughness, vec3 N, vec3 V)
    {
        float NoH = dot(normalize(N + V), N);
        return D_Cloth(roughness, NoH) / 8.0;
    }
#endif


// In CS2, anisotropic cubemaps are default enabled with aniso gloss
#if ((F_ANISOTROPIC_GLOSS == 1) && ((F_SPECULAR_CUBE_MAP_ANISOTROPIC_WARP == 1) || !defined(vr_complex_vfx)))
    vec3 CalculateAnisoCubemapWarpVector(MaterialProperties_t mat)
    {
        // is this like part of the material struct in the og code? it's calculated at the start
        vec2 roughnessOverRoughness = mat.Roughness.xy / mat.Roughness.yx;
        vec3 warpDirection = mix(mat.AnisotropicBitangent, mat.AnisotropicTangent, vec3(step(roughnessOverRoughness.y, roughnessOverRoughness.x))); // in HLA this just uses vertex tangent

        float warpAmount = (1.0 - min(roughnessOverRoughness.x, roughnessOverRoughness.y)) * 0.5;
        vec3 warpedVector = normalize(cross(cross(mat.ViewDir, warpDirection), warpDirection));

        return normalize(mix(mat.AmbientNormal, warpedVector, warpAmount));
    }
#endif

vec3 GetCorrectedSampleCoords(vec3 R, in EnvMap envMapData)
{
    if (!envMapData.IsBoxProjection)
    {
        return R;
    }

    vec3 localReflectionVector = envMapData.WorldToLocal * vec4(R, 0.0);
    return CubemapParallaxCorrection(envMapData, localReflectionVector);
}

vec3 GetEnvironment(MaterialProperties_t mat)
{
    #if (renderMode_Cubemaps == 1)
        vec3 reflectionNormal = mat.GeometricNormal;
    #elif ((F_ANISOTROPIC_GLOSS == 1) && ((F_SPECULAR_CUBE_MAP_ANISOTROPIC_WARP == 1) || !defined(vr_complex_vfx)))
        vec3 reflectionNormal = CalculateAnisoCubemapWarpVector(mat);
    #else
        vec3 reflectionNormal = mat.AmbientNormal;
    #endif

    // Reflection Vector
    vec3 R = normalize(reflect(-mat.ViewDir, reflectionNormal));

    #if (F_ANISOTROPIC_GLOSS == 1)
        float roughness = sqrt(max(mat.Roughness.x, mat.Roughness.y));
    #else
        float roughness = mat.Roughness;
    #endif

    #if (F_CLOTH_SHADING == 1)
        // changed, original was just true
        const bool bIsClothShading = mat.ClothMask > 0.0;
    #else
        const bool bIsClothShading = false;
    #endif

    #if renderMode_Cubemaps == 1
        roughness = 0.0;
    #endif

    vec3 envMap = vec3(0.0);

    const float lod = GetEnvMapLOD(roughness, R, 0.0);

    #if (SCENE_CUBEMAP_TYPE == 0)
        envMap = max(g_vClearColor.rgb, vec3(0.3, 0.1, 0.1));
    #elif (SCENE_CUBEMAP_TYPE == 1 || SCENE_CUBEMAP_TYPE == 2)

        #if (SCENE_CUBEMAP_TYPE == 1)
            const bool bCubemapFadeEnabled = false;
        #else
            const bool bCubemapFadeEnabled = true;
        #endif

        float totalWeight = 0.01;

        int nInstanceEnvMaps = GetInstanceEnvmapCount();
        int nInstanceEnvMapsClamped = min(nInstanceEnvMaps, ENVMAP_MAX_COUNT);

        for (int i = 1; i <= nInstanceEnvMapsClamped; i++)
        {
            int dataIndex = GetInstanceEnvmapDataIndex(i);

            float weight = 1.0;
            EnvMap envMapData = GetEnvMap(dataIndex, vFragPosition);

            // extend bounds a little bit
            envMapData.Bounds.Min -= vec3(0.001);
            envMapData.Bounds.Max += vec3(0.001);

            if (envMapData.IsBoxProjection && bCubemapFadeEnabled) {
                vec3 envmapClampedFadeMax = saturate((envMapData.Bounds.Max - envMapData.LocalPosition) * envMapData.EdgeFadeDistsInverse);
                vec3 envmapClampedFadeMin = saturate((envMapData.LocalPosition - envMapData.Bounds.Min) * envMapData.EdgeFadeDistsInverse);

                float distanceFromEdge = min(min3(envmapClampedFadeMin), min3(envmapClampedFadeMax));

                if (distanceFromEdge == 0.0)
                {
                    continue;
                }

                // blend using a smooth curve
                weight = (pow2(distanceFromEdge) * (3.0 - (2.0 * distanceFromEdge))) * (1.0 - totalWeight);
            }

            totalWeight += weight;

            vec3 coords = GetCorrectedSampleCoords(R, envMapData);
            coords = mix(coords, mat.AmbientNormal, (bIsClothShading) ? sqrt(roughness) : roughness); // blend to fully corrected

            #if (SCENE_CUBEMAP_TYPE == 1)
            {
                envMap = textureLod(g_tEnvironmentMap, coords, lod).rgb; // TODO: upload these cubemaps as cubemap array.
                break;
            }
            #else // SCENE_CUBEMAP_TYPE == 2
            {
                int cubeDepth = envMapData.ArrayTextureIndex;
                envMap += textureLod(g_tEnvironmentMap, vec4(coords, cubeDepth), lod).rgb * weight;

                if (totalWeight > 0.99)
                {
                    break;
                }
            }
            #endif
        }
    #endif // SCENE_CUBEMAP_TYPE == 2

    #if (renderMode_Cubemaps == 1)
        return envMap;
    #endif

    vec3 brdf = EnvBRDF(mat.SpecularColor, GetIsoRoughness(mat.Roughness), mat.AmbientNormal, mat.ViewDir);

    #if (F_CLOTH_SHADING == 1)
        vec3 clothBrdf = vec3(EnvBRDFCloth(GetIsoRoughness(mat.Roughness), mat.AmbientNormal, mat.ViewDir));

        brdf = mix(brdf, clothBrdf, mat.ClothMask);
    #endif

    return brdf * envMap;
}
