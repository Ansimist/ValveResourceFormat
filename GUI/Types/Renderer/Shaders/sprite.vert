#version 460

layout (location = 0) in vec3 vPOSITION;
layout (location = 3) in vec2 vTEXCOORD;

out vec2 vTexCoordOut;

#include "common/instancing.glsl"
#include "common/ViewConstants.glsl"

void main()
{
    vTexCoordOut = vTEXCOORD;
    gl_Position = g_matViewToProjection * CalculateObjectToWorldMatrix() * vec4(vPOSITION, 1.0);
}
