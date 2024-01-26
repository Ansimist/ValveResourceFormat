#version 460

#include "common/ViewConstants.glsl"

layout (location = 0) in vec3 vPOSITION;
#include "common/animation.glsl"
#include "common/instancing.glsl"

layout (location = 0) flat out uint nTransformBufferOffset;

void main(void) {
    gl_Position = g_matViewToProjection * CalculateObjectToWorldMatrix() * getSkinMatrix() * vec4(vPOSITION, 1.0);
    nTransformBufferOffset = sceneObjectId;
}
