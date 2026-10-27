using System;
using UnityEngine;

namespace StbcRevival
{
    /// <summary>
    /// Typing a short text (a crew password): SteamVR's keyboard in the headset (VrKeyboard), otherwise a small
    /// window on the desktop that's only there while it's asked for. The answer goes to the callback only when
    /// something was typed and confirmed.
    /// </summary>
    internal static class TextPrompt
    {
        private static string title;
        private static Action<string> done;
        private static string input = "";
        private static bool hidden = true;
        private static Rect window = new Rect(0, 0, 360, 110);

        internal static void Ask(string what, bool secret, Action<string> answer)
        {
            bool steamVr = UnityEngine.VR.VRSettings.enabled && UnityEngine.VR.VRSettings.loadedDeviceName == "OpenVR";
            if (steamVr)
            {
                VrKeyboard.Open(what, 24, answer);
                return;
            }
            title = what;
            done = answer;
            input = "";
            hidden = secret;
            window.x = (Screen.width - window.width) / 2;
            window.y = (Screen.height - window.height) / 3;
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
            if (UnityEngine.VR.VRSettings.enabled)
                try { UI.PopupMgr.ShowPopup($"Type it in the window on the desktop ({what}).", null, UI.PDHCCBFJODN.Notification, true); }
                catch { /* no popups here */ }
        }

        internal static void OnGUI()
        {
            if (done == null) return;
            window = GUILayout.Window(0x5BC8, window, Draw, title);
        }

        private static void Draw(int id)
        {
            GUI.SetNextControlName("prompt");
            input = hidden ? GUILayout.PasswordField(input, '*', 24) : GUILayout.TextField(input, 24);
            GUI.FocusControl("prompt");
            bool enter = Event.current.type == EventType.KeyDown && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);
            GUILayout.BeginHorizontal();
            bool ok = GUILayout.Button("OK") || enter;
            bool cancel = GUILayout.Button("Cancel") || (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape);
            GUILayout.EndHorizontal();
            if (ok && input.Trim().Length > 0)
            {
                var answer = done;
                done = null;
                answer(input.Trim());
            }
            else if (cancel) done = null;
            GUI.DragWindow();
        }
    }
}
