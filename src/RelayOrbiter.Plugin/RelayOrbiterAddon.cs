using KSP.UI.Screens;
using RelayOrbiter.Core;
using UnityEngine;

namespace RelayOrbiter.Plugin
{
    // Composition root: owns the toolbar button and the window, and wires the
    // core services to the game's bodies.
    [KSPAddon(KSPAddon.Startup.AllGameScenes, once: false)]
    public sealed class RelayOrbiterAddon : MonoBehaviour
    {
        private const string LockId = "RelayOrbiter_WindowLock";

        private const ApplicationLauncher.AppScenes Scenes =
            ApplicationLauncher.AppScenes.SPACECENTER | ApplicationLauncher.AppScenes.FLIGHT |
            ApplicationLauncher.AppScenes.MAPVIEW     | ApplicationLauncher.AppScenes.TRACKSTATION;

        private static bool _announced;
        private static Texture2D? _icon;

        private ApplicationLauncherButton? _button;
        private RelayOrbiterWindow? _window;
        private bool _locked;

        private void Start()
        {
            if (!_announced)
            {
                _announced = true;
                Debug.Log($"[RelayOrbiter] Loaded {typeof(RelayOrbiterAddon).Assembly.GetName().Version}");
            }
            GameEvents.onGUIApplicationLauncherReady.Add(AddButton);
            GameEvents.onGUIApplicationLauncherDestroyed.Add(RemoveButton);
            if (ApplicationLauncher.Ready) AddButton();
        }

        private void OnDestroy()
        {
            GameEvents.onGUIApplicationLauncherReady.Remove(AddButton);
            GameEvents.onGUIApplicationLauncherDestroyed.Remove(RemoveButton);
            RemoveButton();
            SetLock(false);
        }

        private void AddButton()
        {
            if (_button != null) return;
            if (_icon == null) _icon = ToolbarIcon.Create();
            _button = ApplicationLauncher.Instance.AddModApplication(
                ShowWindow, HideWindow, null, null, null, null, Scenes, _icon);
        }

        private void RemoveButton()
        {
            if (_button != null && ApplicationLauncher.Instance != null)
                ApplicationLauncher.Instance.RemoveModApplication(_button);
            _button = null;
        }

        private void ShowWindow()
        {
            if (_window == null) _window = CreateWindow();
            _window.Visible = true;
        }

        private void HideWindow()
        {
            if (_window != null) _window.Visible = false;
            SetLock(false);
        }

        private static RelayOrbiterWindow CreateWindow()
        {
            var db        = new GameBodyDatabase(FlightGlobals.Bodies);
            var mechanics = new OrbitalMechanics();
            var initial   = GameBodyDatabase.NameOf(FlightGlobals.ActiveVessel != null
                ? FlightGlobals.ActiveVessel.mainBody
                : FlightGlobals.GetHomeBody());
            return new RelayOrbiterWindow(
                db,
                new AltitudePlanner(db, mechanics),
                new ConstellationCalculator(mechanics),
                t => KSPUtil.PrintTime(t, 5, false),  // honours the game's Kerbin/Earth calendar
                initial);
        }

        private void OnGUI()
        {
            if (_window == null || !_window.Visible) return;
            GUI.skin = HighLogic.Skin;
            _window.Draw();
            SetLock(_window.Rect.Contains(Event.current.mousePosition));
        }

        // Keep clicks and keystrokes over the window from reaching the game.
        private void SetLock(bool locked)
        {
            if (locked == _locked) return;
            _locked = locked;
            if (locked) InputLockManager.SetControlLock(ControlTypes.ALLBUTCAMERAS, LockId);
            else        InputLockManager.RemoveControlLock(LockId);
        }
    }
}
