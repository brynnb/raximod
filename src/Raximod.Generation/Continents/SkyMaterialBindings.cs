using Raximod.EngineAssets.Databases;

namespace Raximod.Generation.Continents;

/// <summary>Resolves sky mesh swap scopes through the mesh's authored effect package.</summary>
public static class SkyMaterialBindings
{
    public sealed record Binding(
        string Package, string Scope, IReadOnlyList<NativeEffectMaterialSwap> MaterialSwaps,
        IReadOnlyList<NativeEffectLightingSwap> LightingSwaps,
        IReadOnlyList<NativeEffectHiddenPart> HiddenParts,
        NativeAdbProvenance Provenance);

    public static Binding? Resolve(
        SkyDatabase.SkyLayer layer, IReadOnlyDictionary<string, NativeEffectPackage> packages)
    {
        if (layer.Argument == null) return null;
        if (!layer.Kind.Equals("mesh", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Sky layer {layer.Index} '{layer.Name}' has an unsupported argument on '{layer.Kind}'");

        // Retail's sky constructor passes the third layer argument to SetSwapPackage
        // (planetside.exe 0x8775cb-0x8775ea -> 0x96af30), not to a material lookup.
        // Resolve against the mesh name: skydome10's layer uses package skydome1/map10.
        if (!packages.TryGetValue(layer.Name, out NativeEffectPackage? package))
            throw new InvalidDataException($"Sky mesh '{layer.Name}' references missing effect package for scope '{layer.Argument}'");
        string scope = layer.Argument;
        if (!package.Commands.Any(command => command.Name == "efp_swap_begin"
            && command.Arguments.Count == 1
            && command.Arguments[0].Equals(scope, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"Sky mesh '{layer.Name}' references missing swap scope '{scope}'");

        var materials = package.MaterialSwaps.Where(swap =>
            swap.Scope.Equals(scope, StringComparison.OrdinalIgnoreCase)).ToArray();
        var lighting = package.LightingSwaps.Where(swap =>
            swap.Scope.Equals(scope, StringComparison.OrdinalIgnoreCase)).ToArray();
        var hidden = package.HiddenParts.Where(part =>
            string.Equals(part.SwapScope, scope, StringComparison.OrdinalIgnoreCase)).ToArray();
        return new Binding(package.Name, scope, materials, lighting, hidden, package.Provenance);
    }
}
