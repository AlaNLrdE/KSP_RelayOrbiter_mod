using System.Collections.Generic;
using System.Linq;
using RelayOrbiter.Core;
using CorePart = RelayOrbiter.Core.PartInfo;  // KSP has its own global PartInfo

namespace RelayOrbiter.Plugin
{
    // Bridges KSP parts and the KSP-independent PartTree model, and performs
    // the part-level actions of a deployment (release, engine activation).
    internal static class VesselParts
    {
        public static List<CorePart> From(Vessel vessel) => vessel.parts.Select(ToInfo).ToList();

        public static Part? Find(Vessel vessel, uint flightId) =>
            vessel.parts.FirstOrDefault(p => p.flightID == flightId);

        // The satellite's command part: it identifies the satellite's vessel after release.
        public static Part? CommandPart(Vessel vessel, SatelliteCandidate c) =>
            c.PartIds.Select(id => Find(vessel, id))
                     .FirstOrDefault(p => p != null && p.FindModuleImplementing<ModuleCommand>() != null);

        // Highest SAS service level among the given parts (-1 when none has SAS).
        public static int SasLevel(Vessel vessel, IEnumerable<uint> partIds) =>
            partIds.Select(id => Find(vessel, id))
                   .Where(p => p != null)
                   .SelectMany(p => p!.FindModulesImplementing<ModuleSAS>())
                   .Select(m => m.SASServiceLevel)
                   .DefaultIfEmpty(-1)
                   .Max();

        // Fire a decoupler or undock a docking port. False if the part can't separate.
        public static bool Release(Part separator)
        {
            var decoupler = separator.FindModuleImplementing<ModuleDecouplerBase>();
            if (decoupler != null && !decoupler.isDecoupled)
            {
                decoupler.Decouple();
                return true;
            }
            var port = separator.FindModuleImplementing<ModuleDockingNode>();
            if (port != null)
            {
                if (IsDockedPair(port)) port.Undock();
                else                    port.Decouple();
                return true;
            }
            return false;
        }

        // Docked in flight to another port, as opposed to joined in the editor.
        private static bool IsDockedPair(ModuleDockingNode port) =>
            port.otherNode != null && port.state != null && port.state.StartsWith("Docked");

        // One line per separator, for KSP.log: helps diagnose satellites not being found.
        public static string DescribeSeparators(Vessel vessel)
        {
            var lines = new List<string>();
            foreach (var p in vessel.parts)
            {
                var port = p.FindModuleImplementing<ModuleDockingNode>();
                var dec  = p.FindModuleImplementing<ModuleDecouplerBase>();
                if (port == null && dec == null) continue;
                string face = port != null ? Title(port.referenceNode?.attachedPart)
                                           : Title(dec!.ExplosiveNode?.attachedPart);
                lines.Add($"{p.partInfo.title} [{p.flightID}] " +
                          (port != null ? $"port state={port.state} pair={IsDockedPair(port)}"
                                        : $"decoupler decoupled={dec!.isDecoupled}") +
                          $" parent={Title(p.parent)} face={face}");
            }
            return string.Join("; ", lines);
        }

        private static string Title(Part? p) => p != null ? $"{p.partInfo.title} [{p.flightID}]" : "none";

        // Extend the retracted, animated solar panels among the given parts.
        // Returns how many started extending.
        public static int ExtendSolarPanels(Vessel vessel, IEnumerable<uint> partIds)
        {
            int n = 0;
            foreach (var p in partIds.Select(id => Find(vessel, id)).Where(p => p != null))
                foreach (var panel in p!.FindModulesImplementing<ModuleDeployableSolarPanel>())
                    if (panel.useAnimation && panel.deployState == ModuleDeployablePart.DeployState.RETRACTED)
                    {
                        panel.Extend();
                        ++n;
                    }
            return n;
        }

        // Fuel containers among the given parts: transferable resources other than
        // electric charge (that excludes solid fuel, ablator and the like).
        public static List<TankInfo> Tanks(Vessel vessel, IEnumerable<uint> partIds)
        {
            var tanks = new List<TankInfo>();
            foreach (var p in partIds.Select(id => Find(vessel, id)).Where(p => p != null))
                foreach (var r in p!.Resources)
                    if (IsFuel(r)) tanks.Add(new TankInfo(p.flightID, r.resourceName, r.amount, r.maxAmount));
            return tanks;
        }

        // Moves resources between parts of the vessel. Totals are conserved.
        public static void Apply(Vessel vessel, IEnumerable<ResourceTransfer> transfers)
        {
            foreach (var t in transfers)
            {
                var from = Find(vessel, t.From)?.Resources[t.Resource];
                var to   = Find(vessel, t.To)?.Resources[t.Resource];
                if (from == null || to == null) continue;
                double amount = System.Math.Min(t.Amount, System.Math.Min(from.amount, to.maxAmount - to.amount));
                from.amount -= amount;
                to.amount   += amount;
            }
        }

        // Δv of the carrier on its own: Isp of its running engines, the propellant in its own
        // tanks (outside satelliteIds) minus what is moved to the satellites, and the
        // vessel's whole mass, satellites included (the worst case).
        public static double CarrierDeltaV(Vessel vessel, ISet<uint> satelliteIds,
                                           IEnumerable<ResourceTransfer> toSatellites, double isp)
        {
            var propellants = new HashSet<string>(vessel.FindPartModulesImplementing<ModuleEngines>()
                .Where(e => e.isEnabled && e.EngineIgnited && !satelliteIds.Contains(e.part.flightID))
                .SelectMany(e => e.propellants.Select(p => p.name)));
            double fuel = 0.0;
            foreach (var p in vessel.parts.Where(p => !satelliteIds.Contains(p.flightID)))
                foreach (var r in p.Resources)
                    if (propellants.Contains(r.resourceName)) fuel += r.amount * r.info.density;
            foreach (var t in toSatellites.Where(t => propellants.Contains(t.Resource)))
                fuel -= t.Amount * PartResourceLibrary.Instance.GetDefinition(t.Resource).density;
            double wet = vessel.GetTotalMass();
            return BurnTiming.DeltaV(isp, wet, wet - System.Math.Max(0.0, fuel));
        }

        private static bool IsFuel(PartResource r) =>
            r.resourceName != "ElectricCharge" && r.info.resourceTransferMode != ResourceTransferMode.NONE;

        public static void ActivateEngines(Vessel vessel)
        {
            foreach (var e in vessel.FindPartModulesImplementing<ModuleEngines>())
                if (e.isEnabled && !e.EngineIgnited) e.Activate();
        }

        // Thrust (kN, vacuum) and thrust-weighted Isp (s) of the vessel's running engines.
        public static (double thrust, double isp) ActiveEngines(Vessel vessel)
        {
            double f = 0.0, flow = 0.0;
            foreach (var e in vessel.FindPartModulesImplementing<ModuleEngines>())
            {
                if (!e.isEnabled || !e.EngineIgnited || !e.isOperational || e.flameout) continue;
                double thrust = e.maxThrust * e.thrustPercentage / 100.0;
                double isp = e.atmosphereCurve.Evaluate(0f);
                if (thrust <= 0.0 || isp <= 0.0) continue;
                f += thrust;
                flow += thrust / isp;
            }
            return (f, flow > 0.0 ? f / flow : 0.0);
        }

        private static CorePart ToInfo(Part p)
        {
            var kind = SeparatorKind.None;
            bool fromParent = false;
            uint? child = null;

            var decoupler = p.FindModuleImplementing<ModuleDecouplerBase>();
            var port      = p.FindModuleImplementing<ModuleDockingNode>();
            if (decoupler != null && !decoupler.isDecoupled)
            {
                kind = SeparatorKind.Decoupler;
                var node = decoupler.ExplosiveNode;
                var other = node?.attachedPart;
                if (decoupler.isOmniDecoupler || decoupler is ModuleAnchoredDecoupler && other == null)
                    fromParent = true;                         // radial: leaves with what it holds
                else if (other != null && other == p.parent)
                    fromParent = true;
                else if (other != null && other.parent == p)
                    child = other.flightID;
            }
            else if (port != null && port.state != "Docked (same vessel)")
            {
                kind = SeparatorKind.DockingPort;
                // Not docked to another port (joined in the editor): the port works like a
                // decoupler and Decouple() detaches the part on its docking face.
                // Docked to another port in flight: PartTree uses the pair.
                if (!IsDockedPair(port))
                {
                    var other = port.referenceNode?.attachedPart;
                    if (other != null && other == p.parent)      fromParent = true;
                    else if (other != null && other.parent == p) child = other.flightID;
                }
            }

            var engine = p.FindModulesImplementing<ModuleEngines>().FirstOrDefault(e => e.isEnabled);
            double thrust = engine != null ? engine.maxThrust * engine.thrustPercentage / 100.0 : 0.0;
            double isp    = engine != null ? engine.atmosphereCurve.Evaluate(0f) : 0.0;

            return new CorePart(
                p.flightID, p.parent != null ? p.parent.flightID : (uint?)null,
                p.partInfo.title, kind, fromParent, child,
                isCommand:    p.FindModuleImplementing<ModuleCommand>() != null,
                mass:         p.mass + p.GetResourceMass(),
                thrust:       thrust,
                isp:          isp,
                engineActive: engine != null && engine.EngineIgnited);
        }
    }
}
