using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using MSCLoader;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Players;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SummerSync.Unity.Players
{
    /// <summary>
    /// The text chat on screen, at the left above the middle of the bottom: the last lines, which fade after a while, and,
    /// with the chat key (Y unless changed in the mod's keys), a line to type in. Enter sends it, Escape closes it. While it
    /// is open the game does not see the keyboard (<see cref="ChatInput"/>). In the main menu as in the world, whenever
    /// there is a session. Players coming and going show as lines too.
    /// </summary>
    internal sealed class ChatBox
    {
        /// <summary>The chat key until a player changes it in the mod's keys: the game does not use Y (docs/GAME_NOTES.md).</summary>
        public const KeyCode DefaultKey = KeyCode.Y;

        private const string ControlName = "SummerSyncChat";
        private const float ShownSeconds = 14f;
        private const float FadeSeconds = 2f;
        private const int ShownLines = 8;
        private const int OpenLines = 14;
        private const int KeptLines = 60;

        private static readonly Color[] PlayerColors =
        {
            new Color(1f, 0.82f, 0.35f), new Color(0.45f, 0.85f, 1f), new Color(0.55f, 0.95f, 0.5f), new Color(1f, 0.6f, 0.45f),
            new Color(0.85f, 0.6f, 1f), new Color(1f, 0.95f, 0.6f),
        };

        private static readonly Color NoticeColor = new Color(0.78f, 0.8f, 0.82f);
        private static readonly Color Shade = new Color(0f, 0f, 0f, 0.55f);

        // Static: what was said stays on screen when MSCLoader makes the mod again on the way to the main menu.
        private static readonly List<Entry> history = new List<Entry>();

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly List<ChatLine> arrived = new List<ChatLine>();
        private readonly GUIContent content = new GUIContent(string.Empty);
        private GUIStyle lineStyle;
        private GUIStyle inputStyle;
        private int styledFor;
        private string typed = string.Empty;
        private int openedFrame = -10;
        private int closedFrame = -10;
        private bool releaseFocus;

        public ChatBox(Logger log, NetworkManager manager)
        {
            this.log = log;
            this.manager = manager;
            ChatInput.Install(log);
        }

        public bool IsOpen { get; private set; }

        /// <summary>
        /// Call every frame. <paramref name="busy"/>: a window of the mod's is open, which keeps the chat closed.
        /// </summary>
        public void Update(bool busy)
        {
            ChatReplication chat = manager.Chat;
            if (chat != null)
            {
                arrived.Clear();
                chat.Take(arrived);
                for (int i = 0; i < arrived.Count; i++)
                {
                    Add(Describe(arrived[i]), arrived[i].Kind == ChatLineKind.Said ? ColorOf(arrived[i].PlayerId) : NoticeColor);
                }
            }

            bool connected = chat != null && manager.Session != null && manager.Session.State == SessionState.Connected;
            if (IsOpen && (!connected || busy))
            {
                Close();
            }
            else if (!IsOpen && connected && !busy && ChatInput.Ready && KeyDown() && !GameTypes())
            {
                Open();
            }

            // Held through the frame it closes in, whose Escape or Enter the game would otherwise see.
            ChatInput.Holding = IsOpen || Time.frameCount <= closedFrame + 1;
        }

        /// <summary>
        /// The "ss chat" console command: how the chat is; with a text, says it as if typed; "open", opens the line to type
        /// in as the key does (test aids).
        /// </summary>
        public string Command(string text)
        {
            ChatReplication chat = manager.Chat;
            if (string.IsNullOrEmpty(text))
            {
                return "Chat: " + (chat == null ? "no session" : chat.Said + " line(s) written here, " + chat.Heard + " heard, " + chat.Dropped + " dropped as too fast")
                    + "; " + (ChatInput.Ready ? "the keyboard is held while typing" : "it cannot be typed in (see the log)") + "; " + history.Count + " line(s) on screen; key " + Key()
                    + (IsOpen ? "; open" : string.Empty) + (ChatInput.Holding ? "; the game's keyboard is held" : string.Empty) + ".";
            }

            if (chat == null || manager.Session == null || manager.Session.State != SessionState.Connected)
            {
                return "There is no session to write to.";
            }

            if (string.Equals(text, "open", StringComparison.OrdinalIgnoreCase))
            {
                if (!ChatInput.Ready)
                {
                    return "The chat cannot be typed in here (see the log).";
                }

                Open();
                return "Chat: open; Enter sends, Escape closes.";
            }

            ChatSayResult result = chat.Say(text);
            return "Chat: " + result + ".";
        }

        private void Open()
        {
            IsOpen = true;
            typed = string.Empty;
            openedFrame = Time.frameCount;
            ChatInput.Holding = true;
        }

        public void OnGUI()
        {
            if (releaseFocus)
            {
                releaseFocus = false;
                GUIUtility.keyboardControl = 0;
            }

            if (history.Count == 0 && !IsOpen)
            {
                return;
            }

            Styles();
            float now = Time.realtimeSinceStartup;
            float width = Mathf.Min(Screen.width * 0.42f, 700f);
            float left = Mathf.Round(Screen.width * 0.012f) + 4f;
            float bottom = Mathf.Round(Screen.height * 0.74f);
            float inputHeight = lineStyle.lineHeight + 10f;
            if (IsOpen)
            {
                HandleKeys();
            }

            // The lines, newest at the bottom, upwards from the line to type in.
            float y = bottom - (IsOpen ? inputHeight + 4f : 0f);
            int shown = 0;
            int limit = IsOpen ? OpenLines : ShownLines;
            for (int i = history.Count - 1; i >= 0 && shown < limit; i--)
            {
                Entry entry = history[i];
                float age = now - entry.At;
                float alpha = IsOpen ? 1f : Mathf.Clamp01((ShownSeconds - age) / FadeSeconds);
                if (alpha <= 0f)
                {
                    break;
                }

                content.text = entry.Text;
                float height = lineStyle.CalcHeight(content, width - 12f);
                y -= height + 2f;
                var area = new Rect(left, y, width, height + 2f);
                Color before = GUI.color;
                GUI.color = new Color(Shade.r, Shade.g, Shade.b, Shade.a * alpha);
                GUI.DrawTexture(area, Texture2D.whiteTexture);
                lineStyle.normal.textColor = new Color(0f, 0f, 0f, alpha);
                GUI.color = before;
                var text = new Rect(left + 6f, y + 1f, width - 12f, height);
                GUI.Label(new Rect(text.x + 1f, text.y + 1f, text.width, text.height), content, lineStyle);
                lineStyle.normal.textColor = new Color(entry.Color.r, entry.Color.g, entry.Color.b, alpha);
                GUI.Label(text, content, lineStyle);
                shown++;
            }

            if (!IsOpen)
            {
                return;
            }

            var box = new Rect(left, bottom - inputHeight, width, inputHeight);
            Color previous = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.78f);
            GUI.DrawTexture(box, Texture2D.whiteTexture);
            GUI.color = previous;
            content.text = Texts.Pick("Say:", "Decir:");
            float labelWidth = lineStyle.CalcSize(content).x + 8f;
            lineStyle.normal.textColor = PlayerColors[0];
            GUI.Label(new Rect(box.x + 6f, box.y + 5f, labelWidth, box.height - 10f), content, lineStyle);
            GUI.SetNextControlName(ControlName);
            typed = GUI.TextField(new Rect(box.x + 6f + labelWidth, box.y + 3f, box.width - labelWidth - 12f, box.height - 6f), typed ?? string.Empty, ChatReplication.MaxCharacters, inputStyle);
            GUI.FocusControl(ControlName);
        }

        public void Close()
        {
            if (!IsOpen)
            {
                return;
            }

            IsOpen = false;
            typed = string.Empty;
            closedFrame = Time.frameCount;

            // Given back in OnGUI, where the text field had it.
            releaseFocus = true;
        }

        private void HandleKeys()
        {
            Event current = Event.current;
            if (current.type != EventType.KeyDown)
            {
                return;
            }

            // The key that opened it, and the letter it types, which may come a frame later.
            if (Time.frameCount <= openedFrame + 1 && typed.Length == 0 && (current.keyCode == Key() || current.character != '\0'))
            {
                current.Use();
                return;
            }

            if (current.keyCode == KeyCode.Return || current.keyCode == KeyCode.KeypadEnter)
            {
                current.Use();
                Send();
            }
            else if (current.character == '\n')
            {
                // The line break that comes with Enter.
                current.Use();
            }
            else if (current.keyCode == KeyCode.Escape)
            {
                current.Use();
                Close();
            }
        }

        private void Send()
        {
            string text = typed;
            ChatReplication chat = manager.Chat;
            ChatSayResult result = chat == null ? ChatSayResult.NotConnected : chat.Say(text);
            if (result == ChatSayResult.TooFast)
            {
                // Kept, to send again in a moment.
                Add(Texts.Pick("Slow down: too many lines at once.", "Más despacio: demasiadas líneas seguidas."), NoticeColor);
                return;
            }

            Close();
        }

        private static string Describe(ChatLine line)
        {
            switch (line.Kind)
            {
                case ChatLineKind.Joined:
                    return Texts.Pick(line.Name + " joined.", line.Name + " entró.");
                case ChatLineKind.Present:
                    return Texts.Pick(line.Name + " is here.", line.Name + " está acá.");
                case ChatLineKind.Left:
                    return Texts.Pick(line.Name + " left.", line.Name + " se fue.");
                default:
                    return line.Name + ": " + line.Text;
            }
        }

        private static Color ColorOf(byte player)
        {
            return PlayerColors[player % PlayerColors.Length];
        }

        private static void Add(string text, Color color)
        {
            if (history.Count >= KeptLines)
            {
                history.RemoveAt(0);
            }

            history.Add(new Entry { Text = text, Color = color, At = Time.realtimeSinceStartup });
        }

        private bool KeyDown()
        {
            SettingsKeybind key = ModRuntime.ChatKey;
            return key != null ? key.GetKeybindDown() : Input.GetKeyDown(DefaultKey);
        }

        private static KeyCode Key()
        {
            SettingsKeybind key = ModRuntime.ChatKey;
            return key != null ? key.GetKeyValue : DefaultKey;
        }

        // Somewhere else that takes typing: MSCLoader's console or settings, the game's computer, or a menu of the game.
        private bool GameTypes()
        {
            EventSystem events = EventSystem.current;
            GameObject selected = events == null ? null : events.currentSelectedGameObject;
            if (selected != null)
            {
                InputField field = selected.GetComponent<InputField>();
                if (field != null && field.isFocused)
                {
                    return true;
                }
            }

            if (Application.loadedLevelName != "GAME")
            {
                return false;
            }

            FsmBool computer = FsmVariables.GlobalVariables.FindFsmBool("PlayerComputer");
            FsmBool menu = FsmVariables.GlobalVariables.FindFsmBool("PlayerInMenu");
            return (computer != null && computer.Value) || (menu != null && menu.Value);
        }

        private void Styles()
        {
            int size = Mathf.Clamp(Mathf.RoundToInt(Screen.height / 68f), 12, 26);
            if (lineStyle != null && styledFor == size)
            {
                return;
            }

            styledFor = size;
            lineStyle = new GUIStyle(GUI.skin.label) { fontSize = size, wordWrap = true, richText = false, alignment = TextAnchor.UpperLeft };
            lineStyle.padding = new RectOffset(0, 0, 0, 0);
            inputStyle = new GUIStyle(GUI.skin.textField) { fontSize = size, richText = false, wordWrap = false, alignment = TextAnchor.MiddleLeft };
            inputStyle.normal.textColor = Color.white;
            inputStyle.focused.textColor = Color.white;
            log.Debug(LogCategory.Player, "Chat: lines drawn at " + size + " px.");
        }

        private sealed class Entry
        {
            public string Text;

            public Color Color;

            public float At;
        }
    }
}
