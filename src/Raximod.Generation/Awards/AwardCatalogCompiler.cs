using System.Globalization;
using Raximod.EngineAssets.Databases;

namespace Raximod.Generation.Awards;

public sealed record AwardPropertySource(string Name, int StreamOffset);

public sealed record AwardLocalizationValue(
    string Key,
    string? English,
    int? LineNumber,
    string Resolution);

public sealed record AwardLocalization(
    AwardLocalizationValue? DisplayName,
    AwardLocalizationValue? Title,
    AwardLocalizationValue? Description,
    AwardLocalizationValue? Requirements);

public sealed record CompiledAwardRequirement(
    int Index,
    string Type,
    int? QualificationCount,
    int? InOneLifeCount,
    int? MonthsSinceCreation,
    string? AwardName,
    string? Certification,
    string? ObjectGroup,
    string? GameObjectClassName,
    IReadOnlyList<AwardPropertySource> Sources);

public sealed record UnsupportedAwardProperty(
    string Name,
    IReadOnlyList<string> Values,
    int StreamOffset);

public sealed record CompiledAwardDefinition(
    int Id,
    string Name,
    string? Type,
    string? Level,
    string? Empire,
    string? Sex,
    bool Displayable,
    int? AutoShowPriority,
    IReadOnlyList<int> RibbonColorIndices,
    AwardLocalization Localization,
    IReadOnlyList<CompiledAwardRequirement> Requirements,
    IReadOnlyList<UnsupportedAwardProperty> UnsupportedProperties,
    NativeAdbProvenance Provenance);

public sealed record AwardObjectGroupMember(
    int ClassId,
    string Name,
    IReadOnlyList<string> FirstTimeEvents,
    GameObjectDb.GameObjectProvenance Provenance,
    GameObjectDb.GameObjectPropertySource GroupSource,
    GameObjectDb.GameObjectPropertySource? FirstTimeEventSource);

public sealed record CompiledAwardObjectGroup(
    string Name,
    IReadOnlyList<AwardObjectGroupMember> Members);

public sealed record AwardFirstTimeEventObject(
    int ClassId,
    string Name,
    IReadOnlyList<string> AwardRequirementGroups,
    GameObjectDb.GameObjectProvenance Provenance,
    GameObjectDb.GameObjectPropertySource EventSource);

public sealed record CompiledAwardFirstTimeEvent(
    string Name,
    IReadOnlyList<AwardFirstTimeEventObject> Objects);

public sealed record AwardCatalogDiagnostic(
    string Code,
    string Message,
    string? AwardName = null,
    string? PropertyName = null,
    int? StreamOffset = null);

public sealed record AwardCatalogAudit(
    int CommendationCount,
    int PaletteColorCount,
    bool CommendationIdsContiguous,
    bool PaletteIndicesContiguous,
    int ReferencedAwardCount,
    int ReferencedObjectGroupCount,
    int ReferencedGameObjectCount,
    int UnsupportedPropertyCount);

public sealed record CompiledAwardCatalog(
    IReadOnlyList<NativeAwardColor> Palette,
    IReadOnlyList<CompiledAwardDefinition> Definitions,
    IReadOnlyList<CompiledAwardObjectGroup> ObjectGroups,
    IReadOnlyList<CompiledAwardFirstTimeEvent> FirstTimeEvents,
    IReadOnlyList<AwardCatalogDiagnostic> Diagnostics,
    AwardCatalogAudit Audit);

/// <summary>
/// Converts the ordered/lossless awards.adb view into the typed, transport-neutral
/// contract consumed by PSForever and TerraSunder. The raw NativeAward records remain
/// separately exportable; this compiler never replaces or flattens their provenance.
/// </summary>
public static class AwardCatalogCompiler
{
    public const int ExpectedCommendationCount = 429;
    public const int ExpectedPaletteColorCount = 1024;

    private static readonly IReadOnlySet<string> KnownTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "command", "general", "gunner", "operator", "support", "weapons", "weapons_heavy", "weapons_light",
    };

    private static readonly IReadOnlySet<string> KnownLevels = new HashSet<string>(StringComparer.Ordinal)
    {
        "advanced", "basic", "elite", "expert", "master", "none",
    };

    private static readonly IReadOnlySet<string> KnownEmpires = new HashSet<string>(StringComparer.Ordinal)
    {
        "nc", "tr", "vs",
    };

    private static readonly IReadOnlySet<string> KnownRequirementTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "age", "award", "combination", "driverassist", "firsttimeevent", "hackassist", "healassist", "jack",
        "kill", "killblackops", "killwith", "qualified", "repairassist", "respawnassist", "revive",
        "reviveassist", "teleportassist", "transportassist",
    };

    private static readonly IReadOnlySet<string> KnownTopLevelProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        "autoshow_priority", "displayable", "empire", "level", "type", "bar_color0", "bar_color1",
        "bar_color2", "bar_color3", "bar_color4", "bar_color5", "bar_color6", "bar_color7", "bar_color8",
        "bar_color9", "description", "displayname", "requirements", "title",
    };

    public static CompiledAwardCatalog Compile(
        IReadOnlyList<NativeAward> records,
        NativeStringTable english,
        GameObjectDb gameObjects)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(english);
        ArgumentNullException.ThrowIfNull(gameObjects);

        NativeAward[] paletteRecords = records
            .Where(record => record.Name.Equals("award_colors", StringComparison.Ordinal))
            .ToArray();
        if (paletteRecords.Length != 1)
            throw new InvalidDataException($"awards.adb must contain exactly one award_colors record, found {paletteRecords.Length}");

        NativeAwardColor[] palette = paletteRecords[0].Colors.ToArray();
        bool paletteContiguous = palette.Select(color => color.Index).SequenceEqual(Enumerable.Range(0, palette.Length));
        if (palette.Length != ExpectedPaletteColorCount || !paletteContiguous)
            throw new InvalidDataException(
                $"awards.adb palette must contain contiguous indices 0..{ExpectedPaletteColorCount - 1}; " +
                $"found {palette.Length} entries (contiguous={paletteContiguous})");

        NativeAward[] commendations = records
            .Where(record => !record.Name.Equals("award_colors", StringComparison.Ordinal))
            .ToArray();
        if (commendations.Length != ExpectedCommendationCount)
            throw new InvalidDataException(
                $"awards.adb commendation sequence must contain {ExpectedCommendationCount} records; found {commendations.Length}");

        if (commendations.Select(record => record.Name).Distinct(StringComparer.Ordinal).Count() != commendations.Length)
            throw new InvalidDataException("awards.adb contains duplicate commendation names");

        var diagnostics = new List<AwardCatalogDiagnostic>();
        var definitions = new List<CompiledAwardDefinition>(commendations.Length);
        for (int id = 0; id < commendations.Length; id++)
            definitions.Add(CompileDefinition(id, commendations[id], english, palette.Length, diagnostics));

        var definitionsByName = definitions.ToDictionary(definition => definition.Name, StringComparer.Ordinal);
        var resolvedObjectsByName = gameObjects.ResolvedObjects.ToDictionary(value => value.Name, StringComparer.Ordinal);
        CompiledAwardObjectGroup[] objectGroups = CompileObjectGroups(gameObjects).ToArray();
        var objectGroupsByName = objectGroups.ToDictionary(value => value.Name, StringComparer.Ordinal);
        CompiledAwardFirstTimeEvent[] firstTimeEvents = CompileFirstTimeEvents(gameObjects).ToArray();

        var referencedAwards = new HashSet<string>(StringComparer.Ordinal);
        var referencedGroups = new HashSet<string>(StringComparer.Ordinal);
        var referencedObjects = new HashSet<string>(StringComparer.Ordinal);
        foreach (CompiledAwardDefinition definition in definitions)
        {
            foreach (CompiledAwardRequirement requirement in definition.Requirements)
            {
                if (requirement.AwardName is { } awardName)
                {
                    referencedAwards.Add(awardName);
                    if (!definitionsByName.ContainsKey(awardName))
                        throw new InvalidDataException(
                            $"award '{definition.Name}' requirement {requirement.Index} references unknown award '{awardName}'");
                }
                if (requirement.ObjectGroup is { } objectGroup)
                {
                    referencedGroups.Add(objectGroup);
                    if (!objectGroupsByName.TryGetValue(objectGroup, out CompiledAwardObjectGroup? group) || group.Members.Count == 0)
                        throw new InvalidDataException(
                            $"award '{definition.Name}' requirement {requirement.Index} references empty or unknown object group '{objectGroup}'");
                }
                if (requirement.GameObjectClassName is { } gameObjectName)
                {
                    referencedObjects.Add(gameObjectName);
                    if (!resolvedObjectsByName.ContainsKey(gameObjectName))
                        throw new InvalidDataException(
                            $"award '{definition.Name}' requirement {requirement.Index} references unknown game object '{gameObjectName}'");
                }
            }
        }

        int unsupportedCount = definitions.Sum(definition => definition.UnsupportedProperties.Count);
        return new CompiledAwardCatalog(
            palette,
            definitions,
            objectGroups,
            firstTimeEvents,
            diagnostics,
            new AwardCatalogAudit(
                definitions.Count,
                palette.Length,
                definitions.Select(definition => definition.Id).SequenceEqual(Enumerable.Range(0, definitions.Count)),
                paletteContiguous,
                referencedAwards.Count,
                referencedGroups.Count,
                referencedObjects.Count,
                unsupportedCount));
    }

    private static CompiledAwardDefinition CompileDefinition(
        int id,
        NativeAward record,
        NativeStringTable english,
        int paletteCount,
        ICollection<AwardCatalogDiagnostic> diagnostics)
    {
        var byName = record.Properties
            .GroupBy(property => property.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        string? Scalar(string name)
        {
            if (!byName.TryGetValue(name, out NativeAwardProperty[]? properties)) return null;
            if (properties.Length != 1)
                throw new InvalidDataException($"award '{record.Name}' property '{name}' is authored {properties.Length} times");
            if (properties[0].Values.Count != 1)
                throw new InvalidDataException(
                    $"award '{record.Name}' property '{name}' contains {properties[0].Values.Count} values; expected one scalar");
            return properties[0].Values[0];
        }

        int? Integer(string name)
        {
            string? text = Scalar(name);
            if (text is null) return null;
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                throw new InvalidDataException($"award '{record.Name}' property '{name}' is not an integer: '{text}'");
            return value;
        }

        string? type = Scalar("type");
        if (type is not null && !KnownTypes.Contains(type))
            throw new InvalidDataException($"award '{record.Name}' has unknown type '{type}'");
        string? level = Scalar("level");
        if (level is not null && !KnownLevels.Contains(level))
            throw new InvalidDataException($"award '{record.Name}' has unknown level '{level}'");
        string? empire = Scalar("empire");
        if (empire is not null && !KnownEmpires.Contains(empire))
            throw new InvalidDataException($"award '{record.Name}' has unknown empire '{empire}'");

        // awards.adb has no general sex property. These two manual event awards
        // encode the intended recipient only in their canonical native names;
        // preserve that narrow source convention in compiled data so runtimes do
        // not rediscover it with label matching.
        string? sex = record.Name switch
        {
            "valentine_female" => "female",
            "valentine_male" => "male",
            _ => null,
        };

        bool displayable = true;
        if (Scalar("displayable") is { } displayableText && !bool.TryParse(displayableText, out displayable))
            throw new InvalidDataException($"award '{record.Name}' has invalid displayable value '{displayableText}'");

        int[] colors = Enumerable.Range(0, 10).Select(index =>
        {
            int? value = Integer($"bar_color{index}");
            if (value is null)
                throw new InvalidDataException($"award '{record.Name}' is missing bar_color{index}");
            if (value < 0 || value >= paletteCount)
                throw new InvalidDataException(
                    $"award '{record.Name}' bar_color{index} index {value} is outside palette 0..{paletteCount - 1}");
            return value.Value;
        }).ToArray();

        AwardLocalizationValue? Localized(string propertyName)
        {
            string? key = Scalar(propertyName);
            if (key is null) return null;
            IReadOnlyList<NativeStringTableEntry> entries = english.Find(key);
            if (entries.Count == 0)
            {
                diagnostics.Add(new AwardCatalogDiagnostic(
                    "missing-localization-key",
                    $"Award localization key '{key}' is absent from english.str; the typed requirement remains available for generic presentation.",
                    record.Name,
                    propertyName));
                return new AwardLocalizationValue(key, null, null, "missing");
            }
            string value = entries[0].Value;
            if (entries.Any(entry => !entry.Value.Equals(value, StringComparison.Ordinal)))
                throw new InvalidDataException(
                    $"award '{record.Name}' property '{propertyName}' references ambiguous localization key '{key}' on lines " +
                    string.Join(", ", entries.Select(entry => entry.LineNumber)));
            return new AwardLocalizationValue(key, value, entries[0].LineNumber, "resolved");
        }

        var requirementIndexes = new SortedSet<int>();
        foreach (NativeAwardProperty property in record.Properties)
        {
            if (!TryRequirementProperty(property.Name, out int requirementIndex, out _)) continue;
            requirementIndexes.Add(requirementIndex);
        }
        if (requirementIndexes.Count > 0 && !requirementIndexes.SequenceEqual(Enumerable.Range(0, requirementIndexes.Max + 1)))
            throw new InvalidDataException($"award '{record.Name}' has non-contiguous requirement indices");

        var requirements = new List<CompiledAwardRequirement>(requirementIndexes.Count);
        foreach (int index in requirementIndexes)
        {
            string prefix = $"requirement{index}_";
            string? requirementType = Scalar(prefix + "type");
            if (requirementType is null)
                throw new InvalidDataException($"award '{record.Name}' requirement {index} has no type");
            if (!KnownRequirementTypes.Contains(requirementType))
                throw new InvalidDataException(
                    $"award '{record.Name}' requirement {index} has unknown type '{requirementType}'");

            int? qualificationCount = Integer(prefix + "qualificationcount");
            int? inOneLifeCount = Integer(prefix + "inonelifecount");
            int? monthsSinceCreation = Integer(prefix + "monthssincecreation");
            if (qualificationCount is < 0 || inOneLifeCount is < 0 || monthsSinceCreation is < 0)
                throw new InvalidDataException($"award '{record.Name}' requirement {index} contains a negative threshold");
            if (requirementType == "age" && monthsSinceCreation is null)
                throw new InvalidDataException($"award '{record.Name}' age requirement {index} has no month threshold");
            if (requirementType is "award" or "qualified" && Scalar(prefix + "awardname") is null)
                throw new InvalidDataException($"award '{record.Name}' {requirementType} requirement {index} has no award name");
            if (requirementType is not ("age" or "award" or "qualified") && qualificationCount is null)
                throw new InvalidDataException($"award '{record.Name}' requirement {index} has no qualification count");

            NativeAwardProperty[] sources = record.Properties
                .Where(property => property.Name.StartsWith(prefix, StringComparison.Ordinal))
                .OrderBy(property => property.StreamOffset)
                .ToArray();
            requirements.Add(new CompiledAwardRequirement(
                index,
                requirementType,
                qualificationCount,
                inOneLifeCount,
                monthsSinceCreation,
                Scalar(prefix + "awardname"),
                Scalar(prefix + "certification"),
                Scalar(prefix + "objectgroup"),
                Scalar(prefix + "gameobjectclassname"),
                sources.Select(property => new AwardPropertySource(property.Name, property.StreamOffset)).ToArray()));
        }

        var unsupported = new List<UnsupportedAwardProperty>();
        foreach (NativeAwardProperty property in record.Properties)
        {
            if (KnownTopLevelProperties.Contains(property.Name) || TryRequirementProperty(property.Name, out _, out _))
                continue;
            unsupported.Add(new UnsupportedAwardProperty(property.Name, property.Values, property.StreamOffset));
            diagnostics.Add(new AwardCatalogDiagnostic(
                "unsupported-award-property",
                $"Award property '{property.Name}' is preserved but has no evaluator semantics.",
                record.Name,
                property.Name,
                property.StreamOffset));
        }

        return new CompiledAwardDefinition(
            id,
            record.Name,
            type,
            level,
            empire,
            sex,
            displayable,
            Integer("autoshow_priority"),
            colors,
            new AwardLocalization(
                Localized("displayname"),
                Localized("title"),
                Localized("description"),
                Localized("requirements")),
            requirements,
            unsupported,
            record.Provenance);
    }

    private static IEnumerable<CompiledAwardObjectGroup> CompileObjectGroups(GameObjectDb gameObjects)
    {
        var groups = new SortedDictionary<string, List<AwardObjectGroupMember>>(StringComparer.Ordinal);
        foreach (GameObjectDb.GameObject gameObject in gameObjects.ResolvedObjects)
        {
            if (!gameObject.Properties.TryGetValue("award_requirement_groups", out List<string>? memberships))
                continue;
            if (!gameObject.PropertySources.TryGetValue("award_requirement_groups", out GameObjectDb.GameObjectPropertySource? groupSource))
                throw new InvalidDataException($"game object '{gameObject.Name}' has award groups without source provenance");

            IReadOnlyList<string> events = gameObject.Properties.TryGetValue("xp_event", out List<string>? xpEvents)
                ? xpEvents.ToArray()
                : Array.Empty<string>();
            gameObject.PropertySources.TryGetValue("xp_event", out GameObjectDb.GameObjectPropertySource? eventSource);
            foreach (string groupName in memberships)
            {
                if (!groups.TryGetValue(groupName, out List<AwardObjectGroupMember>? members))
                    groups[groupName] = members = new List<AwardObjectGroupMember>();
                members.Add(new AwardObjectGroupMember(
                    gameObject.ClassId,
                    gameObject.Name,
                    events,
                    gameObject.Provenance,
                    groupSource,
                    eventSource));
            }
        }

        return groups.Select(pair => new CompiledAwardObjectGroup(
            pair.Key,
            pair.Value.OrderBy(member => member.ClassId).ThenBy(member => member.Name, StringComparer.Ordinal).ToArray()));
    }

    private static IEnumerable<CompiledAwardFirstTimeEvent> CompileFirstTimeEvents(GameObjectDb gameObjects)
    {
        var events = new SortedDictionary<string, List<AwardFirstTimeEventObject>>(StringComparer.Ordinal);
        foreach (GameObjectDb.GameObject gameObject in gameObjects.ResolvedObjects)
        {
            if (!gameObject.Properties.TryGetValue("xp_event", out List<string>? eventNames))
                continue;
            if (!gameObject.PropertySources.TryGetValue("xp_event", out GameObjectDb.GameObjectPropertySource? eventSource))
                throw new InvalidDataException($"game object '{gameObject.Name}' has xp_event without source provenance");
            IReadOnlyList<string> groups = gameObject.Properties.TryGetValue("award_requirement_groups", out List<string>? memberships)
                ? memberships.ToArray()
                : Array.Empty<string>();
            foreach (string eventName in eventNames)
            {
                if (!events.TryGetValue(eventName, out List<AwardFirstTimeEventObject>? objects))
                    events[eventName] = objects = new List<AwardFirstTimeEventObject>();
                objects.Add(new AwardFirstTimeEventObject(
                    gameObject.ClassId,
                    gameObject.Name,
                    groups,
                    gameObject.Provenance,
                    eventSource));
            }
        }

        return events.Select(pair => new CompiledAwardFirstTimeEvent(
            pair.Key,
            pair.Value.OrderBy(value => value.ClassId).ThenBy(value => value.Name, StringComparer.Ordinal).ToArray()));
    }

    private static bool TryRequirementProperty(string name, out int index, out string suffix)
    {
        index = 0;
        suffix = "";
        const string prefix = "requirement";
        if (!name.StartsWith(prefix, StringComparison.Ordinal)) return false;
        int separator = name.IndexOf('_', prefix.Length);
        if (separator < 0) return false;
        if (!int.TryParse(name.AsSpan(prefix.Length, separator - prefix.Length), NumberStyles.None,
                CultureInfo.InvariantCulture, out index)) return false;
        suffix = name[(separator + 1)..];
        return suffix is "type" or "qualificationcount" or "inonelifecount" or "monthssincecreation"
            or "awardname" or "certification" or "objectgroup" or "gameobjectclassname";
    }
}
