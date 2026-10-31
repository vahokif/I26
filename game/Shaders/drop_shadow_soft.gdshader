// The modern look's drop shadow for cars and props: the 1997 projection (drop_shadow_1997.gdshader), drawn
// several times along lights jittered over a disc, each a faint layer, so the edge feathers by the height of
// the caster above the ground (hard where it touches, softer as it rises: contact hardening for free), the
// offsets capped in metres (max_offset) so that a caster far above the ground (an airborne car, a low sun's long
// ray) still gives one overlapping shadow, not a ghost per layer; it grows fainter with height instead. A stencil
// value per layer (its own `ref`) lets a layer cover a pixel once, so overlapping faces of one caster do not
// double up. i76_light_dir points toward the light; the plane is the instance's.
shader_type spatial;
render_mode depth_draw_never, cull_disabled, fog_disabled, blend_mix, ambient_light_disabled, specular_disabled;
stencil_mode read, write, compare_not_equal, 1;

global uniform vec3 i76_light_dir;
uniform vec3 shadow_colour : source_color = vec3(0.02, 0.025, 0.04);
uniform vec2 jitter = vec2(0.0);      // this layer's offset in the disc, in units of spread
uniform float spread = 0.1;           // the disc's radius, as a tangent of angle
uniform float max_offset = 0.3;       // the disc's largest radius on the ground, in metres
uniform float height_fade = 7.0;      // the height at which the shadow has lost most of its darkness
uniform float layer_alpha = 0.1;
uniform float fade_from = 90.0;
uniform float fade_to = 130.0;
instance uniform float use_ground = 0.0;   // 1: CUSTOM0 is each vertex's own shadow point on the terrain (xyz) and its height above it (w)
instance uniform float all_faces = 0.0;   // 1: a car also casts its faces turned from the light (below): its meshes are open (a pickup's bed has a floor and no underside), so a car tilted off the light would shine through them
instance uniform vec4 plane = vec4(0.0, 1.0, 0.0, 0.0);   // n·p = w, in world space

varying float v_facing;
varying float v_skip;
varying float v_depth;
varying float v_height;
varying float v_clip;
varying vec3 v_plane_n;

void vertex() {
	vec3 w = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	vec3 n = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
	vec3 l0 = normalize(i76_light_dir);
	v_facing = dot(n, l0);
	// an upward face lying on the ground (a lot's asphalt, in a mesh with the building on it) would project onto
	// itself, coplanar, and fight the ground it is part of: it casts nothing
	v_skip = 0.0;
	if (dot(n, plane.xyz) > 0.7 && abs(dot(plane.xyz, w) - plane.w) < 0.6) v_skip = 1.0;
	vec3 t1 = normalize(cross(l0, abs(l0.y) > 0.9 ? vec3(1, 0, 0) : vec3(0, 1, 0)));
	vec3 t2 = cross(l0, t1);
	vec3 l = normalize(l0 + (t1 * jitter.x + t2 * jitter.y) * spread);
	float t = (dot(plane.xyz, w) - plane.w) / max(dot(plane.xyz, l), 0.05);
	vec3 p0 = w - l0 * ((dot(plane.xyz, w) - plane.w) / max(dot(plane.xyz, l0), 0.05));
	vec3 base = use_ground > 0.5 ? CUSTOM0.xyz : p0;
	vec3 p = w - l * t;
	vec3 off = p - p0;
	float cap = max_offset * length(jitter);
	float ol = length(off);
	if (ol > cap) off *= cap / ol;
	p = base + off + plane.xyz * 0.12;
	v_height = dot(plane.xyz, w) - plane.w;   // signed: the part of a face below the ground is clipped in fragment()
	v_clip = use_ground > 0.5 ? CUSTOM0.w : v_height;
	vec4 v = VIEW_MATRIX * vec4(p, 1.0);
	v_depth = -v.z;
	v_plane_n = (VIEW_MATRIX * vec4(plane.xyz, 0.0)).xyz;
	POSITION = PROJECTION_MATRIX * v;
}

void fragment() {
	if (v_skip > 0.5 || (v_facing < 0.0 && all_faces < 0.5)) discard;
	// what lies under the ground (a ramp's or a bridge abutment's walls, sunk into the terrain built up around it)
	// is not between the sun and the ground: its projection is a shadow of nothing. Clipped exactly, as the height
	// varies linearly across a face.
	if (v_clip < 0.05) discard;
	// lit by nothing of its own: the colour is emission, and the sun's own light() below takes the opacity away
	// where the terrain's shadow map already darkens this ground, so the two never stack
	NORMAL = normalize(v_plane_n);
	ALBEDO = vec3(0.0);
	EMISSION = shadow_colour;
	ALPHA = layer_alpha * (1.0 - smoothstep(fade_from, fade_to, v_depth)) * (1.0 - 0.7 * smoothstep(0.0, height_fade, max(v_height, 0.0)));
}

void light() {
	// the sun's shadow map (ATTENUATION: hence no shadows_disabled, which holds it at 1) and the ground turned from the
	// sun (the plane's normal; a back-lit slope has no sunlight to take away)
	if (LIGHT_IS_DIRECTIONAL) ALPHA *= ATTENUATION * smoothstep(0.0, 0.2, dot(NORMAL, LIGHT));
}
