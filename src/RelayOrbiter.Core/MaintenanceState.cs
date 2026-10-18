using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RelayOrbiter.Core
{
    // What a satellite still has to do to be back in its slot, in order.
    // Plane before a transfer that lowers the orbit, PlaneLate after one that raises it
    // (plane changes are cheaper high up). Apsis: an elliptical slot's apoapsis is
    // raised where its periapsis must be.
    public enum CorrectionStage { Plane, Transfer, Circularize, PlaneLate, Apsis, Phase, PhaseReturn, Trim, Done }

    // The slots, frozen when a correction starts: slot k's mean longitude advances at
    // the target period, so every satellite can be phased against it independently
    // (and in parallel), whatever the others are doing meanwhile.
    public sealed class SlotFrame
    {
        public SlotFrame(int slots, double offsetDeg, double epochUT, double period)
        {
            Slots     = slots;
            OffsetDeg = offsetDeg;
            EpochUT   = epochUT;
            Period    = period;
        }

        public int    Slots     { get; }
        public double OffsetDeg { get; }  // slot 0's mean longitude at EpochUT
        public double EpochUT   { get; }
        public double Period    { get; }  // target period

        public double SlotLongitudeDeg(int slot, double ut) =>
            StationKeeping.Mod360(OffsetDeg + 360.0 * (ut - EpochUT) / Period + slot * 360.0 / Slots);

        // −180..180; positive = ahead of the slot.
        public double ErrorDeg(double longitudeDeg, int slot, double ut) =>
            StationKeeping.Wrap180(longitudeDeg - SlotLongitudeDeg(slot, ut));
    }

    // A whole-constellation correction, as plain values so it survives the scene
    // reloads that switching to a far (unloaded) satellite causes.
    public sealed class MaintenanceState
    {
        public const int MaxPhaseAttempts = 2;  // phase again once if the first left it off

        public uint[]   Members     { get; set; } = new uint[0];  // vessel persistentIds
        public int[]    SlotOf      { get; set; } = new int[0];
        public int      Slots       { get; set; }
        public double   OffsetDeg   { get; set; }
        public double   EpochUT     { get; set; }
        public double   TargetSma   { get; set; }
        public double   TargetPeriod { get; set; }
        public int      Reference   { get; set; } = -1;   // member that stays put; -1: best fit
        public int      MinOrbits   { get; set; } = 1;
        public double   MinRadius   { get; set; }
        public double   MaxRadius   { get; set; }
        public CorrectionStage[] Stage { get; set; } = new CorrectionStage[0];
        public double[] PhaseUT     { get; set; } = new double[0];
        public int[]    PhaseOrbits { get; set; } = new int[0];
        public int[]    Attempts    { get; set; } = new int[0];
        public int      Current     { get; set; } = -1;   // member being switched to or burning
        public int      BurnsDone   { get; set; }
        public bool     MatchPlanes { get; set; }             // rotate every orbit into one plane first
        public double   PlaneInclination { get; set; }        // that plane (degrees)
        public double   PlaneLan    { get; set; }

        public OrbitPlane Plane => new OrbitPlane(PlaneInclination, PlaneLan);

        // A shape other than a ring: every member has its own target orbit and phase
        // offset, and phases are mean arguments of latitude (ω + M) in its own plane.
        public bool     Shaped      { get; set; }
        public double[] TargetInc   { get; set; } = new double[0];
        public double[] TargetLan   { get; set; } = new double[0];
        public double[] TargetSmas  { get; set; } = new double[0];
        public double[] TargetEcc   { get; set; } = new double[0];
        public double[] TargetArgPe { get; set; } = new double[0];
        public double[] PhaseOffset { get; set; } = new double[0];
        public string?  AnchorBody  { get; set; }             // Trojans: phases follow this moon

        public OrbitPlane TargetPlane(int i) =>
            Shaped ? new OrbitPlane(TargetInc[i], TargetLan[i]) : Plane;

        public bool TargetElliptical(int i) => Shaped && TargetEcc[i] > StationKeeping.CircularEccentricity;

        // Where member i's slot is at ut (mean argument of latitude, or longitude for a
        // ring); anchorPhase: the moon's own at ut, for Trojans.
        public double SlotPhaseDeg(int i, double ut, double anchorPhase = double.NaN) =>
            !Shaped ? Frame.SlotLongitudeDeg(SlotOf[i], ut)
            : StationKeeping.Mod360((AnchorBody != null ? anchorPhase : OffsetDeg + 360.0 * (ut - EpochUT) / TargetPeriod)
                                    + PhaseOffset[i]);

        // A shape's correction: members into the slots of `fit`, every one into its own plane.
        public static MaintenanceState StartShaped(IReadOnlyList<uint> members, ShapeAssignment fit, double epochUT,
                                                   double targetPeriod, int? reference, int minOrbits,
                                                   double minRadius, double maxRadius, string? anchorBody = null)
        {
            int m = members.Count;
            var stage = Enumerable.Repeat(CorrectionStage.Plane, m).ToArray();
            if (reference.HasValue) stage[reference.Value] = CorrectionStage.Done;
            var slots = Enumerable.Range(0, m).Select(fit.SlotFor).ToArray();
            return new MaintenanceState
            {
                Members      = members.ToArray(),
                SlotOf       = fit.SlotOf.ToArray(),
                Slots        = fit.Slots.Count,
                OffsetDeg    = fit.Theta0Deg,
                EpochUT      = epochUT,
                TargetSma    = slots.Length > 0 ? slots[0].Sma : 0.0,
                TargetPeriod = targetPeriod,
                Reference    = reference ?? -1,
                MinOrbits    = minOrbits,
                MinRadius    = minRadius,
                MaxRadius    = maxRadius,
                Stage        = stage,
                PhaseUT      = Enumerable.Repeat(double.NaN, m).ToArray(),
                PhaseOrbits  = new int[m],
                Attempts     = new int[m],
                MatchPlanes  = true,
                Shaped       = true,
                TargetInc    = Enumerable.Range(0, m).Select(i => fit.PlaneFor(i).InclinationDeg).ToArray(),
                TargetLan    = Enumerable.Range(0, m).Select(i => fit.PlaneFor(i).LanDeg).ToArray(),
                TargetSmas   = slots.Select(s => s.Sma).ToArray(),
                TargetEcc    = slots.Select(s => s.Eccentricity).ToArray(),
                TargetArgPe  = slots.Select(s => s.ArgPeDeg).ToArray(),
                PhaseOffset  = slots.Select(s => s.PhaseOffsetDeg).ToArray(),
                AnchorBody   = anchorBody,
            };
        }

        // Burns member i may still need from its stage (an upper bound: stages it
        // turns out not to need are skipped).
        public int BurnsLeft(int i) => Stage[i] switch
        {
            CorrectionStage.Plane       => 7,
            CorrectionStage.Transfer    => 6,
            CorrectionStage.Circularize => 5,
            CorrectionStage.PlaneLate   => 5,
            CorrectionStage.Apsis       => 4,
            CorrectionStage.Phase       => 3,
            CorrectionStage.PhaseReturn => 2,
            CorrectionStage.Trim        => 1,
            _                           => 0,
        };

        // Burns done plus those that may be left (shrinks as stages are skipped).
        public int BurnsEstimate => BurnsDone + Enumerable.Range(0, Members.Length).Sum(BurnsLeft);

        // 0..1: burns done over the estimate.
        public double Progress => BurnsEstimate == 0 ? 1.0 : (double)BurnsDone / BurnsEstimate;

        public SlotFrame Frame => new SlotFrame(Slots, OffsetDeg, EpochUT, TargetPeriod);

        public static MaintenanceState Start(IReadOnlyList<uint> members, SlotFit fit, double epochUT,
                                             double targetSma, double targetPeriod, int? reference,
                                             int minOrbits, double minRadius, double maxRadius,
                                             OrbitPlane? plane = null)
        {
            int m = members.Count;
            var stage = Enumerable.Repeat(plane != null ? CorrectionStage.Plane : CorrectionStage.Transfer, m).ToArray();
            if (reference.HasValue) stage[reference.Value] = CorrectionStage.Done;
            return new MaintenanceState
            {
                Members      = members.ToArray(),
                SlotOf       = fit.SlotOf.ToArray(),
                Slots        = fit.Slots,
                OffsetDeg    = fit.OffsetDeg,
                EpochUT      = epochUT,
                TargetSma    = targetSma,
                TargetPeriod = targetPeriod,
                Reference    = reference ?? -1,
                MinOrbits    = minOrbits,
                MinRadius    = minRadius,
                MaxRadius    = maxRadius,
                Stage        = stage,
                PhaseUT      = Enumerable.Repeat(double.NaN, m).ToArray(),
                PhaseOrbits  = new int[m],
                Attempts     = new int[m],
                MatchPlanes  = plane != null,
                PlaneInclination = plane?.InclinationDeg ?? 0.0,
                PlaneLan     = plane?.LanDeg ?? 0.0,
            };
        }

        public bool AllDone => Stage.All(s => s == CorrectionStage.Done);

        // The stage after `stage` for member i. After the trim, phase again when the
        // satellite is still off its slot (a missed phasing return, say), once.
        public void Advance(int i, bool phaseOk)
        {
            var s = Stage[i];
            if (s == CorrectionStage.Phase) Attempts[i]++;
            if (s == CorrectionStage.Trim)
            {
                Stage[i] = !phaseOk && Attempts[i] < MaxPhaseAttempts ? CorrectionStage.Phase : CorrectionStage.Done;
                if (Stage[i] == CorrectionStage.Phase) PhaseUT[i] = double.NaN;
                return;
            }
            if (s != CorrectionStage.Done) Stage[i] = s + 1;
        }

        // ── Save file ────────────────────────────────────────────────────────

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public IDictionary<string, string> ToValues() => new Dictionary<string, string>
        {
            ["Members"]      = string.Join(",", Members.Select(x => x.ToString(Inv))),
            ["SlotOf"]       = string.Join(",", SlotOf.Select(x => x.ToString(Inv))),
            ["Slots"]        = Slots.ToString(Inv),
            ["OffsetDeg"]    = Num(OffsetDeg),
            ["EpochUT"]      = Num(EpochUT),
            ["TargetSma"]    = Num(TargetSma),
            ["TargetPeriod"] = Num(TargetPeriod),
            ["Reference"]    = Reference.ToString(Inv),
            ["MinOrbits"]    = MinOrbits.ToString(Inv),
            ["MinRadius"]    = Num(MinRadius),
            ["MaxRadius"]    = Num(MaxRadius),
            ["Stage"]        = string.Join(",", Stage.Select(s => s.ToString())),
            ["PhaseUT"]      = string.Join(",", PhaseUT.Select(Num)),
            ["PhaseOrbits"]  = string.Join(",", PhaseOrbits.Select(x => x.ToString(Inv))),
            ["Attempts"]     = string.Join(",", Attempts.Select(x => x.ToString(Inv))),
            ["Current"]      = Current.ToString(Inv),
            ["BurnsDone"]    = BurnsDone.ToString(Inv),
            ["MatchPlanes"]  = MatchPlanes.ToString(Inv),
            ["PlaneInclination"] = Num(PlaneInclination),
            ["PlaneLan"]     = Num(PlaneLan),
            ["Shaped"]       = Shaped.ToString(Inv),
            ["TargetInc"]    = string.Join(",", TargetInc.Select(Num)),
            ["TargetLan"]    = string.Join(",", TargetLan.Select(Num)),
            ["TargetSmas"]   = string.Join(",", TargetSmas.Select(Num)),
            ["TargetEcc"]    = string.Join(",", TargetEcc.Select(Num)),
            ["TargetArgPe"]  = string.Join(",", TargetArgPe.Select(Num)),
            ["PhaseOffset"]  = string.Join(",", PhaseOffset.Select(Num)),
            ["AnchorBody"]   = AnchorBody == null ? "" : Uri.EscapeDataString(AnchorBody),
        };

        public static MaintenanceState FromValues(IReadOnlyDictionary<string, string> values)
        {
            string Get(string key) =>
                values.TryGetValue(key, out var s) ? s : throw new FormatException($"Missing {key}.");
            string[] List(string key) => Get(key).Length == 0 ? new string[0] : Get(key).Split(',');
            double[] Optional(string key) =>
                values.TryGetValue(key, out var v) && v.Length > 0 ? v.Split(',').Select(Parse).ToArray() : new double[0];

            var state = new MaintenanceState
            {
                Members      = List("Members").Select(x => uint.Parse(x, Inv)).ToArray(),
                SlotOf       = List("SlotOf").Select(x => int.Parse(x, Inv)).ToArray(),
                Slots        = int.Parse(Get("Slots"), Inv),
                OffsetDeg    = Parse(Get("OffsetDeg")),
                EpochUT      = Parse(Get("EpochUT")),
                TargetSma    = Parse(Get("TargetSma")),
                TargetPeriod = Parse(Get("TargetPeriod")),
                Reference    = int.Parse(Get("Reference"), Inv),
                MinOrbits    = int.Parse(Get("MinOrbits"), Inv),
                MinRadius    = Parse(Get("MinRadius")),
                MaxRadius    = Parse(Get("MaxRadius")),
                Stage        = List("Stage").Select(x => (CorrectionStage)Enum.Parse(typeof(CorrectionStage), x)).ToArray(),
                PhaseUT      = List("PhaseUT").Select(Parse).ToArray(),
                PhaseOrbits  = List("PhaseOrbits").Select(x => int.Parse(x, Inv)).ToArray(),
                Attempts     = List("Attempts").Select(x => int.Parse(x, Inv)).ToArray(),
                Current      = int.Parse(Get("Current"), Inv),
                BurnsDone    = values.TryGetValue("BurnsDone", out var b) ? int.Parse(b, Inv) : 0,
                MatchPlanes  = values.TryGetValue("MatchPlanes", out var mp) && bool.Parse(mp),
                PlaneInclination = values.TryGetValue("PlaneInclination", out var pi) ? Parse(pi) : 0.0,
                PlaneLan     = values.TryGetValue("PlaneLan", out var pl) ? Parse(pl) : 0.0,
                Shaped       = values.TryGetValue("Shaped", out var sh) && bool.Parse(sh),
                TargetInc    = Optional("TargetInc"),
                TargetLan    = Optional("TargetLan"),
                TargetSmas   = Optional("TargetSmas"),
                TargetEcc    = Optional("TargetEcc"),
                TargetArgPe  = Optional("TargetArgPe"),
                PhaseOffset  = Optional("PhaseOffset"),
                AnchorBody   = values.TryGetValue("AnchorBody", out var ab) && ab.Length > 0 ? Uri.UnescapeDataString(ab) : null,
            };
            int m = state.Members.Length;
            if (new[] { state.SlotOf.Length, state.Stage.Length, state.PhaseUT.Length,
                        state.PhaseOrbits.Length, state.Attempts.Length }.Any(n => n != m))
                throw new FormatException("Inconsistent member lists.");
            if (state.Shaped && new[] { state.TargetInc.Length, state.TargetLan.Length, state.TargetSmas.Length,
                                        state.TargetEcc.Length, state.TargetArgPe.Length, state.PhaseOffset.Length }
                                    .Any(n => n != m))
                throw new FormatException("Inconsistent shape lists.");
            return state;
        }

        // "R" round-trips doubles; NaN and ±∞ as their invariant names.
        private static string Num(double x) => x.ToString("R", Inv);

        private static double Parse(string s) => double.Parse(s, NumberStyles.Float, Inv);
    }
}
