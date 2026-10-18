using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RelayOrbiter.Core
{
    // Everything needed to resume a deployment after the game reloads the flight
    // scene, as plain values (saved in the game's save file by the plugin).
    public sealed class DeploymentState
    {
        public int      Step            { get; set; }          // index of the step to run
        public uint     Carrier         { get; set; }          // vessel persistentId
        public uint[]   Separators      { get; set; } = new uint[0];
        public uint[]   CommandParts    { get; set; } = new uint[0];  // identify each satellite
        public uint[][] SatelliteParts  { get; set; } = new uint[0][];
        public string[]? Names          { get; set; }
        public bool     ExtendPanels    { get; set; }
        public bool     TopUp           { get; set; }
        public ParkingOrbit Parking     { get; set; }
        public double   GoalRadius      { get; set; }
        public double?  GoalInclination { get; set; }
        public double   ParkingUT       { get; set; } = double.NaN;
        public int      LastReleased    { get; set; } = -1;
        public double   CarrierBurn     { get; set; }
        public double[] SatelliteBurns  { get; set; } = new double[0];
        public bool     Transfer        { get; set; }
        public bool     PlaneChange     { get; set; }
        public bool     PlaneChangeFirst { get; set; }
        public string?  Destination     { get; set; }          // moon to travel to first (body name); null: stay
        public float    SavedLoad       { get; set; }          // carrier's own load range, to restore
        public float    SavedUnload     { get; set; }

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // Values safe for KSP's ConfigNode: invariant numbers, names percent-encoded
        // (so commas, semicolons or "//" in a name can't break the save file).
        public IDictionary<string, string> ToValues()
        {
            var v = new Dictionary<string, string>
            {
                ["Step"]             = Step.ToString(Inv),
                ["Carrier"]          = Carrier.ToString(Inv),
                ["Separators"]       = Join(Separators),
                ["CommandParts"]     = Join(CommandParts),
                ["SatelliteParts"]   = string.Join(";", SatelliteParts.Select(Join)),
                ["ExtendPanels"]     = ExtendPanels.ToString(Inv),
                ["TopUp"]            = TopUp.ToString(Inv),
                ["Parking"]          = Parking.ToString(),
                ["GoalRadius"]       = Num(GoalRadius),
                ["ParkingUT"]        = Num(ParkingUT),
                ["LastReleased"]     = LastReleased.ToString(Inv),
                ["CarrierBurn"]      = Num(CarrierBurn),
                ["SatelliteBurns"]   = string.Join(",", SatelliteBurns.Select(Num)),
                ["Transfer"]         = Transfer.ToString(Inv),
                ["PlaneChange"]      = PlaneChange.ToString(Inv),
                ["PlaneChangeFirst"] = PlaneChangeFirst.ToString(Inv),
                ["SavedLoad"]        = SavedLoad.ToString("R", Inv),
                ["SavedUnload"]      = SavedUnload.ToString("R", Inv),
            };
            if (Names != null) v["Names"] = string.Join(",", Names.Select(Uri.EscapeDataString));
            if (GoalInclination.HasValue) v["GoalInclination"] = Num(GoalInclination.Value);
            if (Destination != null) v["Destination"] = Uri.EscapeDataString(Destination);
            return v;
        }

        public static DeploymentState FromValues(IReadOnlyDictionary<string, string> values)
        {
            string Get(string key) =>
                values.TryGetValue(key, out var s) ? s : throw new FormatException($"Missing {key}.");

            return new DeploymentState
            {
                Step             = int.Parse(Get("Step"), Inv),
                Carrier          = uint.Parse(Get("Carrier"), Inv),
                Separators       = Ids(Get("Separators")),
                CommandParts     = Ids(Get("CommandParts")),
                SatelliteParts   = Get("SatelliteParts").Split(';').Select(Ids).ToArray(),
                Names            = values.TryGetValue("Names", out var n)
                                   ? n.Split(',').Select(Uri.UnescapeDataString).ToArray() : null,
                ExtendPanels     = bool.Parse(Get("ExtendPanels")),
                TopUp            = bool.Parse(Get("TopUp")),
                Parking          = (ParkingOrbit)Enum.Parse(typeof(ParkingOrbit), Get("Parking")),
                GoalRadius       = ParseNum(Get("GoalRadius")),
                GoalInclination  = values.TryGetValue("GoalInclination", out var i) ? ParseNum(i) : (double?)null,
                ParkingUT        = ParseNum(Get("ParkingUT")),
                LastReleased     = int.Parse(Get("LastReleased"), Inv),
                CarrierBurn      = ParseNum(Get("CarrierBurn")),
                SatelliteBurns   = Get("SatelliteBurns").Length == 0 ? new double[0]
                                   : Get("SatelliteBurns").Split(',').Select(ParseNum).ToArray(),
                Transfer         = bool.Parse(Get("Transfer")),
                PlaneChange      = bool.Parse(Get("PlaneChange")),
                PlaneChangeFirst = bool.Parse(Get("PlaneChangeFirst")),
                Destination      = values.TryGetValue("Destination", out var d) ? Uri.UnescapeDataString(d) : null,
                SavedLoad        = float.Parse(Get("SavedLoad"), Inv),
                SavedUnload      = float.Parse(Get("SavedUnload"), Inv),
            };
        }

        private static string Num(double x) => x.ToString("R", Inv);
        private static double ParseNum(string s) => double.Parse(s, NumberStyles.Float, Inv);
        private static string Join(uint[] ids) => string.Join(",", ids.Select(id => id.ToString(Inv)));
        private static uint[] Ids(string s) =>
            s.Length == 0 ? new uint[0] : s.Split(',').Select(x => uint.Parse(x, Inv)).ToArray();
    }
}
