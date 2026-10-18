using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace RelayOrbiter.Core
{
    // A satellite name in our scheme, "{body} {callsign} {k}/{N} - {tag}".
    public sealed class ConstellationName
    {
        private static readonly Regex Pattern = new Regex(
            @"^(?<body>.+) (?<callsign>(?:" + string.Join("|", ConstellationNaming.Callsigns) + @")(?: [IVX]+)?) " +
            @"(?<k>\d+)/(?<n>\d+)(?: - (?<tag>.*))?$",
            RegexOptions.CultureInvariant);

        private ConstellationName(string body, string callsign, int index, int count, string tag)
        {
            Body     = body;
            Callsign = callsign;
            Index    = index;
            Count    = count;
            Tag      = tag;
        }

        public string Body     { get; }
        public string Callsign { get; }
        public int    Index    { get; }  // 1-based
        public int    Count    { get; }  // N
        public string Tag      { get; }  // "SYNC", "1000K", "SYNC i10"; "" when missing

        public static ConstellationName? Parse(string vesselName)
        {
            var m = Pattern.Match(vesselName.Trim());
            if (!m.Success) return null;
            int k = int.Parse(m.Groups["k"].Value, CultureInfo.InvariantCulture);
            int n = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
            if (n < 1 || k < 1 || k > n) return null;
            return new ConstellationName(m.Groups["body"].Value, m.Groups["callsign"].Value, k, n,
                                         m.Groups["tag"].Value);
        }

        // Same constellation: same body, callsign and size.
        public bool SameGroup(ConstellationName other) =>
            other.Body == Body && other.Callsign == Callsign && other.Count == Count;
    }

    // The constellation the active vessel seems to belong to, deduced from names.
    public sealed class GroupSuggestion
    {
        public GroupSuggestion(string label, int count, IReadOnlyList<string> members, bool fromOurNames)
        {
            Label        = label;
            Count        = count;
            Members      = members;
            FromOurNames = fromOurNames;
        }

        public string Label        { get; }  // "Kerbin Orion" or "Kerbin Sat"
        public int    Count        { get; }  // suggested N (slots)
        public IReadOnlyList<string> Members { get; }  // vessel names, active one included
        public bool   FromOurNames { get; }  // false: guessed from a trailing number
    }

    public static class ConstellationGroup
    {
        // "Kerbin Sat 3", "Relay-12": a prefix and a trailing number.
        private static readonly Regex Numbered = new Regex(@"^(?<prefix>.*?)[\s#_-]*(?<k>\d+)$",
                                                           RegexOptions.CultureInvariant);

        // Our naming scheme first; otherwise names that only differ in a trailing number.
        // candidates: the vessels to choose from (usually those orbiting the same body).
        public static GroupSuggestion? Suggest(string activeName, IEnumerable<string> candidates)
        {
            var names = candidates.Concat(new[] { activeName }).Distinct().ToList();

            var ours = ConstellationName.Parse(activeName);
            if (ours != null)
            {
                var members = names.Where(n => ConstellationName.Parse(n) is { } p && p.SameGroup(ours))
                                   .OrderBy(n => ConstellationName.Parse(n)!.Index).ToList();
                return new GroupSuggestion($"{ours.Body} {ours.Callsign}", ours.Count, members, true);
            }

            var key = Numbered.Match(activeName.Trim());
            if (!key.Success || key.Groups["prefix"].Value.Trim().Length == 0) return null;
            string prefix = key.Groups["prefix"].Value.Trim();
            var numbered = new List<(string name, int k)>();
            foreach (var n in names)
            {
                var m = Numbered.Match(n.Trim());
                if (m.Success && string.Equals(m.Groups["prefix"].Value.Trim(), prefix, StringComparison.Ordinal))
                    numbered.Add((n, int.Parse(m.Groups["k"].Value, CultureInfo.InvariantCulture)));
            }
            if (numbered.Count < 2) return null;
            int count = Math.Max(numbered.Count, numbered.Max(x => x.k));
            count = Math.Min(Math.Max(count, Resonance.MinSatellites), Resonance.MaxSatellites);
            return new GroupSuggestion(prefix, count,
                                       numbered.OrderBy(x => x.k).Select(x => x.name).ToList(), false);
        }
    }
}
