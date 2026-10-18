using System;
using System.Collections.Generic;
using RelayOrbiter.Core;
using UnityEngine;

namespace RelayOrbiter.Plugin
{
    // Keeps a running deployment in the game's save file, so it can be resumed when the
    // flight scene reloads (switching to an unloaded vessel, quicksave/quickload,
    // leaving and coming back). Quickloading restores the deployment as it was saved.
    [KSPScenario(ScenarioCreationOptions.AddToAllGames, GameScenes.FLIGHT)]
    public sealed class RelayOrbiterScenario : ScenarioModule
    {
        private const string NodeName = "DEPLOYMENT";

        // The running deployment's state, provided by the Automation panel (null: none).
        internal static Func<DeploymentState?>? Provider;

        // A deployment found in the save, waiting to be resumed or discarded.
        internal static DeploymentState? Pending;

        public override void OnSave(ConfigNode node)
        {
            var state = Provider?.Invoke();
            if (state == null) return;
            var n = node.AddNode(NodeName);
            foreach (var kv in state.ToValues()) n.AddValue(kv.Key, kv.Value);
        }

        public override void OnLoad(ConfigNode node)
        {
            Pending = null;
            var n = node.GetNode(NodeName);
            if (n == null) return;
            var values = new Dictionary<string, string>();
            foreach (ConfigNode.Value v in n.values) values[v.name] = v.value;
            try
            {
                Pending = DeploymentState.FromValues(values);
                Debug.Log($"[RelayOrbiter] Found an interrupted deployment at step {Pending.Step + 1}.");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RelayOrbiter] Ignoring a saved deployment that can't be read: " + e.Message);
            }
        }
    }
}
