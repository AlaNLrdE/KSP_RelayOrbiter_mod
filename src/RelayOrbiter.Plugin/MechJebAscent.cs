using System.Reflection;
using UnityEngine;

namespace RelayOrbiter.Plugin
{
    // MechJeb 2's ascent autopilot, reached by reflection so MechJeb stays optional.
    // Uses MuMech.VesselExtensions.GetMasterMechJeb(vessel), core.AscentSettings
    // (DesiredOrbitAltitude, DesiredInclination, Autostage) and core.Ascent (Users,
    // Enabled, Status), as in MechJeb 2.15.
    internal sealed class MechJebAscent
    {
        private const BindingFlags Instance = BindingFlags.Public | BindingFlags.Instance;

        private static MethodInfo? _getMasterMechJeb;
        private static bool _searched;

        private readonly object _settings;
        private readonly object _autopilot;

        private MechJebAscent(object settings, object autopilot)
        {
            _settings  = settings;
            _autopilot = autopilot;
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
                return settings != null && autopilot != null ? new MechJebAscent(settings, autopilot) : null;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[RelayOrbiter] MechJeb is installed but its API changed: " + e.Message);
                return null;
            }
        }

        // Circular orbit at altitude (m) and inclination (degrees), staging automatically.
        public void Configure(double altitude, double inclinationDeg)
        {
            SetEditable("DesiredOrbitAltitude", altitude);
            SetEditable("DesiredInclination", inclinationDeg);
            _settings.GetType().GetProperty("Autostage", Instance)?.SetValue(_settings, true, null);
        }

        public void Engage(object user)    => CallUsers("Add", user);
        public void Disengage(object user) => CallUsers("Remove", user);

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
