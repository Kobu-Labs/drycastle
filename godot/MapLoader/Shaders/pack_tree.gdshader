shader_type spatial;
// One material of the pack's trees (materials.json: albedo texture and colour, alpha scissor, cull mode, roughness), drawn from a tile mesh that holds
// many trees. UV2 holds the base point of the tree a vertex belongs to (PackTileMesher). A tree whose base is beyond the radius is dropped whole
// (its vertices collapse into one point); in the fade band each tree swaps whole against its baked twin, at a distance of its own (no per-pixel dither). MapLoader.PackTrees.cs replaces CULL_MODE.
// COLOR (PackTileMesher.KindAlpha): rgb = the tree's foliage tint halved, a = what it is: 1 tree, 0.5 ground object (cut like a tree, no tint), 0 undergrowth
// (own radius, tree_cut_under), 0.25 far undergrowth (saplings, shrubs: radius tree_cut_far.z). Undergrowth has nothing behind it, so each piece grows out of
// the ground at a distance of its own in the band (tree_grow). 0.875 a tree's card, 0.375 a ground object's card (#362): drawn where the pack model is not and
// the cards reach; on a card NORMAL is the model's X axis in the world and normal_tex holds the model's normals (model frame).
render_mode diffuse_burley, specular_schlick_ggx, CULL_MODE;

// @include tree_cut

uniform sampler2D albedo_tex : source_color, filter_linear_mipmap, repeat_enable;
uniform vec4 albedo_colour : source_color = vec4(1.0);
uniform float scissor = -1.0;   // alpha scissor threshold; negative: opaque
uniform float roughness = 0.8;
uniform float metallic = 0.0;
uniform float specular = 0.5;
// Foliage colour (pack_trees.json colour): saturation and brightness of the green texels, and how green a texel must be to count as foliage.
uniform float foliage_saturation = 1.0;
uniform float foliage_gain = 1.0;
uniform float leaf_lo = 0.01;
uniform float leaf_hi = 0.08;
// Foliage takes the colour in the vertex colour (the baked tree's own green), shaded by the texel's brightness against leaf_ref.
uniform float recolour = 1.0;
uniform float leaf_ref = 0.2;
uniform float shade_min = 0.75;
uniform float shade_max = 1.25;

// Cards only: the normals of the pictures in albedo_tex, in the model's frame (xyz * 0.5 + 0.5).
uniform sampler2D normal_tex : filter_linear_mipmap, repeat_disable;
// Undergrowth: the share of the fade band over which a piece grows, and how deep under the ground it starts (m).
uniform float under_grow = 0.35;
uniform float under_sink = 0.5;

varying flat vec3 tint;
varying flat float tinted;
varying flat vec4 card; // xyz: the model's X axis in the world, w: 1 on a card

void vertex() {
	float kind = COLOR.a;
	bool object_card = abs(kind - 0.375) < 0.06;
	bool is_card = object_card || abs(kind - 0.875) < 0.06;
	card = vec4(0.0);
	if (kind < 0.3) {
		vec4 cut = kind > 0.125 ? vec4(tree_cut.xy, tree_cut_far.zw) : tree_cut_under;
		float g = tree_grow(tree_far(UV2, VERTEX.y, MODEL_MATRIX, cut), under_grow);
		if (g <= 0.0) {
			VERTEX = vec3(0.0);
		} else {
			VERTEX.xz = mix(UV2, VERTEX.xz, g);
			VERTEX.y -= (1.0 - g) * under_sink;
		}
	} else if (is_card) {
		bool pack_gone = tree_is_baked(tree_far(UV2, VERTEX.y, MODEL_MATRIX, tree_cut));
		bool card_gone = tree_is_baked(tree_far(UV2, VERTEX.y, MODEL_MATRIX, tree_card_cut()));
		if (!pack_gone || card_gone) {
			VERTEX = vec3(0.0);
		}
		card = vec4(NORMAL, 1.0); // the tile is not turned, so the mesh's frame is the world's
		NORMAL = vec3(0.0, 1.0, 0.0);
	} else if (tree_is_baked(tree_far(UV2, VERTEX.y, MODEL_MATRIX, tree_cut))) {
		VERTEX = vec3(0.0);
	}
	tint = COLOR.rgb * 2.0;
	if (recolour > 0.5) {
		// the colour is the baked tree's, in sRGB bytes: to linear exactly as props_tree_cut does for the baked mesh, or the two would not match
		tint = mix(pow((tint + vec3(0.055)) * (1.0 / (1.0 + 0.055)), vec3(2.4)), tint * (1.0 / 12.92), lessThan(tint, vec3(0.04045)));
	}
	tinted = (abs(kind - 0.5) < 0.06 || object_card) ? 0.0 : 1.0; // ground objects keep the atlas as it is
}

void fragment() {
	vec4 c = texture(albedo_tex, UV) * albedo_colour;
	if (scissor >= 0.0 && c.a < scissor) {
		discard;
	}
	// foliage only: by how much greener than red the texel is (the atlas' leaves run from olive to blue-grey), so brown trunks and bark keep their colour
	float leaf = tinted * smoothstep(leaf_lo, leaf_hi, (c.g - c.r) / max(c.g, 0.01));
	vec3 lum = vec3(0.2126, 0.7152, 0.0722);
	vec3 varied = recolour > 0.5 ? tint * clamp(dot(c.rgb, lum) / leaf_ref, shade_min, shade_max) : c.rgb * tint;
	float luma = dot(varied, lum);
	varied = mix(vec3(luma), varied, foliage_saturation) * foliage_gain;
	ALBEDO = mix(c.rgb, varied, leaf);
	if (card.w > 0.5) {
		// the picture's normal, from the model's frame to the world (X axis from the vertex, Y up, Z across) and on to the view
		vec3 n = texture(normal_tex, UV).xyz * 2.0 - 1.0;
		if (dot(n, n) > 0.01) {
			vec3 world_n = n.x * card.xyz + vec3(0.0, n.y, 0.0) + n.z * vec3(-card.z, 0.0, card.x);
			vec3 view_n = normalize((VIEW_MATRIX * vec4(world_n, 0.0)).xyz);
			// a card seen from behind shows the picture mirrored: mirror its normals too, so the lit side stays the sun's side
			vec3 face = normalize(cross(dFdx(VERTEX), dFdy(VERTEX)));
			if (dot(face, VERTEX) > 0.0) {
				face = -face;
			}
			float toward = dot(view_n, face);
			if (toward < 0.0) {
				view_n -= 2.0 * toward * face;
			}
			NORMAL = view_n;
		}
	}
	ROUGHNESS = roughness;
	METALLIC = metallic;
	SPECULAR = specular;
}
