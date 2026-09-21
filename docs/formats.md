# Supported formats

Every format below was recovered from an engine-derived reference client and ported to a UI-free
C# library (`Raximod.EngineAssets`) used by the headless Raximod commands.

| Format | Extension(s) | Read | Write | Notes |
|---|---|:---:|:---:|---|
| PACK archive | `.pak` | ✅ | ✅ | v2 container: 28-byte header + directory record, LZO1X-compressed payload records with a length-seeded CRC. The repack writer emits store-mode LZO1X and matching per-record CRCs. |
| FLAT archive | `.fat` / `.fdx` | ✅ | ✅ | A self-describing flat data store (name + length framed inline) with a companion index; the writer emits a matching `.fat` + `.fdx` pair. |
| UberMesh | `.ubr` (`uber` magic) | ✅ | — | Per-`CMeshSection` geometry: positions, UVs, normals, skeletons, and material bindings, decoded straight into GPU-ready submeshes. |
| UberAnim | `anims.ubr` (`ANIM` magic) | ✅ | — | A two-stream keyframe database — a directory stream and a data stream — read together and matched to a model's skeleton by bone name. |
| ASCII database | `.adb` | ✅ | ✅ | `chunky` / `asciidatabase` framed token tables. The lossless representation retains raw command order/repetitions, string-pool and name-index bytes, record offsets, parent links and opaque header fields, and re-encodes byte-identically. Typed and inherited views are separate projections. |
| Surface tile | `.srf` | ✅ | ✅ | A 128×128 grid of typed cells describing ground cover / terrain surface type. Edits re-encode losslessly. |
| Map manifest | `contents_mapNN.mpo` | ✅ | — | Per-continent tile and object placement — which static scene objects (bases, towers, warpgates, trees) sit where, and which UberMesh record backs each one. |
| Drop-pod location table | `.droppod` | ✅ | ✅ | Native extent/cell-size header and row-major float32 XY landing mappings. Full byte accounting and identical encoding; static suggestions are not current deployment permission. |
| DDS texture | `.dds` | ✅ | — | BC1/BC2/BC3 block-compressed and uncompressed 32-bpp images, decoded for PNG and GLB export. |
| LZO1X | — | ✅ | ✅ | The compression codec every `.pak` record uses; not a file format on its own, but load-bearing for everything inside a PACK archive. |

## What "Write" means here

A ✅ in the **Write** column means the `Raximod.EngineAssets` library can re-encode that
exact byte layout — not just export to some other format. PACK, FLAT and ADB structures round-trip
losslessly (rebuild → reload → re-extract byte-identical), and surface tiles re-encode in place after
a paint edit. ADB write support is currently a structural proof/format API, not an endorsement of
mutating retail databases without a semantic validator.

Write support is a library and validation capability. Raximod deliberately does not modify an
installed client in place; its public commands extract and package derived assets into another
directory. PACK/FLAT rebuilding remains covered by the headless validation harness for lossless
format work.

## Detection

Documents are identified **by magic bytes first, file extension second** — an archive entry with
no reliable extension (common inside `.pak` payloads) still opens as the right document type.
Inspection commands report unsupported input explicitly rather than guessing from an extension.
