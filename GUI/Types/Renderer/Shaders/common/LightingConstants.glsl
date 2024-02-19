#version 460

#define MAX_ENVMAPS 144

struct LightProbeVolumeData
{
    mat4 WorldToLocalNormalizer;
    vec4 Min;
    vec4 Max;
    vec4 AtlasScale;
    vec4 AtlasOffset;
};

layout(std140, binding = 1) uniform LightingConstants {
    vec4 g_vLightmapUvScale;
    mat4 vLightPosition;
    vec4 vLightColor;
    vec4 g_vEnvMapSizeConstants;
    mat4 g_matEnvMapWorldToLocal[MAX_ENVMAPS];
    vec4[MAX_ENVMAPS] g_vEnvMapBoxMins;
    vec4[MAX_ENVMAPS] g_vEnvMapBoxMaxs;
    vec4[MAX_ENVMAPS] g_vEnvMapEdgeFadeDistsInv;
    vec4[MAX_ENVMAPS] g_vEnvMapProxySphere;
    vec4[MAX_ENVMAPS] g_vEnvMapColorRotated;
    vec4[MAX_ENVMAPS] g_vEnvMapNormalizationSH;
    LightProbeVolumeData[MAX_ENVMAPS] g_vLightProbeVolumeData;
};
