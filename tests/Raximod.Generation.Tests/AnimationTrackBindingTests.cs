using Raximod.Generation.Assets;
using Xunit;

namespace Raximod.Generation.Tests
{
    public sealed class AnimationTrackBindingTests
    {
        [Fact]
        public void ExplicitAliasMapsTrekFireTrackOntoDetailedSkeletonBone()
        {
            var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["t_remote_electronics_kit"] = "remote_electronics_kit",
            };

            Assert.Equal("remote_electronics_kit",
                AnimationTrackBinding.ResolveTarget("T_REMOTE_ELECTRONICS_KIT", aliases));
            Assert.Equal("bip01_r_hand", AnimationTrackBinding.ResolveTarget("bip01_r_hand", aliases));
        }
    }
}
