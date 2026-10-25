using UnityEngine;

namespace FXM.WebStream.WalkingSim
{
    /// <summary>
    /// Small gameplay overlay: controls hint, relay status and the shared-room player
    /// count. It stays out of the way while the setup panel is open.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("FXM WebStream/Walking Sim/Walking Sim HUD")]
    public sealed class WalkingSimHud : MonoBehaviour
    {
        private WalkingSimPlayer _player;
        private WalkingSimNetwork _network;
        private StreamSetupPanel _setupPanel;
        private GUIStyle _style;
        private GUIStyle _statusStyle;

        public void Configure(WalkingSimPlayer player, WalkingSimNetwork network,
            StreamSetupPanel panel)
        {
            _player = player;
            _network = network;
            _setupPanel = panel;
        }

        private void OnGUI()
        {
            if (_setupPanel != null && _setupPanel.isActiveAndEnabled)
            {
                if (!_setupPanel.IsPanelHidden ||
                    _setupPanel.Mode != StreamSetupPanel.SetupMode.Stream)
                {
                    return;
                }
            }

            EnsureStyles();

            bool mobile = Application.isMobilePlatform;
            string controls = mobile
                ? "Left stick to move  ·  swipe to look"
                : "WASD to move  ·  mouse to look  ·  F1 for setup";

            string status;
            if (_player == null || _network == null)
            {
                status = "Local play";
            }
            else if (_network.IsConnected)
            {
                status = "Room " + _network.room + "  ·  " + _network.PlayerCount + " player(s)";
            }
            else
            {
                status = "Room " + _network.room + "  ·  relay offline";
            }

            float scale = Mathf.Clamp(Screen.dpi > 1f ? Screen.dpi / 160f : Screen.height / 900f, 1f, 2f);
            float width = Mathf.Min(Screen.width * 0.52f, 430f * scale);
            float height = 44f * scale;
            Rect safe = Screen.safeArea;

            float right = safe.xMax - 12f * scale;
            float bottom = Screen.height - safe.y - 12f * scale;
            Rect rect = new Rect(right - width, bottom - height, width, height);

            Color previous = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.52f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = previous;

            GUI.Label(new Rect(rect.x + 10f * scale, rect.y + 5f * scale,
                rect.width - 20f * scale, 18f * scale), status, _statusStyle);
            GUI.Label(new Rect(rect.x + 10f * scale, rect.y + 21f * scale,
                rect.width - 20f * scale, 18f * scale), controls, _style);
        }

        private void EnsureStyles()
        {
            if (_style != null)
            {
                return;
            }

            float scale = Mathf.Clamp(Screen.dpi > 1f ? Screen.dpi / 160f : Screen.height / 900f, 1f, 2f);
            int fontSize = Mathf.RoundToInt(11f * scale);

            _style = new GUIStyle(GUI.skin.label)
            {
                fontSize = fontSize,
                alignment = TextAnchor.MiddleLeft,
                clipping = TextClipping.Clip
            };
            _style.normal.textColor = new Color(0.78f, 0.82f, 0.87f, 0.95f);

            _statusStyle = new GUIStyle(_style)
            {
                fontStyle = FontStyle.Bold
            };
            _statusStyle.normal.textColor = new Color(0.55f, 0.84f, 0.9f, 1f);
        }
    }
}
