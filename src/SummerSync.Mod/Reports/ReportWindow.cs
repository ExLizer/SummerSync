using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using HutongGames.PlayMaker;
using MSCLoader;
using SummerSync.Core;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Reports;
using SummerSync.Unity.Net;
using SummerSync.Unity.Players;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SummerSync.Unity.Reports
{
    /// <summary>
    /// The bug report: a window where the player says what happened, ticks what it was about, may give their Discord name,
    /// reads what is sent and presses Send. Nothing leaves this game before that press. Then the mod writes the session as
    /// this game has it to the log and to the report (<see cref="SessionSnapshot"/>), packs its logs, settings and the
    /// game's log (<see cref="ReportPacker"/>), sends them through Steam (<see cref="SteamUpload"/>) and shows the code the
    /// server gave, or what went wrong in plain words.
    ///
    /// It opens from the game's Esc menu (<see cref="PauseMenuButton"/>), with its key (F6 unless changed in the mod's
    /// keys), with "ss report" in the console and "/report" in the chat; with or without a session, in the world or in the
    /// main menu. In a session, the others are told the code (<see cref="ReportReplication"/>) and each is asked whether to
    /// send their own log with it: if they agree, their report names that code and the server files the two together.
    ///
    /// While it is open the game sees no key (<see cref="ChatInput"/>), and in the world its mouse look is held and the
    /// cursor is free, as in the character window.
    /// </summary>
    internal sealed class ReportWindow
    {
        public const KeyCode DefaultKey = KeyCode.F6;

        private const string TextControl = "SummerSyncReportText";
        private const string LookComponent = "MouseLook";

        // A notice of another player's report shows this long on the screen, and can be answered for this long (the server
        // takes files for a report for 15 minutes after its first).
        private const double NoticeShown = 90.0;
        private const double NoticeAnswerable = 14 * 60.0;

        // Steam's own time limit, and how long after it the mod stops waiting for Steam's answer.
        private const uint TimeoutMs = 180000;
        private const double MostWaiting = 210.0;

        private static readonly string[] TagsEnglish = { "Vehicles", "Players", "World", "People & jobs", "Items", "Other" };
        private static readonly string[] TagsSpanish = { "Vehículos", "Jugadores", "Mundo", "Gente y trabajos", "Objetos", "Otro" };

        // What outlives this window (the mod's object goes when the menu loads again): where reports go, the last one, and
        // another player's report to answer.
        private static string endpoint = Edition.ReportsEndpoint;
        private static string lastCode;
        private static string lastProblem;
        private static ReportNotice notice;
        private static double noticeSince = double.NegativeInfinity;
        private static bool noticeAnswered;

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly PauseMenuButton pause;
        private readonly Func<Transform, string> snapshot;
        private readonly List<ReportNotice> arrived = new List<ReportNotice>();
        private readonly bool[] ticked = new bool[ReportRequest.Tags.Length];
        private readonly GUIContent content = new GUIContent();
        private Stage stage = Stage.Closed;
        private string description = string.Empty;
        private string contact = string.Empty;
        private string group;
        private string groupName;
        private ReportRequest request;
        private ReportPacker packer;
        private SteamUpload upload;
        private double stageSince;
        private double now;
        private string code;
        private string problem;
        private bool canGoAlone;
        private bool copied;
        private bool focus;
        private int closedFrame = -10;
        private bool releaseFocus;
        private Transform player;
        private Transform latestPlayer;
        private Behaviour[] held = new Behaviour[0];
        private FsmBool inMenu;
        private bool inMenuBefore;
        private bool holdingGame;

        private GUIStyle panelStyle;
        private GUIStyle titleStyle;
        private GUIStyle textStyle;
        private GUIStyle smallStyle;
        private GUIStyle codeStyle;
        private GUIStyle rightStyle;
        private GUIStyle workingStyle;
        private GUIStyle buttonStyle;
        private GUIStyle sendStyle;
        private GUIStyle fieldStyle;
        private GUIStyle areaStyle;
        private GUIStyle toggleStyle;
        private Texture2D panelTexture;
        private Texture2D sendTexture;
        private Texture2D noticeTexture;
        private float styledFor;

        public ReportWindow(Logger log, NetworkManager manager, Func<Transform, string> snapshot)
        {
            this.log = log;
            this.manager = manager;
            this.snapshot = snapshot;
            pause = new PauseMenuButton(log);
        }

        private enum Stage
        {
            Closed,
            Compose,
            Packing,
            Sending,
            Sent,
            Failed,
        }

        public bool IsOpen
        {
            get { return stage != Stage.Closed; }
        }

        /// <summary>Where reports go now: <see cref="Edition.ReportsEndpoint"/>, unless "ss report url" said otherwise.</summary>
        public static string Endpoint
        {
            get { return endpoint; }
        }

        /// <summary>
        /// Once per frame, after the network. <paramref name="inWorld"/>: the game world is loaded and this is not the main
        /// menu; <paramref name="busy"/>: the chat or another window of the mod's is open, which the key does not open over.
        /// </summary>
        public void Update(double time, Transform localPlayer, bool inWorld, bool busy)
        {
            now = time;
            latestPlayer = localPlayer;
            TakeNotices();
            bool clicked = pause.Update(inWorld);
            if (!IsOpen && (clicked || (!busy && KeyDown() && !Typing())))
            {
                Open(null, localPlayer);
            }

            if (IsOpen && stage != Stage.Packing && stage != Stage.Sending && player != null && player != localPlayer)
            {
                // The world went away under the window.
                Close();
            }

            Progress();
            ChatInput.WindowHolding = IsOpen || Time.frameCount <= closedFrame + 1;
            pause.HoldClicks(IsOpen && pause.Shown);
        }

        /// <summary>Once per frame, late: the game takes the cursor back every frame it is not in a menu of its own.</summary>
        public void LateUpdate()
        {
            if (IsOpen && holdingGame)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
        }

        /// <summary>Opens the window: on another player's report to answer when there is one, otherwise a report of this player's.</summary>
        public void Open(string text, Transform localPlayer)
        {
            if (IsOpen)
            {
                return;
            }

            if (AnswerableNotice())
            {
                group = notice.Code;
                groupName = notice.Name;
            }
            else
            {
                group = null;
                groupName = null;
            }

            description = ReportRequest.CleanDescription(text);
            contact = string.Empty;
            for (int i = 0; i < ticked.Length; i++)
            {
                ticked[i] = false;
            }

            code = null;
            problem = null;
            canGoAlone = false;
            copied = false;
            focus = true;
            player = localPlayer;
            stage = Stage.Compose;
            stageSince = now;
            ChatInput.WindowHolding = true;
            if (localPlayer != null)
            {
                HoldTheGame(true);
            }

            log.Info(LogCategory.General, "Report: the window is open" + (group != null ? " to send this player's log with " + groupName + "'s report " + group : string.Empty) + ".");
        }

        /// <summary>Closes the window, unless a report is on its way (it is not stopped halfway).</summary>
        public void Close()
        {
            if (!IsOpen || stage == Stage.Packing || stage == Stage.Sending)
            {
                return;
            }

            stage = Stage.Closed;
            closedFrame = Time.frameCount;
            releaseFocus = true;
            HoldTheGame(false);
            pause.HoldClicks(false);
        }

        /// <summary>The mod's object is going away: nothing of the game stays held, and Steam's call is let go.</summary>
        public void Dispose()
        {
            if (upload != null)
            {
                upload.Dispose();
                upload = null;
            }

            HoldTheGame(false);
            pause.HoldClicks(false);
            ChatInput.WindowHolding = false;
            stage = Stage.Closed;
        }

        /// <summary>
        /// The "ss report" console command: without more, opens the window; with a text, opens it with that text; "status",
        /// how it stands; "no", lets another player's report go unanswered; "close". Test aids of the developers' build:
        /// "url [address|reset]", "send" (presses Send on the open window, or on the answer to another player's report),
        /// "gzipstream" (whether the game's own GZipStream works).
        /// </summary>
        public string Command(string text, Transform localPlayer)
        {
            string verb = string.IsNullOrEmpty(text) ? string.Empty : text.Trim();
            string first = verb.Split(' ')[0].ToLowerInvariant();
            switch (first)
            {
                case "":
                    Open(null, localPlayer);
                    return "Report: the window is open" + (group != null ? " (to send your log with " + groupName + "'s report " + group + ")" : string.Empty) + ".";
                case "status":
                    return Status();
                case "snapshot":
                    string picture = snapshot(localPlayer);
                    log.Info(LogCategory.Player, "Report: the session at this moment (\"ss report snapshot\", nothing sent):\n" + picture);
                    return "Report: the session as a report would carry it (" + picture.Length + " characters) is in the log; nothing was sent.";
                case "no":
                    noticeAnswered = true;
                    return notice == null ? "Report: no other player's report to answer." : "Report: " + notice.Name + "'s report " + notice.Code + " is left unanswered.";
                case "close":
                    Close();
                    return IsOpen ? "Report: on its way; the window closes when it is done." : "Report: closed.";
                case "url":
                    if (!Edition.Developer)
                    {
                        return "Report: the address of the reports' server is fixed in this build (" + endpoint + ").";
                    }

                    string address = verb.Length > 3 ? verb.Substring(3).Trim() : string.Empty;
                    if (address.Length > 0)
                    {
                        endpoint = string.Equals(address, "reset", StringComparison.OrdinalIgnoreCase) ? Edition.ReportsEndpoint : address.TrimEnd('/');
                        log.Info(LogCategory.General, "Report: reports go to " + endpoint + " until the game closes (test aid).");
                    }

                    return "Report: reports go to " + endpoint + "/v1/reports" + (endpoint != Edition.ReportsEndpoint ? " (test aid; 'ss report url reset' puts back " + Edition.ReportsEndpoint + ")" : string.Empty) + ".";
                case "send":
                    if (!Edition.Developer)
                    {
                        return "Report: press Send in the window.";
                    }

                    if (!IsOpen)
                    {
                        Open(verb.Length > 4 ? verb.Substring(4).Trim() : null, localPlayer);
                    }

                    if (stage != Stage.Compose)
                    {
                        return "Report: nothing to send now (" + stage + ").";
                    }

                    Send(localPlayer);
                    return "Report: sending (test aid, as the Send button)" + (group != null ? " with " + group : string.Empty) + ".";
                case "gzipstream":
                    return Edition.Developer ? TryGZipStream() : "Report: a test aid of the developers' build.";
                case "escape":
                    if (!Edition.Developer)
                    {
                        return "Report: a test aid of the developers' build.";
                    }

                    const string Sample = "a b%c!*'()~ ñ\n";
                    return "Report: the game's Uri.EscapeDataString writes '" + Uri.EscapeDataString(Sample) + "'; the mod sends '" + ReportRequest.PercentEncode(Sample) + "'.";
                default:
                    Open(verb, localPlayer);
                    return "Report: the window is open with that text.";
            }
        }

        /// <summary>Draws the window, or the notice of another player's report. Only from OnGUI.</summary>
        public void OnGUI(bool cursorFree)
        {
            if (releaseFocus)
            {
                releaseFocus = false;
                GUIUtility.keyboardControl = 0;
            }

            Styles();
            if (!IsOpen)
            {
                DrawNotice(cursorFree);
                return;
            }

            HandleKeys();
            if (!IsOpen)
            {
                return;
            }

            float k = styledFor;
            float width = Mathf.Min(700f * k, Screen.width - 40f);
            float height = Mathf.Min(stage == Stage.Compose ? ComposeHeight(width - (44f * k), k) : 330f * k, Screen.height - 40f);
            var panel = new Rect(Mathf.Round((Screen.width - width) * 0.5f), Mathf.Round((Screen.height - height) * 0.5f), width, height);
            GUI.Box(panel, GUIContent.none, panelStyle);
            float x = panel.x + (22f * k);
            float inner = panel.width - (44f * k);
            float y = panel.y + (16f * k);
            GUI.Label(new Rect(panel.x, y, panel.width, 34f * k), Texts.Pick("REPORT A BUG", "REPORTAR UN BUG"), titleStyle);
            y += 42f * k;
            switch (stage)
            {
                case Stage.Compose:
                    DrawCompose(panel, x, inner, y, k);
                    break;
                case Stage.Packing:
                case Stage.Sending:
                    DrawWorking(panel, x, inner, y, k);
                    break;
                case Stage.Sent:
                    DrawSent(panel, x, inner, y, k);
                    break;
                case Stage.Failed:
                    DrawFailed(panel, x, inner, y, k);
                    break;
            }
        }

        // The compose page's height: its fixed rows, and the two paragraphs at the width they wrap to.
        private float ComposeHeight(float inner, float k)
        {
            float height = (16f + 42f + 26f + 128f + 26f + 64f + 26f + 40f + 6f + 8f + 46f + 18f) * k;
            content.text = Notice();
            height += smallStyle.CalcHeight(content, inner);
            if (group != null)
            {
                content.text = GroupLine();
                height += textStyle.CalcHeight(content, inner) + (12f * k);
            }

            return height;
        }

        private string GroupLine()
        {
            return Texts.Pick(
                groupName + " sent a bug report (" + group + "). Send your log too, so that it goes with theirs? Saying what you saw helps, but it is not needed.",
                groupName + " mandó un reporte de bug (" + group + "). ¿Mandás tu log también, para que vaya con el suyo? Contar qué viste ayuda, pero no hace falta.");
        }

        private static string Notice()
        {
            return Texts.Pick(
                "What is sent: this mod's logs (this game's and up to five before it), its settings and keys, the game's output_log.txt, and a picture of the session right now (players, vehicles, objects). They carry the players' Steam names and IDs and what was said in the chat. Your Windows user folder is taken out of the paths. Nothing else is sent, and nothing until you press Send.",
                "Qué se manda: los logs del mod (el de esta partida y hasta cinco anteriores), sus ajustes y teclas, el output_log.txt del juego y una foto de la sesión en este momento (jugadores, vehículos, objetos). Llevan los nombres e identificadores de Steam de los jugadores y lo que se dijo en el chat. La carpeta de tu usuario de Windows se saca de las rutas. No se manda nada más, y nada hasta que aprietes Enviar.");
        }

        private void DrawCompose(Rect panel, float x, float inner, float y, float k)
        {
            if (group != null)
            {
                y = Paragraph(x, y, inner, GroupLine(), textStyle);
                y += 6f * k;
            }

            content.text = Texts.Pick("What happened?", "¿Qué pasó?");
            GUI.Label(new Rect(x, y, inner, 24f * k), content, textStyle);
            string count = description.Length + " / " + ReportRequest.MaxDescription;
            GUI.Label(new Rect(x, y, inner, 24f * k), count, rightStyle);
            y += 26f * k;
            GUI.SetNextControlName(TextControl);
            string typed = GUI.TextArea(new Rect(x, y, inner, 120f * k), description ?? string.Empty, ReportRequest.MaxDescription, areaStyle);
            description = typed.Length > ReportRequest.MaxDescription ? typed.Substring(0, ReportRequest.MaxDescription) : typed;
            if (focus)
            {
                focus = false;
                GUI.FocusControl(TextControl);
            }

            y += 128f * k;
            GUI.Label(new Rect(x, y, inner, 24f * k), Texts.Pick("What was it about? (tick any)", "¿De qué se trataba? (marcá los que quieras)"), textStyle);
            y += 26f * k;
            float column = inner / 3f;
            for (int i = 0; i < ticked.Length; i++)
            {
                var place = new Rect(x + (column * (i % 3)), y + (30f * k * (i / 3)), column - (6f * k), 28f * k);
                ticked[i] = GUI.Toggle(place, ticked[i], " " + Texts.Pick(TagsEnglish[i], TagsSpanish[i]), toggleStyle);
            }

            y += 64f * k;
            GUI.Label(new Rect(x, y, inner, 24f * k), Texts.Pick("Your Discord user name (optional, if we may ask you about it)", "Tu usuario de Discord (opcional, por si te preguntamos algo)"), textStyle);
            y += 26f * k;
            contact = GUI.TextField(new Rect(x, y, inner * 0.6f, 30f * k), contact ?? string.Empty, ReportRequest.MaxContact, fieldStyle);
            y += 40f * k;
            y = Paragraph(x, y, inner, Notice(), smallStyle);
            float buttonWidth = 200f * k;
            float buttons = Mathf.Max(y + (8f * k), panel.yMax - (62f * k));
            if (GUI.Button(new Rect(panel.x + (panel.width * 0.5f) - buttonWidth - (8f * k), buttons, buttonWidth, 46f * k), group != null ? Texts.Pick("Send mine", "Mandar el mío") : Texts.Pick("Send", "Enviar"), sendStyle))
            {
                Send(player);
                return;
            }

            if (GUI.Button(new Rect(panel.x + (panel.width * 0.5f) + (8f * k), buttons, buttonWidth, 46f * k), group != null ? Texts.Pick("No", "No") : Texts.Pick("Cancel", "Cancelar"), buttonStyle))
            {
                if (group != null)
                {
                    noticeAnswered = true;
                    log.Info(LogCategory.General, "Report: this player did not send their log with " + group + ".");
                }

                Close();
            }
        }

        private void DrawWorking(Rect panel, float x, float inner, float y, float k)
        {
            int dots = 1 + ((int)((now - stageSince) * 2.0) % 3);
            string seconds = ((int)(now - stageSince)).ToString(CultureInfo.InvariantCulture);
            string line = stage == Stage.Packing
                ? Texts.Pick("Putting the report together", "Armando el reporte") + new string('.', dots)
                : Texts.Pick("Sending", "Enviando") + new string('.', dots) + "  " + seconds + " s";
            GUI.Label(new Rect(x, y + (30f * k), inner, 40f * k), line, workingStyle);
            Paragraph(x, y + (90f * k), inner, Texts.Pick(
                "It goes through Steam. A big report can take a minute on a slow connection; you can keep this window open meanwhile.",
                "Va por Steam. Un reporte grande puede tardar un minuto con una conexión lenta; dejá esta ventana abierta mientras tanto."), smallStyle);
        }

        private void DrawSent(Rect panel, float x, float inner, float y, float k)
        {
            GUI.Label(new Rect(x, y, inner, 24f * k), Texts.Pick("Sent. Its code:", "Enviado. Su código:"), textStyle);
            y += 28f * k;
            GUI.Label(new Rect(x, y, inner, 64f * k), code, codeStyle);
            y += 70f * k;
            Paragraph(x, y, inner, Texts.Pick(
                "Thank you. Say this code if you write about it on the Discord: it is how the report is found.",
                "Gracias. Decí este código si escribís sobre esto en el Discord: así se encuentra el reporte."), smallStyle);
            float buttonWidth = 200f * k;
            float buttons = panel.yMax - (62f * k);
            if (GUI.Button(new Rect(panel.x + (panel.width * 0.5f) - buttonWidth - (8f * k), buttons, buttonWidth, 46f * k), copied ? Texts.Pick("Copied", "Copiado") : Texts.Pick("Copy the code", "Copiar el código"), sendStyle))
            {
                var editor = new TextEditor { content = new GUIContent(code) };
                editor.SelectAll();
                editor.Copy();
                copied = true;
            }

            if (GUI.Button(new Rect(panel.x + (panel.width * 0.5f) + (8f * k), buttons, buttonWidth, 46f * k), Texts.Pick("Close", "Cerrar"), buttonStyle))
            {
                Close();
            }
        }

        private void DrawFailed(Rect panel, float x, float inner, float y, float k)
        {
            GUI.Label(new Rect(x, y, inner, 24f * k), Texts.Pick("It was not sent.", "No se mandó."), textStyle);
            y += 30f * k;
            Paragraph(x, y, inner, problem, textStyle);
            float buttonWidth = 200f * k;
            float buttons = panel.yMax - (62f * k);
            string again = canGoAlone ? Texts.Pick("Send as my own", "Mandarlo como propio") : Texts.Pick("Try again", "Probar de nuevo");
            if (GUI.Button(new Rect(panel.x + (panel.width * 0.5f) - buttonWidth - (8f * k), buttons, buttonWidth, 46f * k), again, sendStyle))
            {
                if (canGoAlone)
                {
                    group = null;
                    groupName = null;
                    noticeAnswered = true;
                }

                stage = Stage.Compose;
                stageSince = now;
                return;
            }

            if (GUI.Button(new Rect(panel.x + (panel.width * 0.5f) + (8f * k), buttons, buttonWidth, 46f * k), Texts.Pick("Close", "Cerrar"), buttonStyle))
            {
                Close();
            }
        }

        // Another player's report, at the top of the screen for a while: answered with the buttons where the cursor is free,
        // and otherwise with the report's key, which opens the window on it.
        private void DrawNotice(bool cursorFree)
        {
            if (!AnswerableNotice() || now - noticeSince > NoticeShown)
            {
                return;
            }

            float k = styledFor;
            float width = Mathf.Min(620f * k, Screen.width - 40f);
            string line = Texts.Pick(
                notice.Name + " sent a bug report (" + notice.Code + "). Send your log too?",
                notice.Name + " mandó un reporte de bug (" + notice.Code + "). ¿Mandar tu log también?");
            string how = cursorFree ? string.Empty : Texts.Pick(
                "Press " + Key() + " to answer (or Esc, then REPORT A BUG).",
                "Apretá " + Key() + " para responder (o Esc y REPORT A BUG).");
            content.text = line;
            float textHeight = textStyle.CalcHeight(content, width - (24f * k));
            float height = textHeight + (cursorFree ? 58f * k : 42f * k);
            var box = new Rect(Mathf.Round((Screen.width - width) * 0.5f), Mathf.Round(Screen.height * 0.08f), width, height);
            Color before = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.96f);
            GUI.DrawTexture(box, noticeTexture);
            GUI.color = before;
            GUI.Label(new Rect(box.x + (12f * k), box.y + (8f * k), box.width - (24f * k), textHeight), content, textStyle);
            float y = box.y + (10f * k) + textHeight;
            if (!cursorFree)
            {
                GUI.Label(new Rect(box.x + (12f * k), y, box.width - (24f * k), 26f * k), how, smallStyle);
                return;
            }

            float buttonWidth = 170f * k;
            if (GUI.Button(new Rect(box.x + (box.width * 0.5f) - buttonWidth - (6f * k), y + (4f * k), buttonWidth, 36f * k), Texts.Pick("Send mine...", "Mandar el mío..."), sendStyle))
            {
                Open(null, latestPlayer);
            }

            if (GUI.Button(new Rect(box.x + (box.width * 0.5f) + (6f * k), y + (4f * k), buttonWidth, 36f * k), Texts.Pick("No", "No"), buttonStyle))
            {
                noticeAnswered = true;
                log.Info(LogCategory.General, "Report: this player did not send their log with " + notice.Code + ".");
            }
        }

        private void HandleKeys()
        {
            Event current = Event.current;
            if (current.type != EventType.KeyDown || current.keyCode != KeyCode.Escape)
            {
                return;
            }

            current.Use();
            if (stage == Stage.Compose && group != null)
            {
                noticeAnswered = true;
            }

            Close();
        }

        private void Send(Transform localPlayer)
        {
            request = new ReportRequest
            {
                Version = BuildInfo.InformationalVersion,
                Edition = Edition.Developer ? "dev" : "public",
                Group = group,
                Description = description,
                Contact = contact,
            };

            NetSession session = manager.Session;
            bool connected = session != null && session.State == SessionState.Connected && (manager.Mode == NetworkMode.Host || manager.Mode == NetworkMode.Client);
            request.Role = !connected ? ReportRequest.RoleSolo : session.Role == SessionRole.Host ? ReportRequest.RoleHost : ReportRequest.RoleGuest;
            request.Players = connected ? session.Players.Count : 1;
            request.Player = connected ? session.LocalPlayerId : 0;
            for (int i = 0; i < ticked.Length; i++)
            {
                if (ticked[i])
                {
                    request.Tick(ReportRequest.Tags[i]);
                }
            }

            // The session as this game has it, in the log first: the log that goes is read after this.
            string picture;
            try
            {
                picture = snapshot(localPlayer);
            }
            catch (Exception exception)
            {
                picture = "The snapshot failed: " + exception;
            }

            // Not in the general category: that one is printed in MSCLoader's console as well, and this is long.
            log.Info(LogCategory.General, "Report: the player pressed Send" + (group != null ? " (with " + group + ")" : string.Empty) + "; the session at this moment follows.");
            log.Info(LogCategory.Player, "Report: the session at this moment:\n" + picture);
            var sources = new List<ReportSource>();
            sources.Add(new ReportSource { Name = "snapshot.txt", Bytes = new UTF8Encoding(false).GetBytes(picture), Required = true });
            string logPath = ModRuntime.LogPath;
            sources.Add(new ReportSource { Name = logPath != null ? Path.GetFileName(logPath) : "SummerSync.log", Path = logPath, Required = true });
            sources.Add(new ReportSource { Name = "output_log.txt", Path = GameLog(), Required = true });
            string folder = ModRuntime.SettingsFolder;
            sources.Add(new ReportSource { Name = "settings.json", Path = folder == null ? null : Path.Combine(folder, "settings.json"), Required = true });
            sources.Add(new ReportSource { Name = "keybinds.json", Path = folder == null ? null : Path.Combine(folder, "keybinds.json"), Required = true });
            for (int i = 1; logPath != null && i <= FileLogSink.PreviousLogs; i++)
            {
                string earlier = FileLogSink.GetPreviousPath(logPath, i);
                sources.Add(new ReportSource { Name = Path.GetFileName(earlier), Path = earlier, Required = false });
            }

            packer = new ReportPacker(sources, Facts(request, localPlayer), Profiles());
            packer.Start();
            stage = Stage.Packing;
            stageSince = now;
        }

        // What report.json says besides its files: the headers' facts, and when, where and on what.
        private Dictionary<string, object> Facts(ReportRequest made, Transform localPlayer)
        {
            var tags = new List<string>(made.TagsTicked);
            var sent = new Dictionary<string, string>();
            foreach (KeyValuePair<string, string> header in made.Headers())
            {
                sent[header.Key] = header.Value;
            }

            string group;
            var facts = new Dictionary<string, object>();
            facts["format"] = 1;
            facts["time"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            facts["localTime"] = DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
            facts["version"] = sent["X-SS-Version"];
            facts["edition"] = sent["X-SS-Edition"];
            facts["role"] = sent["X-SS-Role"];
            facts["players"] = int.Parse(sent["X-SS-Players"], CultureInfo.InvariantCulture);
            facts["player"] = int.Parse(sent["X-SS-Player"], CultureInfo.InvariantCulture);
            facts["group"] = sent.TryGetValue("X-SS-Group", out group) ? group : null;
            facts["description"] = ReportRequest.CleanDescription(made.Description);
            facts["tags"] = tags;
            facts["contact"] = ReportRequest.CleanContact(made.Contact);
            facts["scene"] = Application.loadedLevelName;
            facts["inWorld"] = localPlayer != null;
            facts["language"] = Texts.Spanish ? "es" : "en";
            facts["protocol"] = (int)Protocol.Version;
            facts["game"] = manager.GameVersion;
            facts["mscloader"] = ModLoader.MSCLoader_Ver;
            facts["unity"] = Application.unityVersion;
            facts["system"] = SystemInfo.operatingSystem;
            float frame = Time.smoothDeltaTime;
            facts["framesPerSecond"] = frame > 0f ? (double)Mathf.Round(1f / frame) : 0.0;
            return facts;
        }

        private void Progress()
        {
            if (stage == Stage.Packing && packer != null && packer.Done)
            {
                if (packer.Error != null || packer.Bytes == null)
                {
                    log.Error(LogCategory.General, "Report: it could not be put together: " + packer.Error);
                    Fail(Texts.Pick("The report could not be put together (" + packer.Error + ").", "El reporte no se pudo armar (" + packer.Error + ")."), "packing: " + packer.Error);
                    packer = null;
                    return;
                }

                byte[] body = packer.Bytes;
                log.Info(LogCategory.General, "Report: packed, " + packer.Summary + "; sending it to " + endpoint + "/v1/reports.");
                packer = null;
                upload = new SteamUpload(endpoint + "/v1/reports", request.Headers(), "application/gzip", body, TimeoutMs, BuildInfo.ProductName + "/" + ReportRequest.CleanVersion(BuildInfo.InformationalVersion));
                stage = Stage.Sending;
                stageSince = now;
            }

            if (stage != Stage.Sending || upload == null)
            {
                return;
            }

            if (!upload.Done && now - stageSince < MostWaiting)
            {
                return;
            }

            SteamUpload done = upload;
            upload = null;
            ReportAnswer answer = done.Done && done.Answered ? ReportAnswer.Read(done.Status, done.Body, done.RetryAfter) : ReportAnswer.None();
            string why = done.Done ? done.Error : "Steam did not answer in " + MostWaiting + " s";
            bool noSteam = done.SteamMissing;
            done.Dispose();
            Answered(answer, why, noSteam);
        }

        private void Answered(ReportAnswer answer, string why, bool noSteam)
        {
            switch (answer.Outcome)
            {
                case ReportOutcome.Sent:
                    code = answer.Id;
                    lastCode = code;
                    lastProblem = null;
                    stage = Stage.Sent;
                    stageSince = now;
                    log.Info(LogCategory.General, "Report: sent as " + code + (group != null ? " (with the report " + group + ")" : string.Empty) + ".");
                    if (group != null)
                    {
                        noticeAnswered = true;
                    }
                    else
                    {
                        ReportReplication reports = manager.Reports;
                        if (reports != null)
                        {
                            reports.Announce(code);
                        }
                    }

                    return;
                case ReportOutcome.NoAnswer:
                    Fail(noSteam
                        ? Texts.Pick("Steam is not running here, and the report goes through Steam. Open the game from Steam and try again.", "Steam no está abierto, y el reporte va por Steam. Abrí el juego desde Steam y probá de nuevo.")
                        : Texts.Pick("No connection: the report could not reach the server. Check your internet and try again.", "Sin conexión: el reporte no llegó al servidor. Revisá tu internet y probá de nuevo."),
                        "no answer: " + why);
                    return;
                case ReportOutcome.TooBig:
                    Fail(Texts.Pick("The report is too big for the server.", "El reporte es demasiado grande para el servidor."), "413 " + answer.ServerError);
                    return;
                case ReportOutcome.TooMany:
                    Fail(Texts.Pick(
                        "Too many reports from your connection for now: try again in " + answer.RetryAfterMinutes + " minute(s).",
                        "Demasiados reportes desde tu conexión por ahora: probá de nuevo en " + answer.RetryAfterMinutes + " minuto(s)."), "429 " + answer.ServerError + ", retry after " + answer.RetryAfterSeconds + " s");
                    return;
                case ReportOutcome.ServiceFull:
                    Fail(Texts.Pick(
                        "The reports' service is full for today. Try again tomorrow (in about " + answer.RetryAfterHours + " hour(s)).",
                        "El servicio de reportes está lleno por hoy. Probá de nuevo mañana (en unas " + answer.RetryAfterHours + " hora(s))."), "503 " + answer.ServerError + ", retry after " + answer.RetryAfterSeconds + " s");
                    return;
                case ReportOutcome.GroupClosed:
                    canGoAlone = true;
                    Fail(Texts.Pick(
                        "The other player's report no longer takes your log: it is over 15 minutes old, or yours is already in it. You can send yours as a report of its own.",
                        "El reporte del otro jugador ya no recibe tu log: tiene más de 15 minutos, o el tuyo ya está. Podés mandar el tuyo como un reporte propio."), "404 " + answer.ServerError);
                    return;
                case ReportOutcome.Refused:
                    Fail(Texts.Pick("The server did not accept the report: a fault of this version of the mod (the log says what).", "El servidor no aceptó el reporte: una falla de esta versión del mod (el log dice cuál)."), "400 " + answer.ServerError);
                    return;
                default:
                    Fail(Texts.Pick("The server had a problem (" + answer.Status + "). Try again in a moment.", "El servidor tuvo un problema (" + answer.Status + "). Probá de nuevo en un rato."), answer.Status + " " + answer.ServerError);
                    return;
            }
        }

        private void Fail(string forPlayer, string forLog)
        {
            problem = forPlayer;
            lastProblem = forLog;
            stage = Stage.Failed;
            stageSince = now;
            log.Warning(LogCategory.General, "Report: not sent (" + forLog + ").");
        }

        private void TakeNotices()
        {
            ReportReplication reports = manager.Reports;
            if (reports == null)
            {
                return;
            }

            arrived.Clear();
            reports.Take(arrived);
            for (int i = 0; i < arrived.Count; i++)
            {
                notice = arrived[i];
                noticeSince = now;
                noticeAnswered = false;
            }
        }

        private bool AnswerableNotice()
        {
            return notice != null && !noticeAnswered && now - noticeSince <= NoticeAnswerable && notice.Code != lastCode;
        }

        private string Status()
        {
            var text = new StringBuilder("Report: ");
            text.Append(IsOpen ? "the window is " + stage : "the window is closed").Append("; reports go to ").Append(endpoint).Append("/v1/reports");
            text.Append(lastCode != null ? "; the last one sent here is " + lastCode : "; none sent here yet");
            text.Append(lastProblem != null ? "; the last problem: " + lastProblem : string.Empty);
            if (notice != null)
            {
                text.Append("; ").Append(notice.Name).Append("'s report ").Append(notice.Code).Append(AnswerableNotice() ? " waits for an answer" : " was answered or is too old");
            }

            text.Append("; the Esc menu's button ").Append(pause.Shown ? "is on screen" : "shows with the Esc menu").Append("; key ").Append(Key()).Append('.');
            return text.ToString();
        }

        // Where the game writes its own log: given on its command line (MSCLoader's preloader adds "-logFile"), or Unity's place.
        private static string GameLog()
        {
            try
            {
                string[] arguments = Environment.GetCommandLineArgs();
                for (int i = 0; i + 1 < arguments.Length; i++)
                {
                    if (string.Equals(arguments[i], "-logFile", StringComparison.OrdinalIgnoreCase) && arguments[i + 1].Length > 0 && arguments[i + 1] != "-")
                    {
                        return arguments[i + 1];
                    }
                }
            }
            catch (Exception)
            {
                // No command line to read: Unity's own place.
            }

            string inData = Path.Combine(Application.dataPath, "output_log.txt");
            if (File.Exists(inData))
            {
                return inData;
            }

            return Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? string.Empty, "output_log.txt");
        }

        // The user's folders as the system names them: they are taken out of the report wherever they show.
        private static List<string> Profiles()
        {
            var profiles = new List<string>();
            Environment.SpecialFolder[] folders = { Environment.SpecialFolder.Personal, Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.Desktop };
            for (int i = 0; i < folders.Length; i++)
            {
                try
                {
                    string folder = Environment.GetFolderPath(folders[i]);
                    if (!string.IsNullOrEmpty(folder))
                    {
                        profiles.Add(folder);

                        // The profile itself, the folder above "Documents" or "AppData\Local".
                        string parent = Path.GetDirectoryName(folder);
                        if (folders[i] == Environment.SpecialFolder.Personal && !string.IsNullOrEmpty(parent))
                        {
                            profiles.Add(parent);
                        }
                    }
                }
                catch (Exception)
                {
                    // A folder the system does not have.
                }
            }

            try
            {
                string profile = Environment.GetEnvironmentVariable("USERPROFILE");
                if (!string.IsNullOrEmpty(profile))
                {
                    profiles.Add(profile);
                }
            }
            catch (Exception)
            {
                // No environment to read.
            }

            return profiles;
        }

        private static string TryGZipStream()
        {
            try
            {
                using (var stream = new MemoryStream())
                {
                    byte[] data = Encoding.ASCII.GetBytes(new string('a', 1000));
                    using (var gzip = new System.IO.Compression.GZipStream(stream, System.IO.Compression.CompressionMode.Compress, true))
                    {
                        gzip.Write(data, 0, data.Length);
                    }

                    return "Report: the game's GZipStream worked (1000 bytes made " + stream.Length + ").";
                }
            }
            catch (Exception exception)
            {
                return "Report: the game's GZipStream failed: " + exception.GetType().FullName + ": " + exception.Message;
            }
        }

        private bool KeyDown()
        {
            SettingsKeybind key = ModRuntime.ReportKey;
            return key != null ? key.GetKeybindDown() : Input.GetKeyDown(DefaultKey);
        }

        private static string Key()
        {
            SettingsKeybind key = ModRuntime.ReportKey;
            return (key != null ? key.GetKeyValue : DefaultKey).ToString();
        }

        // Somewhere else that takes typing: MSCLoader's console or settings, or the game's computer.
        private static bool Typing()
        {
            EventSystem events = EventSystem.current;
            GameObject selected = events == null ? null : events.currentSelectedGameObject;
            InputField field = selected == null ? null : selected.GetComponent<InputField>();
            if (field != null && field.isFocused)
            {
                return true;
            }

            FsmBool computer = Application.loadedLevelName == "GAME" ? FsmVariables.GlobalVariables.FindFsmBool("PlayerComputer") : null;
            return computer != null && computer.Value;
        }

        // The game's mouse look held while the cursor is in use, and the game told it is in a menu (as the character window).
        private void HoldTheGame(bool hold)
        {
            if (hold == holdingGame)
            {
                return;
            }

            holdingGame = hold;
            if (hold)
            {
                var found = new List<Behaviour>();
                Behaviour[] behaviours = player == null ? new Behaviour[0] : player.GetComponentsInChildren<Behaviour>(true);
                for (int i = 0; i < behaviours.Length; i++)
                {
                    if (behaviours[i] != null && behaviours[i].GetType().Name == LookComponent && behaviours[i].enabled)
                    {
                        behaviours[i].enabled = false;
                        found.Add(behaviours[i]);
                    }
                }

                held = found.ToArray();
                inMenu = FsmVariables.GlobalVariables.FindFsmBool("PlayerInMenu");
                if (inMenu != null)
                {
                    inMenuBefore = inMenu.Value;
                    inMenu.Value = true;
                }

                return;
            }

            for (int i = 0; i < held.Length; i++)
            {
                // Unity's null check is also true for a destroyed object.
                if (held[i] != null)
                {
                    held[i].enabled = true;
                }
            }

            held = new Behaviour[0];
            if (inMenu != null)
            {
                inMenu.Value = inMenuBefore;
                inMenu = null;
            }

            // Back to the game's cursor, unless its own Esc menu is up under the window.
            if (Application.loadedLevelName == "GAME" && !inMenuBefore && !pause.Shown)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
        }

        private float Paragraph(float x, float y, float width, string text, GUIStyle style)
        {
            content.text = text ?? string.Empty;
            float height = style.CalcHeight(content, width);
            GUI.Label(new Rect(x, y, width, height), content, style);
            return y + height + (6f * styledFor);
        }


        private void Styles()
        {
            float k = Mathf.Clamp(Screen.height / 1080f, 0.75f, 2f);
            if (panelStyle != null && Mathf.Approximately(k, styledFor))
            {
                return;
            }

            styledFor = k;
            if (panelTexture == null)
            {
                panelTexture = Solid(new Color(0.07f, 0.08f, 0.1f, 0.95f));
                sendTexture = Solid(new Color(0.18f, 0.55f, 0.32f, 1f));
                noticeTexture = Solid(new Color(0.09f, 0.1f, 0.13f, 0.92f));
            }

            panelStyle = new GUIStyle(GUI.skin.box);
            panelStyle.normal.background = panelTexture;
            titleStyle = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(26 * k), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            titleStyle.normal.textColor = new Color(0.95f, 0.85f, 0.55f);
            textStyle = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(16 * k), wordWrap = true, richText = false };
            textStyle.normal.textColor = new Color(0.92f, 0.93f, 0.95f);
            smallStyle = new GUIStyle(textStyle) { fontSize = Mathf.RoundToInt(13 * k) };
            smallStyle.normal.textColor = new Color(0.72f, 0.74f, 0.78f);
            rightStyle = new GUIStyle(smallStyle) { alignment = TextAnchor.MiddleRight, wordWrap = false };
            codeStyle = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(46 * k), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            codeStyle.normal.textColor = new Color(0.95f, 0.85f, 0.55f);
            workingStyle = new GUIStyle(codeStyle) { fontSize = Mathf.RoundToInt(24 * k) };
            buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = Mathf.RoundToInt(17 * k), fontStyle = FontStyle.Bold };
            sendStyle = new GUIStyle(buttonStyle);
            sendStyle.normal.background = sendTexture;
            sendStyle.hover.background = sendTexture;
            sendStyle.normal.textColor = Color.white;
            sendStyle.hover.textColor = Color.white;
            fieldStyle = new GUIStyle(GUI.skin.textField) { fontSize = Mathf.RoundToInt(16 * k), alignment = TextAnchor.MiddleLeft, richText = false };
            areaStyle = new GUIStyle(GUI.skin.textArea) { fontSize = Mathf.RoundToInt(16 * k), wordWrap = true, richText = false };
            toggleStyle = new GUIStyle(GUI.skin.toggle) { fontSize = Mathf.RoundToInt(15 * k) };
            toggleStyle.normal.textColor = textStyle.normal.textColor;
            toggleStyle.onNormal.textColor = new Color(0.95f, 0.85f, 0.55f);
            toggleStyle.hover.textColor = Color.white;
            toggleStyle.onHover.textColor = new Color(0.95f, 0.85f, 0.55f);
        }

        private static Texture2D Solid(Color color)
        {
            var texture = new Texture2D(1, 1);
            texture.SetPixel(0, 0, color);
            texture.Apply();
            return texture;
        }
    }
}
