namespace Raximod.Generation.Effects
{
    /// <summary>
    /// Stable semantic classification for the effects.adb command language. Keeping this table in
    /// the exporter makes new/unknown commands visible in generated diagnostics instead of relying
    /// on a browser runtime switch that silently drops them.
    /// </summary>
    public static class EffectCommandSemantics
    {
        public sealed record Definition(string Category, string RuntimeSupport);

        private static readonly IReadOnlyDictionary<string, Definition> Definitions = Build();

        public static Definition For(string command) => Definitions.TryGetValue(command, out Definition? value)
            ? value
            : new Definition("unknown", "preserved");

        public static IReadOnlyDictionary<string, Definition> All => Definitions;

        private static IReadOnlyDictionary<string, Definition> Build()
        {
            var result = new Dictionary<string, Definition>(StringComparer.OrdinalIgnoreCase);
            void Add(string category, string support, params string[] commands)
            {
                foreach (string command in commands) result[command] = new Definition(category, support);
            }

            Add("structure", "implemented", "ef_layer", "ef_end");
            Add("graph", "implemented", "ef_effect", "ef_emitter", "ef_emitter_immediate");
            Add("graph", "event-only", "ef_hit_effect", "ef_collision_death");
            Add("scoped-action", "implemented", "ef_effect_on", "ef_effect_off", "ef_trigger_animation");
            Add("scoped-action", "callback", "ef_set_swap_package", "ef_remove_swap_package");
            Add("decal", "partial", "ef_decal");
            Add("light", "partial", "ef_light", "ef_attached_light", "ef_change_light");
            Add("camera", "implemented", "ef_joltcamera");
            Add("audio", "implemented", "ef_audio", "ef_wave_package");
            Add("audio", "partial", "ef_audio_distance", "ef_audio_volume", "ef_audio_priority", "ef_audio_auto_update");
            Add("lifecycle", "implemented", "ef_lifespan", "ef_startdelay", "ef_timescale",
                "ef_fade_in", "ef_fadeoutend", "ef_one_shot");
            Add("lifecycle", "preserved", "ef_expire_anim_complete");
            Add("lifecycle", "preserved", "ef_infinity", "ef_emit_end", "ef_terminate_children",
                "ef_every_other", "ef_always_emit");
            Add("geometry", "implemented", "ef_mesh", "ef_material", "ef_centered",
                "ef_initial_width", "ef_initial_height", "ef_initial_size", "ef_initial_scale",
                "ef_layer_offset", "ef_offset_pos", "ef_initial_orientation", "ef_offset_orientation", "ef_angle",
                "ef_align_world", "ef_world_space", "ef_orient_to_velocity_vector",
                "ef_point_trail");
            Add("geometry", "partial", "ef_point_pair", "ef_point_pair1", "ef_point_pair2",
                "ef_point_pair_change", "ef_point_pair_change1", "ef_point_pair_change2",
                "ef_point_trail_rollv", "ef_point_trail_rolldv", "ef_point_trail_decay_time",
                "ef_point_stream", "ef_point_width", "ef_squash", "ef_point_trail_width_change", "ef_radius");
            Add("motion", "implemented", "ef_force", "ef_forcevector", "ef_gravity", "ef_wind",
                "ef_faccel", "ef_width_change", "ef_height_change", "ef_scale_change",
                "ef_angle_change", "ef_orientation_change", "ef_spin");
            Add("motion", "partial", "ef_spin_up", "ef_spin_down");
            Add("motion", "preserved", "ef_gravity_well", "ef_gravity_well_offset");
            Add("appearance", "implemented", "ef_color", "ef_flicker", "ef_random_alpha", "ef_emit_count");
            Add("appearance", "partial", "ef_framerate", "ef_explicit_framerate", "ef_random_frame");
            Add("appearance", "partial", "ef_color_ramp1", "ef_color_ramp2", "ef_color_ramp3",
                "ef_color_ramp_fixup", "ef_color_ramp_loop", "ef_color_ramp_ping_pong",
                "ef_fade_headon", "ef_fade_side", "ef_fade_distance", "ef_material_yon");
            Add("collision", "implemented", "ef_collision", "ef_bounce_death", "ef_restitution",
                "ef_friction", "ef_raytrace");
            Add("visibility", "implemented", "ef_never_cull");
            Add("control", "preserved", "ef_clamp_ok");
            return result;
        }
    }
}
