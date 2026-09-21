using System.Text;
using System.Text.Json;
using Raximod.EngineAssets.Databases;
using Xunit;

namespace Raximod.Generation.Tests
{
    public sealed class GameObjectDbTests
    {
        [Fact]
        public void PreservesRawCommandsNameIndexParentsAndByteExactRoundTrip()
        {
            byte[] fixture = Fixture(cycle: false);
            GameObjectDb database = GameObjectDb.Parse(fixture);

            Assert.Equal(fixture, database.Encode());
            Assert.Equal(fixture.Length, database.DecodedByteLength);
            Assert.NotEmpty(database.StringPoolBytes.ToArray());
            Assert.True(database.Diagnostics.RoundTripVerified);
            Assert.Equal(10, database.Diagnostics.CommandNodes);
            Assert.Equal(2, database.Diagnostics.ParentCommands);
            Assert.Equal(2u, database.NameIndexCount);
            Assert.Equal(0x11223344u, database.NameIndexReserved);
            Assert.Equal(7u, database.NameIndexVersion);
            Assert.Collection(
                database.NameIndex,
                child =>
                {
                    Assert.Equal(0, child.Index);
                    Assert.Equal("child_projectile", child.Name);
                    Assert.Equal(17u, child.RecordOffset);
                },
                parent =>
                {
                    Assert.Equal(1, parent.Index);
                    Assert.Equal("base_projectile", parent.Name);
                    Assert.Equal(1u, parent.RecordOffset);
                });

            Assert.Equal(10, database.Commands.Count);
            Assert.Equal(2, database.Commands.Count(command => command.IsSeparator));
            Assert.Equal(2, database.Commands.Count(command =>
                command.Name == "add_property"
                && command.Fields.Count >= 3
                && command.Fields[1] == "child_projectile"
                && command.Fields[2] == "damage"));
            Assert.Equal(2, database.ParentLinks.Count);
            GameObjectDb.ResourceParentLink link = database.ParentLinks[0];
            Assert.Equal("child_projectile", link.Child);
            Assert.Equal("game_objects", link.Resource);
            Assert.Equal("base_projectile", link.Parent);
            GameObjectDb.ResourceParentLink clear = database.ParentLinks[1];
            Assert.Equal("detached", clear.Child);
            Assert.Null(clear.Resource);
            Assert.Null(clear.Parent);
            GameObjectDb.RawCommand unknown = Assert.Single(
                database.Commands,
                command => command.Name == "future_command");
            Assert.Equal(new[] { "future_command", "payload", "payload" }, unknown.Fields);

            GameObjectDb.GameObject direct = Assert.Single(
                database.DirectObjects,
                item => item.Name == "child_projectile");
            Assert.False(direct.IsResolved);
            Assert.Equal("", direct.Type);
            Assert.Equal("21", Assert.Single(direct.Properties["damage"]));
            Assert.False(direct.Properties.ContainsKey("color"));
            Assert.Equal(new[] { "20", "21" }, direct.DirectPropertyOperations
                .Where(operation => operation.Name == "damage")
                .Select(operation => Assert.Single(operation.Values)).ToArray());
            Assert.Equal(5, direct.PropertySources["damage"].CommandIndex);
            Assert.Equal("child_projectile", direct.PropertySources["damage"].DefinedBy);

            GameObjectDb.GameObject resolved = database.Resolve("child_projectile");
            Assert.True(resolved.IsResolved);
            Assert.Equal("projectile", resolved.Type);
            Assert.Equal(new[] { "base_projectile", "child_projectile" }, resolved.InheritanceChain);
            Assert.Equal("21", Assert.Single(resolved.Properties["damage"]));
            Assert.Equal("blue", Assert.Single(resolved.Properties["color"]));
            Assert.Equal("base_projectile", resolved.PropertySources["color"].DefinedBy);
            Assert.Equal(2, resolved.PropertySources["color"].CommandIndex);
            Assert.Equal("child_projectile", resolved.PropertySources["damage"].DefinedBy);
            using JsonDocument provenance = JsonDocument.Parse(JsonSerializer.Serialize(resolved.Provenance));
            Assert.Equal("child_projectile", provenance.RootElement.GetProperty("definition").GetString());
            Assert.Equal("base_projectile", provenance.RootElement.GetProperty("parent").GetString());
            Assert.Equal(
                new[] { "base_projectile", "child_projectile" },
                provenance.RootElement.GetProperty("inheritanceChain").EnumerateArray()
                    .Select(value => value.GetString()).ToArray());
        }

        [Fact]
        public void RejectsInheritanceCyclesWithTheCompletePath()
        {
            InvalidDataException error = Assert.Throws<InvalidDataException>(
                () => GameObjectDb.Parse(Fixture(cycle: true)));
            Assert.Contains(
                "base_projectile -> child_projectile -> base_projectile",
                error.Message,
                StringComparison.Ordinal);
        }

        [Fact]
        public void RejectsMissingParentsInsteadOfSilentlyFlatteningIncompleteData()
        {
            InvalidDataException error = Assert.Throws<InvalidDataException>(
                () => GameObjectDb.Parse(Fixture(cycle: false, missingParent: true)));
            Assert.Contains("missing_parent", error.Message, StringComparison.Ordinal);
            Assert.Contains("child_projectile", error.Message, StringComparison.Ordinal);
        }

        private static byte[] Fixture(bool cycle, bool missingParent = false)
        {
            string[] symbols =
            [
                "add_property",
                "set_resource_parent",
                "base_projectile",
                "child_projectile",
                "type",
                "projectile",
                "damage",
                "10",
                "20",
                "21",
                "color",
                "blue",
                "game_objects",
                "future_command",
                "payload",
                "detached",
                "missing_parent",
            ];
            var offsets = new Dictionary<string, uint>(StringComparer.Ordinal);
            using var pool = new MemoryStream();
            foreach (string symbol in symbols)
            {
                offsets[symbol] = checked((uint)pool.Position);
                pool.Write(Encoding.Latin1.GetBytes(symbol));
                pool.WriteByte(0);
            }

            using var output = new MemoryStream();
            output.Write(Encoding.ASCII.GetBytes("chunky"));
            output.Write(Encoding.ASCII.GetBytes("asciidatabase"));
            output.WriteByte(0);
            WriteU32(output, 0x01020304);
            WriteU32(output, 0);
            output.Write(Encoding.ASCII.GetBytes("add_resource"));
            output.WriteByte(0);
            WriteU32(output, checked((uint)pool.Length));
            pool.Position = 0;
            pool.CopyTo(output);

            WriteU32(output, 2);
            WriteU32(output, 0x11223344);
            WriteU32(output, 7);
            WriteU32(output, offsets["child_projectile"]);
            // Record offsets are one-based 32-bit command-stream word offsets.
            // The child starts after three five-word commands plus a separator.
            WriteU32(output, 17);
            WriteU32(output, offsets["base_projectile"]);
            WriteU32(output, 1);

            Command(output, offsets, "add_property", "base_projectile", "type", "projectile");
            Command(output, offsets, "add_property", "base_projectile", "damage", "10");
            Command(output, offsets, "add_property", "base_projectile", "color", "blue");
            Separator(output);
            Command(output, offsets, "add_property", "child_projectile", "damage", "20");
            Command(output, offsets, "add_property", "child_projectile", "damage", "21");
            Command(
                output,
                offsets,
                "set_resource_parent",
                "child_projectile",
                "game_objects",
                missingParent ? "missing_parent" : "base_projectile");
            Command(output, offsets, "future_command", "payload", "payload");
            Command(output, offsets, "set_resource_parent", "detached");
            if (cycle)
                Command(output, offsets, "set_resource_parent", "base_projectile", "game_objects", "child_projectile");
            Separator(output);
            return output.ToArray();
        }

        private static void Command(
            Stream output,
            IReadOnlyDictionary<string, uint> offsets,
            params string[] fields)
        {
            WriteU32(output, checked((uint)fields.Length));
            foreach (string field in fields) WriteU32(output, offsets[field]);
        }

        private static void Separator(Stream output) => WriteU32(output, 0);

        private static void WriteU32(Stream output, uint value)
        {
            output.WriteByte((byte)value);
            output.WriteByte((byte)(value >> 8));
            output.WriteByte((byte)(value >> 16));
            output.WriteByte((byte)(value >> 24));
        }
    }
}
