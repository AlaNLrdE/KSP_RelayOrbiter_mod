using System.Reflection;
using UnityEngine;

namespace RelayOrbiter.Plugin
{
    // MechJeb 2's ascent autopilot, reached by reflection so MechJeb stays optional.
    // Uses MuMech.VesselExtensions.GetMasterMechJeb(vessel), core.AscentSettings
    // (DesiredOrbitAltitude, DesiredInclination, Autostage), core.Ascent
    // (Users, Enabled, Status) and core.Staging (AutoStageLimitRequest/Remove), as in
    // MechJeb 2.15.
    internal sealed class MechJebAscent
    {
        private const BindingFlags Instance = BindingFlags.Public | BindingFlags.Instance;

        private static MethodInfo? _getMasterMechJeb;
        private static bool _searched;

        private readonly object _settings;
        private readonly object _autopilot;
        private readonly object? _staging;
        private object? _limitUser;

        private MechJebAscent(object settings, object autopilot, object? staging)
        {
            _settings  = settings;
            _autopilot = autopilot;
            _staging   = staging;
        }

        public static bool Installed
        {
            get { Find(); return _getMasterMechJeb != null; }
        }

        // Null when MechJeb is missing or the vessel has no MechJeb core.
        public static MechJebAscent? For(Vessel vessel)
        {
            Find();
            if (_getMasterMechJeb == null) return null;
            try
            {
                var core = _getMasterMechJeb.Invoke(null, new object[] { vessel });
                if (core == null) return null;
                var settings  = core.GetType().GetField("AscentSettings", Instance)?.GetValue(core);
                var autopilot = core.GetType().GetProperty("Ascent", Instance)?.GetValue(core, null);
                var staging   = core.GetType().GetField("Staging", Instance)?.GetValue(core);
                return settings != null && autopilot != null ? new MechJebAscent(settings, autopilot, staging) : null;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[RelayOrbiter] MechJeb is installed but its API changed: " + e.Message);
                return null;
            }
        }

        // Circular orbit at altitude (m) and inclination (degrees). autostage: MechJeb stages
        // by itself; off, someone else must (its own limits don't hold its ascent back from
        // the satellites' stages). The player's setting comes back on disengage.
        public void Configure(double altitude, double inclinationDeg, bool autostage)
        {
            SetEditable("DesiredOrbitAltitude", altitude);
            SetEditable("DesiredInclination", inclinationDeg);
            var prop = _settings.GetType().GetProperty("Autostage", Instance);
            if (prop == null) return;
            if (_savedAutostage == null) _savedAutostage = prop.GetValue(_settings, null) as bool?;
            prop.SetValue(_settings, autostage, null);
        }

        private bool? _savedAutostage;

        public void Engage(object user)    => CallUsers("Add", user);

        public void Disengage(object user)
        {
            CallUsers("Remove", user);
            ReleaseStageLimit();
            if (_savedAutostage.HasValue)
                _settings.GetType().GetProperty("Autostage", Instance)?.SetValue(_settings, _savedAutostage.Value, null);
            _savedAutostage = null;
        }

        // Never autostage down to `stage` or below: the satellites' stages (their engines
        // and separators) stay for the deployment. (MechJeb's ascent doesn't always honour
        // it: Relay Orbiter stages itself during its launches.) False when this MechJeb has
        // no staging limit to set.
        public bool LimitStaging(int stage, object user)
        {
            bool set = false;
            if (_staging != null)
            {
                var request = _staging.GetType().GetMethod("AutoStageLimitRequest", Instance);
                if (request != null)
                {
                    request.Invoke(_staging, new object[] { stage, user });
                    _limitUser = user;
                    set = true;
                }
            }
            return set;
        }

        private void ReleaseStageLimit()
        {
            if (_staging == null || _limitUser == null) return;
            _staging.GetType().GetMethod("AutoStageLimitRemove", Instance)?.Invoke(_staging, new[] { _limitUser });
            _limitUser = null;
        }

        public bool Enabled =>
            _autopilot.GetType().GetProperty("Enabled", Instance)?.GetValue(_autopilot, null) as bool? ?? false;

        public string Status =>
            _autopilot.GetType().GetField("Status", Instance)?.GetValue(_autopilot) as string ?? "";

        private void SetEditable(string field, double value)
        {
            var editable = _settings.GetType().GetField(field, Instance)?.GetValue(_settings);
            editable?.GetType().GetProperty("Val", Instance)?.SetValue(editable, value, null);
        }

        private void CallUsers(string method, object user)
        {
            var users = _autopilot.GetType().GetField("Users", Instance)?.GetValue(_autopilot);
            users?.GetType().GetMethod(method, Instance, null, new[] { typeof(object) }, null)
                 ?.Invoke(users, new[] { user });
        }

        private static void Find()
        {
            if (_searched) return;
            _searched = true;
            foreach (var a in AssemblyLoader.loadedAssemblies)
                if (a.name == "MechJeb2")
                    _getMasterMechJeb = a.assembly.GetType("MuMech.VesselExtensions")
                        ?.GetMethod("GetMasterMechJeb", BindingFlags.Public | BindingFlags.Static);
        }
    }
}
