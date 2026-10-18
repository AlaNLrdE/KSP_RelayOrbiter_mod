using System;
using KSP.UI.Screens;
using RelayOrbiter.Core;
using UnityEngine;

namespace RelayOrbiter.Plugin
{
    // Composition root (flight only): owns the toolbar button and the window, wires
    // the core services to the game's bodies, and keeps the window's settings.
    [KSPAddon(KSPAddon.Startup.Flight, once: false)]
    public sealed class RelayOrbiterAddon : MonoBehaviour
    {
        private const string LockId = "RelayOrbiter_WindowLock";

        private const ApplicationLauncher.AppScenes Scenes =
            ApplicationLauncher.AppScenes.FLIGHT | ApplicationLauncher.AppScenes.MAPVIEW;

        private static bool _announced;
        private static Texture2D? _icon;

        private ApplicationLauncherButton? _button;
        private RelayOrbiterWindow? _window;
        private KSP.IO.PluginConfiguration? _config;
        private readonly ConstellationSettings _settings = new ConstellationSettings();
        private bool _locked, _uiHidden;

        private void Start()
        {
            if (!_announced)
            {
                _announced = true;
                Debug.Log($"[RelayOrbiter] Loaded {typeof(RelayOrbiterAddon).Assembly.GetName().Version}");
            }
            GameEvents.onGUIApplicationLauncherReady.Add(AddButton);
            GameEvents.onGUIApplicationLauncherDestroyed.Add(RemoveButton);
            GameEvents.onHideUI.Add(OnHideUI);
            GameEvents.onShowUI.Add(OnShowUI);
            if (ApplicationLauncher.Ready) AddButton();
        }

        private void Update()
        {
            // A constellation correction that switched to a far satellite reloaded the
            // scene: open the window, so it carries on (and shows) by itself.
            if (_window == null && _button != null && RelayOrbiterScenario.PendingMaintenance != null)
                _button.SetTrue(makeCall: true);
            _window?.Tick();
        }

        private void OnDestroy()
        {
            // Leaving the scene stops a running deployment (it is in the save file, so it can
            // be resumed). Whatever happens there, the clean-up below must run, or the stale
            // toolbar button and events keep pointing at this destroyed add-on.
            try { _window?.Abort(); }
            catch (Exception e) { Debug.LogException(e); }
            try { SaveSettings(); }
            catch (Exception e) { Debug.LogException(e); }
            GameEvents.onGUIApplicationLauncherReady.Remove(AddButton);
            GameEvents.onGUIApplicationLauncherDestroyed.Remove(RemoveButton);
            GameEvents.onHideUI.Remove(OnHideUI);
            GameEvents.onShowUI.Remove(OnShowUI);
            RemoveButton();
            SetLock(false);
        }

        private void OnHideUI() { _uiHidden = true; SetLock(false); }  // F2
        private void OnShowUI() => _uiHidden = false;

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
            SaveSettings();
            SetLock(false);
        }

        // The window's × button: same as clicking the toolbar button off.
        private void CloseWindow()
        {
            if (_button != null) _button.SetFalse(makeCall: true);
            else HideWindow();
        }

        private RelayOrbiterWindow CreateWindow()
        {
            var db         = new GameBodyDatabase(FlightGlobals.Bodies);
            var mechanics  = new OrbitalMechanics();
            var altitudes  = new AltitudePlanner(db, mechanics);
            var deployment = new DeploymentPlanner(mechanics);
            var calculator = new ConstellationCalculator(mechanics);
            Func<double, string> formatTime = t => KSPUtil.PrintTime(t, 5, false);  // honours the game's calendar

            var window = new RelayOrbiterWindow(
                db, altitudes, calculator, formatTime, _settings,
                new ManeuversPanel(deployment, altitudes, calculator, _settings, formatTime),
                new AutomationPanel(deployment, altitudes, calculator, _settings, new TransferPlanner(mechanics), formatTime),
                new MaintenancePanel(mechanics, new TransferPlanner(mechanics), new PhasingPlanner(mechanics),
                                     deployment, formatTime),
                CloseWindow);
            LoadSettings(window);
            return window;
        }

        private void OnGUI()
        {
            if (_window == null || !_window.Visible || _uiHidden) return;
            GUI.skin = RelayOrbiterSkin.Get();
            _window.Draw();
            SetLock(_window.Rect.Contains(Event.current.mousePosition));
        }

        // ── Settings (GameData/RelayOrbiter/Plugins/PluginData/RelayOrbiter/config.xml) ──

        private void LoadSettings(RelayOrbiterWindow window)
        {
            _config = KSP.IO.PluginConfiguration.CreateForType<RelayOrbiterAddon>();
            _config.load();
            window.Position = new Vector2(_config.GetValue("windowX", 240f), _config.GetValue("windowY", 120f));
            window.Tab = _config.GetValue("tab", 0);
            _settings.Satellites = _config.GetValue("satellites", 3);
            _settings.ChangeInclination = _config.GetValue("changeInclination", false);
            _settings.InclinationDeg = _config.GetValue("inclination", 0.0);
            _settings.NameSatellites = _config.GetValue("nameSatellites", true);
        }

        private void SaveSettings()
        {
            if (_config == null || _window == null) return;
            _config.SetValue("windowX", _window.Position.x);
            _config.SetValue("windowY", _window.Position.y);
            _config.SetValue("tab", _window.Tab);
            _config.SetValue("satellites", _settings.Satellites);
            _config.SetValue("changeInclination", _settings.ChangeInclination);
            _config.SetValue("inclination", _settings.InclinationDeg);
            _config.SetValue("nameSatellites", _settings.NameSatellites);
            _config.save();
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
