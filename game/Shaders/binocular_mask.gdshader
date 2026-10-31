// The binoculars' two-circle frame (bmask*.msk): black where the mask is set, over the whole view.
// The mask is the original's 4:3 screen, fitted to the window's height and centred; wider windows are black at the
// sides. The 1997 look samples the 640x480 mask's pixels; the modern look a softened 800x600 one, cut at its middle
// with a pixel of antialiasing.
shader_type canvas_item;

uniform sampler2D mask_nearest : filter_nearest;
uniform sampler2D mask_soft : filter_linear;
uniform bool faithful = false;
uniform vec2 screen_size = vec2(640.0, 480.0);

void fragment() {
	vec2 px = UV * screen_size;
	float w43 = screen_size.y * 4.0 / 3.0;
	vec2 m = vec2((px.x - (screen_size.x - w43) * 0.5) / w43, px.y / screen_size.y);
	float a;
	if (m.x < 0.0 || m.x > 1.0) {
		a = 1.0;
	} else if (faithful) {
		a = texture(mask_nearest, m).r;
	} else {
		float v = texture(mask_soft, m).r;
		float fw = max(fwidth(v), 1e-4);
		a = smoothstep(0.5 - fw, 0.5 + fw, v);
	}
	COLOR = vec4(0.0, 0.0, 0.0, a);
}
