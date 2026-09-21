using Raximod.EngineAssets.Databases;
using Raximod.Generation.Assets;
using Xunit;

namespace Raximod.Generation.Tests
{
    public sealed class NativeAnimationClipSelectionTests
    {
        [Theory]
        [InlineData("refpose1", true)]
        [InlineData("REFPOSE96", true)]
        [InlineData("blended_refpose", true)]
        [InlineData("play_once", false)]
        [InlineData("play_once_hold", false)]
        [InlineData("0", false)]
        [InlineData(null, false)]
        public void ClassifiesAuthoredVirtualReferencePoseModes(string? mode, bool expected)
        {
            Assert.Equal(expected, NativeAnimationClipSelection.IsVirtualReferencePoseMode(mode));
        }

        [Fact]
        public void ExpandsBlendedReferencePoseFamilyDeterministically()
        {
            NativeAnimationEntry entry = Entry("ncmlite_pain01_rifle", "blended_refpose");
            string[] available =
            [
                "ncmlite_pain01_rifle_ref08",
                "unrelated_ref00",
                "ncmlite_pain01_rifle_ref00",
                "ncmlite_pain01_rifle_ref01",
                "ncmlite_pain01_rifle_reference",
            ];

            NativeAnimationClipSelection.Binding binding =
                NativeAnimationClipSelection.Resolve(entry, available);

            Assert.Equal("virtual-reference-poses", binding.Resolution);
            Assert.Equal(
            [
                "ncmlite_pain01_rifle_ref00",
                "ncmlite_pain01_rifle_ref01",
                "ncmlite_pain01_rifle_ref08",
            ], binding.ConcreteClips);
        }

        [Fact]
        public void UsesConcreteFamilyWhenPlaybackEnumIsUnresolvedAndExactClipIsAbsent()
        {
            NativeAnimationEntry entry = Entry("ncflite_walkbackward_rifle", "0");

            NativeAnimationClipSelection.Binding binding = NativeAnimationClipSelection.Resolve(
                entry,
                [
                    "ncflite_walkbackward_rifle_ref05",
                    "ncflite_walkbackward_rifle_ref00",
                    "ncflite_walkbackward_rifle_ref01",
                ]);

            Assert.Equal("concrete-reference-family", binding.Resolution);
            Assert.Equal(
            [
                "ncflite_walkbackward_rifle_ref00",
                "ncflite_walkbackward_rifle_ref01",
                "ncflite_walkbackward_rifle_ref05",
            ], binding.ConcreteClips);
        }

        [Fact]
        public void PreservesAnOrdinaryExactClipEvenWhenSimilarlyNamedReferencesExist()
        {
            NativeAnimationEntry entry = Entry("ncmlite_reload_rifle", "play_once_blend");

            NativeAnimationClipSelection.Binding binding = NativeAnimationClipSelection.Resolve(
                entry,
                ["ncmlite_reload_rifle", "ncmlite_reload_rifle_ref00"]);

            Assert.Equal("authored-exact", binding.Resolution);
            Assert.Equal(["ncmlite_reload_rifle"], binding.ConcreteClips);
        }

        [Fact]
        public void InstalledMaleAndFemalePackagesResolveRequiredRifleCombatClips()
        {
            string? planetSide = FindPlanetSideDirectory();
            if (planetSide == null) return;

            string packagePath = Path.Combine(planetSide, "startup.pak-out", "apackage.adb");
            var packages = NativeAnimationPackageCatalog.Parse(File.ReadAllBytes(packagePath))
                .ToDictionary(package => package.Name, StringComparer.OrdinalIgnoreCase);
            string[] available = GlbExportTool.LoadAnimationCatalog(planetSide)
                .Select(clip => clip.Name)
                .ToArray();
            var availableSet = available.ToHashSet(StringComparer.OrdinalIgnoreCase);
            string[] requiredAliases =
            [
                "rifle",
                "walkforward_rifle",
                "walkbackward_rifle",
                "runforward_rifle",
                "rifle_crouch",
                "crouchforward_rifle",
                "crouchbackward_rifle",
                "jumpup_rifle",
                "jumpidle_rifle",
                "jumpdown_rifle",
                "rifle_fire",
                "reload_rifle",
                "pain01_rifle",
                "death01_rifle",
            ];

            foreach (string packageName in new[] { "ncmlite", "ncflite" })
            {
                NativeAnimationPackage package = packages[packageName];
                foreach (string alias in requiredAliases)
                {
                    NativeAnimationEntry entry = Assert.Single(package.Animations, animation =>
                        animation.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase));
                    NativeAnimationClipSelection.Binding binding =
                        NativeAnimationClipSelection.Resolve(entry, available);
                    Assert.NotEmpty(binding.ConcreteClips);
                    Assert.All(binding.ConcreteClips, clip => Assert.Contains(clip, availableSet));
                }
            }

            NativeAnimationEntry malePain = Assert.Single(packages["ncmlite"].Animations,
                animation => animation.Alias.Equals("pain01_rifle", StringComparison.OrdinalIgnoreCase));
            NativeAnimationClipSelection.Binding malePainBinding =
                NativeAnimationClipSelection.Resolve(malePain, available);
            Assert.Equal("virtual-reference-poses", malePainBinding.Resolution);
            Assert.Contains("ncmlite_pain01_rifle_ref00", malePainBinding.ConcreteClips,
                StringComparer.OrdinalIgnoreCase);

            NativeAnimationEntry femaleWalkBack = Assert.Single(packages["ncflite"].Animations,
                animation => animation.Alias.Equals("walkbackward_rifle", StringComparison.OrdinalIgnoreCase));
            NativeAnimationClipSelection.Binding femaleWalkBackBinding =
                NativeAnimationClipSelection.Resolve(femaleWalkBack, available);
            Assert.Equal("concrete-reference-family", femaleWalkBackBinding.Resolution);
            Assert.Contains("ncflite_walkbackward_rifle_ref00", femaleWalkBackBinding.ConcreteClips,
                StringComparer.OrdinalIgnoreCase);
        }

        private static NativeAnimationEntry Entry(string animation, string? playbackMode) => new(
            animation,
            animation[(animation.IndexOf('_') + 1)..],
            true,
            "none",
            playbackMode,
            0,
            Array.Empty<string>());

        private static string? FindPlanetSideDirectory()
        {
            string? configured = Environment.GetEnvironmentVariable("PLANETSIDE_DIR");
            if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
                return Path.GetFullPath(configured);
            const string local = "/home/brynn/Downloads/PlanetSide";
            return Directory.Exists(local) ? local : null;
        }
    }
}
