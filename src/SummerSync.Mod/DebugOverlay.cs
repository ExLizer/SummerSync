using UnityEngine;

namespace SummerSync.Unity
{
    /// <summary>
    /// Text panel in the top-left corner of the screen. Later phases add network, tick and ownership lines.
    /// Must only be drawn from OnGUI.
    /// </summary>
    internal sealed class DebugOverlay
    {
        private const float Margin = 10f;
        private const float Padding = 6f;
        private const int FontSize = 13;

        private static readonly Color BackgroundColor = new Color(0f, 0f, 0f, 0.6f);

        private readonly GUIContent content = new GUIContent(string.Empty);
        private GUIStyle style;
        private Vector2 size;
        private bool sizeDirty = true;

        /// <summary>Sets the text to show. Lines are separated by '\n'.</summary>
        public void SetText(string text)
        {
            if (content.text == text)
            {
                return;
            }

            content.text = text;
            sizeDirty = true;
        }

        public void Draw()
        {
            if (style == null)
            {
                // GUI.skin is only available inside OnGUI, so the style cannot be built in the constructor.
                style = new GUIStyle(GUI.skin.label)
                {
                    fontSize = FontSize,
                    alignment = TextAnchor.UpperLeft,
                    wordWrap = false,
                    richText = false,
                };
                style.normal.textColor = Color.white;
            }

            if (sizeDirty)
            {
                size = style.CalcSize(content);
                sizeDirty = false;
            }

            var panel = new Rect(Margin, Margin, size.x + (Padding * 2f), size.y + (Padding * 2f));
            Color previousColor = GUI.color;
            GUI.color = BackgroundColor;
            GUI.DrawTexture(panel, Texture2D.whiteTexture);
            GUI.color = previousColor;

            GUI.Label(new Rect(panel.x + Padding, panel.y + Padding, size.x, size.y), content, style);
        }
    }
}
