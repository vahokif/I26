// The player's cab as an oriented box (World/CabClip.cs sets it while the cockpit view is the current camera): the world
// effects (plants, smoke, flames) that drift through the car are cut away inside it, so the driver never sees them in
// the cab. i76_cab_inv maps world to the box's frame (centre at the origin); half-extents of zero are off.
// Use: #include this, then CAB_CLIP as the first line of fragment(). The mirror's camera sits in the cab too, so it clips alike.
global uniform mat4 i76_cab_inv;
global uniform vec3 i76_cab_half;

// 0 outside the box, 1 deeper than the soft margin (0.3 m) inside it
float cab_depth(vec3 world) {
	vec3 q = abs((i76_cab_inv * vec4(world, 1.0)).xyz) - i76_cab_half;
	return smoothstep(0.0, 0.3, -max(q.x, max(q.y, q.z)));
}

// the margin thins out by interleaved gradient noise, so the cut has no hard edge for the eye to find
#define CAB_CLIP if (i76_cab_half.x > 0.0) { float cab_d = cab_depth((INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz); if (cab_d > 0.0 && cab_d >= fract(52.9829189 * fract(dot(FRAGCOORD.xy, vec2(0.06711056, 0.00583715))))) discard; }
