// The modern look's distant terrain (HorizonTerrain.cs): horizon_terrain.gdshaderinc, with the ring's own depth and masks.
shader_type spatial;
render_mode cull_back, specular_disabled, diffuse_lambert;
#define HORIZON_RING
#include "res://Shaders/horizon_terrain.gdshaderinc"
