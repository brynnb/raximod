using System;
using System.Collections.Generic;
using System.Text;

namespace Raximod.EngineAssets.Maps
{
    /// <summary>A named world object mapped to a relative-object composition definition.</summary>
    public readonly record struct ObjectLink(string ObjectName, string Definition);

    /// <summary>Parses the <c>pse_link object-name definition-name</c> records in objects_mapNN.lst.</summary>
    public static class ObjectLinkList
    {
        public static IReadOnlyList<ObjectLink> Parse(byte[] data)
        {
            var links = new List<ObjectLink>();
            string text = Encoding.ASCII.GetString(data);
            foreach (string raw in text.Split('\n'))
            {
                string[] tokens = raw.Split(
                    new[] { ' ', '\t', '\r' },
                    StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length < 3 ||
                    !tokens[0].Equals("pse_link", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                links.Add(new ObjectLink(tokens[1], tokens[2]));
            }
            return links;
        }
    }
}
