using System;
using System.Collections;
using System.Reflection;
using UnityEngine;

namespace RelayOrbiter.Plugin
{
    // MechJeb 2's maneuver planner, reached by reflection so MechJeb stays optional:
    // MuMech.OperationGeneric (Hohmann to a moon), OperationInterplanetaryTransfer
    // (WaitForPhaseAngle) and OperationCourseCorrection (CourseCorrectFinalPeA), each
    // with MakeNodes(Orbit, UT, core.Target) → ManeuverParameters (dV, UT), placed with
    // VesselExtensions.PlaceManeuverNode, as in MechJeb 2.15. Relay Orbiter flies the
    // nodes with its own executor.
    internal static class MechJebTransfer
    {
        private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const BindingFlags Static   = BindingFlags.Public | BindingFlags.Static;

        private static Assembly? _assembly;
        private static bool _searched;

        public static bool Installed => Assembly() != null;

        public static bool Available(Vessel vessel) => Core(vessel) != null;

        // Null on success; otherwise why it failed.
        public static string? PlanEjection(Vessel vessel, CelestialBody target, bool interplanetary, Action<string> log)
        {
            string type = interplanetary ? "MuMech.OperationInterplanetaryTransfer" : "MuMech.OperationGeneric";
            return Plan(vessel, target, type, op =>
            {
                if (interplanetary) SetField(op, "WaitForPhaseAngle", true);
                else
                {
                    SetField(op, "Capture", false);
                    SetField(op, "PlanCapture", false);
                    SetField(op, "Rendezvous", false);
                }
            }, "transfer", log);
        }

        public static string? PlanCorrection(Vessel vessel, CelestialBody target, double finalPeriapsisAltitude,
                                             Action<string> log) =>
            Plan(vessel, target, "MuMech.OperationCourseCorrection", op =>
            {
                var editable = op.GetType().GetField("CourseCorrectFinalPeA", Instance)?.GetValue(op);
                editable?.GetType().GetProperty("Val", Instance)?.SetValue(editable, finalPeriapsisAltitude, null);
            }, "course correction", log);

        private static string? Plan(Vessel vessel, CelestialBody target, string typeName, Action<object> configure,
                                    string what, Action<string> log)
        {
            try
            {
                var core = Core(vessel);
                if (core == null) return "no MechJeb on this vessel";
                var targetController = core.GetType().GetField("Target", Instance)?.GetValue(core);
                if (targetController == null) return "MechJeb's target controller not found";
                targetController.GetType().GetMethod("Set", Instance)?.Invoke(targetController, new object[] { target });
                FlightGlobals.fetch.SetVesselTarget(target);

                var type = Assembly()!.GetType(typeName);
                if (type == null) return $"{typeName} not found";
                var op = Activator.CreateInstance(type);
                configure(op);
                var make = type.GetMethod("MakeNodes", Instance);
                if (make == null) return "MakeNodes not found";
                var nodes = make.Invoke(op, new[] { vessel.orbit, (object)Planetarium.GetUniversalTime(), targetController })
                            as IList;
                if (nodes == null || nodes.Count == 0)
                    return type.GetMethod("GetErrorMessage", Instance)?.Invoke(op, null) as string ?? "no maneuver";

                var first = nodes[0];
                var dv = (Vector3d)first.GetType().GetField("dV", Instance)!.GetValue(first);
                double ut = (double)first.GetType().GetField("UT", Instance)!.GetValue(first);
                var place = Assembly()!.GetType("MuMech.VesselExtensions")?.GetMethod("PlaceManeuverNode", Static);
                if (place == null) return "PlaceManeuverNode not found";
                place.Invoke(null, new object[] { vessel, vessel.orbit, dv, ut });
                log($"MechJeb planned the {what}: {dv.magnitude:F2} m/s in " +
                    $"{KSPUtil.PrintTime(ut - Planetarium.GetUniversalTime(), 3, false)}.");
                return null;
            }
            catch (TargetInvocationException e)
            {
                return e.InnerException?.Message ?? e.Message;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RelayOrbiter] MechJeb's maneuver planner API changed: " + e);
                return e.Message;
            }
        }

        private static object? Core(Vessel vessel)
        {
            var get = Assembly()?.GetType("MuMech.VesselExtensions")?.GetMethod("GetMasterMechJeb", Static);
            try { return get?.Invoke(null, new object[] { vessel }); }
            catch { return null; }
        }

        private static void SetField(object o, string name, object value) =>
            o.GetType().GetField(name, Instance)?.SetValue(o, value);

        private static Assembly? Assembly()
        {
            if (_searched) return _assembly;
            _searched = true;
            foreach (var a in AssemblyLoader.loadedAssemblies)
                if (a.name == "MechJeb2") _assembly = a.assembly;
            return _assembly;
        }
    }
}
