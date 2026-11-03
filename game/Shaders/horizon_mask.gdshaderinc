// The distant ring's masks (HorizonTerrain.cs), shared by its modern shading (horizon_terrain.gdshaderinc) and its 1997 shading in the
// map editor (outside_1997.gdshaderinc).
//
// OutsideTerrain draws the outside ground's own samples near the camera: the ring gives way under them. R of near_mask is 1 for each
// 160 m chunk they cover (near_origin its corner, centre-relative game frame, near_cells chunks a side).
uniform sampler2D near_mask : filter_nearest, repeat_disable;
uniform vec2 near_origin = vec2(0.0);
uniform float near_cells = 0.0;      // 0: none
const float NEAR_CHUNK = 160.0;

// R: the lowest playable ground near each 10 m texel of the map's bounds (1e9 off the tiles; HorizonTerrain.FloorGrid): over the
// map the ring is drawn only where it is under that, as a backstop under the terrain's edge, never poking through the ground.
// G: 1 on a tile's outermost texels.
uniform sampler2D map_floor : filter_nearest, repeat_disable;
uniform vec2 map_origin = vec2(0.0);
uniform vec2 map_extent = vec2(0.0);   // 0: no floor

// What the masks make of a fragment of the ring at the given position (centre-relative, game frame): 0 drawn, 1 not drawn (under a chunk near the
// camera, or over the map's ground), 2 drawn at twice its distance (over the outermost 10 m of a tile, which the modern surface can
// pull a few metres in from the tile's edge: a backstop behind the terrain's edge, so that nothing of the ground stands behind it).
int horizon_mask(vec3 g_pos) {
	if (near_cells > 0.0) {
		vec2 cell = (g_pos.xz - near_origin) / NEAR_CHUNK;
		if (cell.x >= 0.0 && cell.y >= 0.0 && cell.x < near_cells && cell.y < near_cells
			&& textureLod(near_mask, (floor(cell) + 0.5) / near_cells, 0.0).r > 0.5) return 1;
	}
	if (map_extent.x > 0.0) {
		vec2 tuv = (g_pos.xz - map_origin) / map_extent;
		if (tuv.x >= 0.0 && tuv.y >= 0.0 && tuv.x < 1.0 && tuv.y < 1.0) {
			vec2 fl = textureLod(map_floor, tuv, 0.0).rg;
			if (g_pos.y > fl.r) return fl.g > 0.5 ? 2 : 1;
		}
	}
	return 0;
}
