# Raximod extraction rules

Raximod is a headless extraction toolkit and TerraSunder's authoritative PlanetSide asset pipeline.
Before changing generation code, read `docs/terrasunder-pipeline.md`, then the format- or
family-specific document it links.

- Keep parsing/lossless structures in the engine-assets library, transport-neutral generation in the
  generation library, cohesive family operations in `src/Raximod.Modules`, and thin argument
  handling in `src/Raximod.Cli`. The supported product has no desktop viewer. `tools/` contains only
  narrowly scoped recovery utilities with documented provenance.
- Parsing, semantic interpretation, export, and Babylon runtime behavior are separate layers. Fix the
  earliest incorrect layer. Do not bake a runtime workaround into a parser.
- Preserve raw command order, repeated properties, offsets, parent links, unknown fields, and source
  provenance. A semantic view may resolve inheritance, but it must not replace the raw view.
- Use explicit scalar, tuple/vector, and list accessors. A scalar accessor must fail on multiple
  authored values unless that property is explicitly classified as a tuple/list.
- New parsers need byte/range accounting where possible. “Every byte accounted for” and “every field
  understood” are different claims; report both honestly.
- Generated output must be deterministic. Write only when content changes, sort stable catalogs, and
  emit source identities and diagnostics suitable for diffing.
- Follow `docs/output-layout.md`: generated output may remain when its format, path, provenance,
  regeneration command, and source-control policy are explicit. Do not commit machine-specific paths
  or installed-client samples merely because a development command produced them.
- Never select the first ambiguous UBR record, skeleton, texture, or embedded mesh. Search all
  installed archives, require a unique result or explicit source, and fail closed on ambiguity.
- Do not strip animation/rig data merely because TerraSunder currently instances an asset. Preserve a
  full-fidelity source export; static derivatives are an explicit optimization.
- Never use render geometry as collision fallback. Production order is asset discovery/export,
  spatial/source audit, explicit/AAB collision export, then manifest publication.
- Never use spatial similarity, bounds, or vertex count to discard suspected LODs. Use native LOD,
  complete AAB ownership, or narrowly audited whole-model naming evidence and emit provenance.
- Run focused tests/audits before broad ones. Keep full-install exports sequential and avoid large
  temporary output in `/tmp`; it may be RAM-backed. Do not overwrite a known-good published tree if a
  strict audit fails.
- When replacing a workaround with a root fix, remove/disable the workaround during validation so it
  cannot mask the result.
- When research establishes a reusable native convention, preserve it in a focused test/audit, a
  code comment at the dangerous boundary, and `docs/terrasunder-pipeline.md`. A chat transcript or
  local disassembly notebook is evidence, not durable project documentation.
