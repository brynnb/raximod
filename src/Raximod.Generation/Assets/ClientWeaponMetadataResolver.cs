using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using Raximod.EngineAssets.Databases;

namespace Raximod.Generation.Assets
{
    /// <summary>Extracts ordered client muzzle and vehicle weapon-point bindings without name guessing.</summary>
    public static partial class ClientWeaponMetadataResolver
    {
        /// <summary>
        /// PSForever identifiers need not have the client's spelling (object class 14 is
        /// cannon_dropship_20mm / 20mm_cannon_dropship). Join by the native registry class ID,
        /// never a spelling substitution. Keep the caller's identifier as its wire identity.
        /// </summary>
        public static GameObjectDb.GameObject? NativeWeapon(
            string definition,
            IReadOnlyDictionary<string, GameObjectDb.GameObject> objects,
            IReadOnlyDictionary<string, int> serverClasses)
        {
            objects.TryGetValue(definition, out GameObjectDb.GameObject? named);
            if (!serverClasses.TryGetValue(definition, out int classId)) return named;
            if (named is not null)
            {
                if (named.ClassId != classId)
                    throw new InvalidDataException(
                        $"Weapon '{definition}' has server class {classId}, native class {named.ClassId}");
                return named;
            }
            GameObjectDb.GameObject[] candidates = objects.Values
                .Where(value => value.ClassId == classId).ToArray();
            if (candidates.Length != 1 || !candidates[0].Type.Equals("weapon", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Weapon '{definition}' class {classId} requires one native weapon; found "
                    + string.Join(", ", candidates.Select(value => $"{value.Name} ({value.Type})")));
            return candidates[0];
        }

        /// <summary>Reads the server's explicit ObjectClass integer constants, not inferred aliases.</summary>
        public static IReadOnlyDictionary<string, int> ServerObjectClasses(string source)
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in ObjectClassConstant().Matches(source))
                if (!result.TryAdd(match.Groups[1].Value, int.Parse(match.Groups[2].Value,
                    System.Globalization.CultureInfo.InvariantCulture)))
                    throw new InvalidDataException($"Duplicate server object class '{match.Groups[1].Value}'");
            if (result.Count == 0) throw new InvalidDataException("No explicit server ObjectClass constants found");
            return result;
        }

        /// <summary>
        /// Only the owning resolved ADB weapon declares modes. A reused mesh may
        /// supply presentation defaults, but cannot add another weapon's modes
        /// (Vulture uses the Liberator bomb-bay mesh without its cluster mode).
        /// </summary>
        public static int[] FireModeIndices(GameObjectDb.GameObject weapon) => weapon.Properties.Keys
            .Select(key => Regex.Match(key, @"^firemode(\d+)_", RegexOptions.IgnoreCase))
            .Where(match => match.Success)
            .Select(match => int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
            .Distinct().Order().ToArray();

        public sealed record WeaponPoint(
            int Index,
            string[] AttachBones,
            string[] PitchBones,
            string[] YawBones,
            WeaponPointAim Aim);

        public sealed record WeaponPointAim(
            [property: JsonPropertyName("orientationDegrees")] float[]? OrientationDegrees,
            [property: JsonPropertyName("leftDegrees")] float[] LeftDegrees,
            [property: JsonPropertyName("rightDegrees")] float[] RightDegrees,
            [property: JsonPropertyName("upDegrees")] float[] UpDegrees,
            [property: JsonPropertyName("downDegrees")] float[] DownDegrees,
            [property: JsonPropertyName("upDownUsesY")] bool? UpDownUsesY,
            [property: JsonPropertyName("invertUpDown")] bool[] InvertUpDown,
            [property: JsonPropertyName("invertYWhenYawBoneIsPitchBone")] bool? InvertYWhenYawBoneIsPitchBone);

        public sealed record WeaponComponent(
            int Index,
            string Record,
            string? WeaponAttachBone);

        public sealed record AttachmentBoneResolution(
            [property: JsonPropertyName("requested")] string Requested,
            [property: JsonPropertyName("resolved")] string Resolved,
            [property: JsonPropertyName("reason")] string Reason);

        public static AttachmentBoneResolution AttachmentBone(string? requested, string[] nodes, string? jointZero)
        {
            if (requested is null) throw new InvalidDataException("Missing authored weapon attachment name");
            string? exact = nodes.FirstOrDefault(name => name.Equals(requested, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return new(requested, exact, "exact");
            // Original AttachSequence name lookup at 0x99e350..0x99e3a2 selects
            // joint zero on a missing child name. Record that resolution rather
            // than renaming source data (Spitfire AA asks for barrels_bone but
            // its original patch5 rig begins at barrels_aa_bone).
            if (jointZero is null) throw new InvalidDataException($"Unresolved attachment '{requested}' without one native joint zero");
            return new(requested, jointZero, "native-joint-zero");
        }

        public sealed record FirePresentationMetadata(
            [property: JsonPropertyName("firstPersonEvent")] string FirstPersonEvent,
            [property: JsonPropertyName("effectKeys")] string[] EffectKeys,
            [property: JsonPropertyName("effectOffset")] int EffectOffset,
            [property: JsonPropertyName("alternatesFire")] bool? AlternatesFire,
            [property: JsonPropertyName("commonMesh")] bool? CommonMesh,
            [property: JsonPropertyName("numberOfMeshes")] int? NumberOfMeshes);

        public sealed record FireBurstMetadata(
            [property: JsonPropertyName("additionalShots")] int AdditionalShots,
            [property: JsonPropertyName("intervalMs")] int IntervalMs);

        public sealed record TurningAccuracyMetadata(
            [property: JsonPropertyName("penaltyPerAngleUnit")] float PenaltyPerAngleUnit,
            [property: JsonPropertyName("maximumPenalty")] float MaximumPenalty);

        public static TurningAccuracyMetadata? TurningAccuracy(GameObjectDb.GameObject weapon)
        {
            // WeaponDefinition +0x30/+0x34, read by retail 0x8faf80. These
            // belong to the owning weapon, not its mesh or an individual mode.
            string? penalty = GameObjectPropertyReader.Scalar(weapon, "turncofpenalty");
            string? maximum = GameObjectPropertyReader.Scalar(weapon, "turncofpenalty_max");
            if (penalty is null && maximum is null) return null;
            float Number(string? raw, string property)
            {
                if (float.TryParse(raw, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float value)
                    && float.IsFinite(value) && value >= 0) return value;
                throw new InvalidDataException($"Weapon '{weapon.Name}' has missing or invalid {property}: '{raw}'");
            }
            return new(Number(penalty, "turncofpenalty"), Number(maximum, "turncofpenalty_max"));
        }

        public sealed record LoopingSoundMetadata(
            [property: JsonPropertyName("file")] string File,
            [property: JsonPropertyName("volume")] float Volume,
            [property: JsonPropertyName("maxDistance")] float MaxDistance);

        public static LoopingSoundMetadata? LoopingSound(GameObjectDb.GameObject weapon)
        {
            // soundkey_looping is a sound/volume/radius tuple, distinct from the
            // boolean clientfiremodeN_looping_fire_sound used during firing.
            var values = GameObjectPropertyReader.Tuple(weapon, "soundkey_looping", 3);
            if (values is null) return null;
            float Number(int index)
            {
                if (float.TryParse(values[index], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float value)
                    && float.IsFinite(value) && value >= 0 && (index != 2 || value > 0)) return value;
                throw new InvalidDataException($"Weapon '{weapon.Name}' has invalid soundkey_looping: {string.Join(" ", values)}");
            }
            if (!values[0].EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Weapon '{weapon.Name}' has invalid soundkey_looping file '{values[0]}'");
            return new(values[0], Number(1), Number(2));
        }

        /// <summary>
        /// autofirecount counts rounds after the initial trigger (Jackhammer 2 => 3,
        /// Rocklet 5 => 6). This is distinct from shotsperround, which counts pellets,
        /// and refiretime, which controls recovery after firing. Sound loops are not bursts.
        /// </summary>
        public static FireBurstMetadata? FireBurst(int fireMode, params GameObjectDb.GameObject?[] propertySources)
        {
            Dictionary<string, List<string>> properties = Merge(propertySources);
            int Number(string suffix)
            {
                string key = $"firemode{fireMode}_{suffix}";
                string? value = GameObjectPropertyReader.Scalar(properties, "weapon burst", key);
                if (value is null) return 0;
                if (int.TryParse(value, out int number) && number >= 0) return number;
                throw new InvalidDataException($"Invalid {key}: '{value}'");
            }
            int count = Number("autofirecount");
            int interval = Number("autofiretime");
            if (count == 0) return null;
            if (interval == 0) throw new InvalidDataException($"Fire mode {fireMode} has a burst without a positive autofiretime");
            return new(count, interval);
        }

        /// <summary>
        /// First-person state and world effect keys are different native contracts. In the
        /// reference EXE, 0x4604ef uses fp_fire_anim_state (getter 0x5de1c0), then 0x462129
        /// triggers that state's effect. World firing at 0x565ca6 instead uses
        /// fire_effect_offset + shot ordinal modulo muzzle count. Do not remap both by mode.
        /// </summary>
        public static FirePresentationMetadata FirePresentation(
            int fireMode, params GameObjectDb.GameObject?[] propertySources)
        {
            Dictionary<string, List<string>> properties = Merge(propertySources);
            string? Value(string key) => GameObjectPropertyReader.Scalar(properties,
                $"weapon fire mode {fireMode}", key);
            int? Integer(string suffix)
            {
                string? value = Value($"clientfiremode{fireMode}_{suffix}");
                if (value is null) return null;
                if (int.TryParse(value, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int parsed)) return parsed;
                throw new InvalidDataException($"Fire mode {fireMode} has invalid {suffix}: '{value}'");
            }
            bool? Flag(string suffix)
            {
                string? value = Value($"clientfiremode{fireMode}_{suffix}");
                if (value is null) return null;
                if (bool.TryParse(value, out bool parsed)) return parsed;
                throw new InvalidDataException($"Fire mode {fireMode} has invalid {suffix}: '{value}'");
            }
            int state = Integer("fp_fire_anim_state") ?? -1;
            if (state < 0) state = fireMode;
            // Retail maps states 0..3 to fire1..4; other values take the fire1 branch.
            int firstPersonEvent = state is >= 0 and <= 3 ? state + 1 : 1;
            int offset = Integer("fire_effect_offset") ?? -1;
            string[] keys = Values(properties, "fire_effect_keys");
            // This is the executable's default vector (0x5dbfa3..0x5dc0b2), not
            // a fabricated fallback for missing package bindings.
            if (keys.Length == 0) keys = ["fire1", "fire2", "fire3"];
            return new($"fire{firstPersonEvent}", keys, offset < 0 ? fireMode : offset,
                Flag("weaponalternatesfire"), Flag("common_mesh"), Integer("numberofmeshes"));
        }

        /// <summary>
        /// Resolves the ordered render records owned by a weapon-system container. The client
        /// stores their corresponding weapon-local attachment names in a parallel property.
        /// Neither list describes the vehicle-side mount; that comes from WeaponPoint.AttachBones.
        /// </summary>
        public static WeaponComponent[] Components(GameObjectDb.GameObject? weapon)
        {
            string[] records = GameObjectPropertyReader.List(weapon, "meshsequence").ToArray();
            string[] attachBones = GameObjectPropertyReader.List(weapon, "weaponattachbonenames").ToArray();
            return records.Select((record, index) => new WeaponComponent(
                index,
                record,
                index < attachBones.Length ? attachBones[index] : null)).ToArray();
        }

        /// <summary>
        /// Native parser 0x5dd2e6..0x5dd376 parses the suffix as an integer (empty is zero)
        /// and assigns that vector slot. Thus muzzle and muzzle0 replace one another;
        /// they must not become two barrels. Apply inheritance and command order before
        /// joining the slots, including when a child changes the spelling of slot zero.
        /// </summary>
        public static string[] Muzzles(int fireMode, params GameObjectDb.GameObject?[] propertySources)
        {
            string prefix = $"clientfiremode{fireMode}_muzzle";
            var slots = new SortedDictionary<int, string>();
            foreach (GameObjectDb.GameObject source in propertySources.OfType<GameObjectDb.GameObject>())
            {
                var ancestry = source.InheritanceChain.Select((name, index) => (name, index))
                    .ToDictionary(value => value.name, value => value.index, StringComparer.OrdinalIgnoreCase);
                foreach (var property in source.Properties
                    .Where(property => MuzzleIndex(property.Key, prefix).HasValue)
                    .OrderBy(property => source.PropertySources.TryGetValue(property.Key, out var origin)
                        && ancestry.TryGetValue(origin.DefinedBy, out int rank) ? rank : 0)
                    .ThenBy(property => source.PropertySources.TryGetValue(property.Key, out var origin)
                        ? origin.CommandIndex : 0))
                {
                    int index = MuzzleIndex(property.Key, prefix)!.Value;
                    if (index is < 0 or >= 10)
                        throw new InvalidDataException($"{source.Name}: muzzle slot {index} exceeds native range 0..9");
                    slots[index] = GameObjectPropertyReader.Scalar(source, property.Key)
                        ?? throw new InvalidDataException($"{source.Name}: empty muzzle slot {index}");
                }
            }
            if (slots.Keys.Where((index, ordinal) => index != ordinal).Any())
                throw new InvalidDataException($"Fire mode {fireMode} has gaps in its native muzzle slots");
            return slots.Values.ToArray();
        }

        /// <summary>Matches the requested occurrence of an exact weaponN definition to weaponpointN.</summary>
        public static WeaponPoint? ExactWeaponPoint(
            string weaponDefinition,
            int occurrence,
            params GameObjectDb.GameObject?[] propertySources)
        {
            Dictionary<string, List<string>> properties = Merge(propertySources);
            int[] candidates = properties
                .Select(property => new { property.Key, property.Value, Match = WeaponProperty().Match(property.Key) })
                .Where(item => item.Match.Success
                    && GameObjectPropertyReader.Scalar(
                        new[] { new KeyValuePair<string, List<string>>(item.Key, item.Value) },
                        "vehicle weapon binding", item.Key) is string value
                    && value.Equals(weaponDefinition, StringComparison.OrdinalIgnoreCase))
                .Select(item => int.Parse(item.Match.Groups[1].Value))
                .OrderBy(index => index)
                .ToArray();
            return occurrence < candidates.Length ? WeaponPointAt(properties, candidates[occurrence]) : null;
        }

        /// <summary>Resolves a server WeaponPaths(N) binding to the client's weaponN/weaponpointN pair.</summary>
        public static WeaponPoint? IndexedWeaponPoint(
            int weaponPoint,
            params GameObjectDb.GameObject?[] propertySources)
        {
            Dictionary<string, List<string>> properties = Merge(propertySources);
            return Values(properties, $"weapon{weaponPoint}").Length > 0
                ? WeaponPointAt(properties, weaponPoint)
                : null;
        }

        private static Dictionary<string, List<string>> Merge(IEnumerable<GameObjectDb.GameObject?> sources)
        {
            var properties = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (GameObjectDb.GameObject source in sources.OfType<GameObjectDb.GameObject>())
                foreach ((string key, List<string> value) in source.Properties)
                    properties[key] = value;
            return properties;
        }

        private static WeaponPoint WeaponPointAt(Dictionary<string, List<string>> properties, int index)
        {
            string prefix = $"weaponpoint{index}_";
            var source = new GameObjectDb.GameObject { Name = prefix, Properties = properties };
            float Number(string value) => float.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float parsed) && float.IsFinite(parsed)
                ? parsed : throw new InvalidDataException($"{prefix}: invalid aim number '{value}'");
            // Native 0x9536d0..0x953747 appends every AOF value. Flail and
            // Switchblade have distinct mobile/deployed limits, not duplicates.
            float[] Arc(string suffix) => Values(properties, prefix + suffix).Select(Number).ToArray();
            string? axis = GameObjectPropertyReader.Scalar(properties, prefix, prefix + "UpDownUsesY");
            bool? usesY = axis is null ? null : bool.TryParse(axis, out bool flag)
                ? flag : throw new InvalidDataException($"{prefix}UpDownUsesY: invalid boolean '{axis}'");
            bool Boolean(string value) => bool.TryParse(value, out bool parsed) ? parsed
                : throw new InvalidDataException($"{prefix}: invalid aim boolean '{value}'");
            string? sharedPitch = GameObjectPropertyReader.Scalar(properties, prefix, prefix + "InvertYWhenYawBoneIsPitchBone");
            return new(index, Values(properties, prefix + "AttachBones"),
                Values(properties, prefix + "PitchBone"), Values(properties, prefix + "YawBone"),
                new(GameObjectPropertyReader.Tuple(source, prefix + "BoneOrientation", 3)?.Select(Number).ToArray(),
                    Arc("LeftAOF"), Arc("RightAOF"), Arc("UpAOF"), Arc("DownAOF"), usesY,
                    Values(properties, prefix + "InvertUpDown").Select(Boolean).ToArray(),
                    sharedPitch is null ? null : Boolean(sharedPitch)));
        }

        private static string[] Values(Dictionary<string, List<string>> properties, string key) =>
            properties.TryGetValue(key, out List<string>? values)
                ? values.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray()
                : [];

        private static int? MuzzleIndex(string key, string prefix)
        {
            if (key.Equals(prefix, StringComparison.OrdinalIgnoreCase)) return 0;
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
            return int.TryParse(key[prefix.Length..], out int index) ? index : null;
        }

        [GeneratedRegex("^weapon(\\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex WeaponProperty();

        [GeneratedRegex(@"^\s*final val (\w+)\s*=\s*(\d+)\s*(?://[^\r\n]*)?$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
        private static partial Regex ObjectClassConstant();
    }
}
