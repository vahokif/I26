// The outside ground near the camera (OutsideTerrain.cs): the distant ring's shading (horizon_terrain.gdshaderinc), drawn
// without its depth writes and masks (the GPU rejects hidden fragments early), its parameters copied from the ring's material.
shader_type spatial;
render_mode cull_back, specular_disabled, diffuse_lambert;
#include "res://Shaders/horizon_terrain.gdshaderinc"
