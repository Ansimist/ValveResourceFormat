#version 460

#include "common/ViewConstants.glsl"

#define D_ANIMATED 0 // should also set bAnimated uniform
#define D_MORPHED 0 // should also set F_MORPH_SUPPORTED define

layout (location = 0) in vec3 vPOSITION;
#include "common/animation.glsl"
#include "common/morph.glsl"
#include "common/instancing.glsl"


void main()
{
    mat4 vertexTransform = CalculateObjectToWorldMatrix();
    vec3 vertexPosition = vPOSITION;

    #if (D_ANIMATED == 1)
        vertexTransform *= getSkinMatrix();
    #endif

    #if (D_MORPHED == 1)
        vertexPosition += getMorphOffset();
    #endif

    vec4 fragPosition = vertexTransform * vec4(vertexPosition, 1.0);
    gl_Position = g_matViewToProjection * fragPosition;
}
