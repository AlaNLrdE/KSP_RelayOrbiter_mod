using System;
using System.Collections.Generic;
using RelayOrbiter.Core;
using UnityEngine;

namespace RelayOrbiter.Plugin
{
    // Keeps a running deployment (and a running constellation correction) in the game's save file, so it can be resumed when the
    // flight scene reloads (switching to an unloaded vessel, quicksave/quickload,
    // leaving and coming back). Quickloading restores the deployment as it was saved.
    [KSPScenario(ScenarioCreationOptions.AddToAllGames, GameScenes.FLIGHT)]
    public sealed class RelayOrbiterScenario : ScenarioModule
    {
        private const string NodeName = "DEPLOYMENT";
        private const string MaintenanceNode = "MAINTENANCE";

        // The running deployment's state, provided by the Automation panel (null: none).
        internal static Func<DeploymentState?>? Provider;

        // A deployment found in the save, waiting to be resumed or discarded.
        internal static DeploymentState? Pending;

        // The same for a running constellation correction (Maintenance panel).
        internal static Func<MaintenanceState?>? MaintenanceProvider;
        internal static MaintenanceState? PendingMaintenance;

        public override void OnSave(ConfigNode node)
        {
            var state = Provider?.Invoke();
            if (state != null)
            {
                var n = node.AddNode(NodeName);
                foreach (var kv in state.ToValues()) n.AddValue(kv.Key, kv.Value);
            }
            var maintenance = MaintenanceProvider?.Invoke();
            if (maintenance != null)
            {
                var n = node.AddNode(MaintenanceNode);
                foreach (var kv in maintenance.ToValues()) n.AddValue(kv.Key, kv.Value);
            }
        }

        public override void OnLoad(ConfigNode node)
        {
            Pending = null;
            PendingMaintenance = null;
            var n = node.GetNode(NodeName);
            if (n != null)
                try
                {
                    Pending = DeploymentState.FromValues(Values(n));
                    Debug.Log($"[RelayOrbiter] Found an interrupted deployment at step {Pending.Step + 1}.");
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[RelayOrbiter] Ignoring a saved deployment that can't be read: " + e.Message);
                }
            var m = node.GetNode(MaintenanceNode);
            if (m != null)
                try
                {
                    PendingMaintenance = MaintenanceState.FromValues(Values(m));
                    Debug.Log("[RelayOrbiter] Found a constellation correction in progress.");
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[RelayOrbiter] Ignoring a saved correction that can't be read: " + e.Message);
                }
        }

        private static Dictionary<string, string> Values(ConfigNode n)
        {
            var values = new Dictionary<string, string>();
            foreach (ConfigNode.Value v in n.values) values[v.name] = v.value;
            return values;
        }
    }
}
