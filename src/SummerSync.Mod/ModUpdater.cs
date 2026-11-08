using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using SummerSync.Core;
using SummerSync.Core.Logging;
using SummerSync.Unity.Net;
using UnityEngine;

namespace SummerSync.Unity
{
    /// <summary>
    /// The main menu's offer to update the mod, done by the mod itself: no program is started (MSCLoader stops any mod
    /// that starts cmd, PowerShell or a script, and says so as malicious), and the game's Mono and Unity's own downloads
    /// cannot speak to GitHub or the site (TLS 1.0 only): the Steam client fetches for it (<see cref="SteamDownload"/>).
    /// The first time the menu shows, the latest release's version.txt is fetched from the site (its tag and the SHA-256
    /// of its SummerSync.dll, tools/Publish.ps1; <see cref="Edition.VersionFile"/>). If the tag is not this build's
    /// (<see cref="UpdateCheck"/>), the menu offers it. Accepted, the release's SummerSync.dll is fetched from GitHub
    /// (the repository in github-repo.txt) and checked against that SHA-256; the mod in the Mods folder is kept as
    /// SummerSync.dll.prev and the new one put in its place (Windows lets a loaded file be renamed). It is used the next
    /// time the game starts: the menu says so and offers to close the game. Never during a session.
    ///
    /// That is the developers' edition. The public one only says that there is a newer build and where to get it (the
    /// site's download page, opened in the browser): it installs nothing.
    /// </summary>
    internal static class ModUpdater
    {
        private const string RepoResource = "SummerSync.updater.github-repo.txt";
        private const string ModFile = "SummerSync.dll";
        private const float Width = 440f;
        private const double MostWaiting = 120.0;

        private static Stage stage = Stage.NotAsked;
        private static SteamDownload asking;
        private static double askedAt;
        private static string latest;
        private static string expectedHash;
        private static string problem;
        private static bool confirming;
        private static GUIStyle text;
        private static GUIStyle small;
        private static GUIStyle press;
        private static float styledFor;

        private enum Stage
        {
            NotAsked,
            Asking,
            UpToDate,
            Offered,
            Downloading,
            Installed,
            Failed,
            Off,
        }

        /// <summary>Once per frame; it starts asking the first time the main menu shows.</summary>
        public static void Update(double now, bool inMenu)
        {
            if (stage == Stage.NotAsked)
            {
                if (inMenu)
                {
                    askedAt = now;
                    Ask();
                }

                return;
            }

            if (asking == null || (stage != Stage.Asking && stage != Stage.Downloading))
            {
                return;
            }

            asking.Update();
            if (!asking.Done && now - askedAt > MostWaiting)
            {
                asking.Dispose();
                asking = null;
                if (stage == Stage.Asking)
                {
                    stage = Stage.Off;
                    ModRuntime.Log.Info(LogCategory.General, "Update: Steam did not answer about the latest build in " + MostWaiting + " s; the main menu offers no update.");
                }
                else
                {
                    Fail(Texts.Pick("Steam did not finish downloading it.", "Steam no terminó de bajarlo."));
                }

                return;
            }

            if (asking.Done)
            {
                if (stage == Stage.Asking)
                {
                    Heard();
                }
                else
                {
                    Install();
                }
            }
        }

        /// <summary>The offer, in a corner of the main menu, while there is no session.</summary>
        public static void OnGUI(bool inMenu, bool inSession)
        {
            bool shown = stage == Stage.Offered || stage == Stage.Downloading || stage == Stage.Installed || stage == Stage.Failed;
            if (!inMenu || inSession || !shown)
            {
                return;
            }

            // Sized for 1080 lines, and grown with the screen.
            float k = Mathf.Max(1f, Screen.height / 1080f);
            if (text == null || styledFor != k)
            {
                styledFor = k;
                text = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(15 * k), wordWrap = true };
                text.normal.textColor = Color.white;
                small = new GUIStyle(text) { fontSize = Mathf.RoundToInt(13 * k) };
                small.normal.textColor = new Color(0.85f, 0.85f, 0.85f);
                press = new GUIStyle(GUI.skin.button) { fontSize = Mathf.RoundToInt(14 * k) };
            }

            var area = new Rect(Screen.width - ((Width + 16f) * k), Screen.height - (116f * k), Width * k, 100f * k);
            GUI.Box(area, GUIContent.none);
            var line1 = new Rect(area.x + (10f * k), area.y + (6f * k), area.width - (20f * k), 24f * k);
            var line2 = new Rect(area.x + (10f * k), area.y + (30f * k), area.width - (20f * k), 24f * k);
            var button = new Rect(area.x + (10f * k), area.y + (58f * k), area.width - (20f * k), 32f * k);
            float half = (area.width - (30f * k)) / 2f;
            var left = new Rect(area.x + (10f * k), area.y + (58f * k), half, 32f * k);
            var right = new Rect(area.x + (20f * k) + half, area.y + (58f * k), half, 32f * k);
            switch (stage)
            {
                case Stage.Offered:
                    GUI.Label(line1, Texts.Pick("SummerSync: a new version is out (", "SummerSync: hay una versión nueva (") + latest + ").", text);
                    if (!Edition.Developer)
                    {
                        GUI.Label(line2, Texts.Pick("Get it at " + Edition.Site + ".", "Bajala de " + Edition.Site + "."), small);
                        if (GUI.Button(left, Texts.Pick("Open the page", "Abrir la página"), press))
                        {
                            Application.OpenURL(Edition.DownloadPage);
                        }

                        if (GUI.Button(right, Texts.Pick("Not now", "Ahora no"), press))
                        {
                            stage = Stage.Off;
                        }
                    }
                    else if (!confirming)
                    {
                        GUI.Label(line2, Texts.Pick("You have ", "Tenés ") + UpdateCheck.TagOf(BuildInfo.InformationalVersion) + ".", small);
                        if (GUI.Button(button, Texts.Pick("Update", "Actualizar"), press))
                        {
                            confirming = true;
                        }
                    }
                    else
                    {
                        GUI.Label(line2, Texts.Pick("It is downloaded and installed; used the next time you open the game.", "Se baja e instala; se usa la próxima vez que abras el juego."), small);
                        if (GUI.Button(left, Texts.Pick("Yes, update", "Sí, actualizar"), press))
                        {
                            Download();
                        }

                        if (GUI.Button(right, Texts.Pick("Not now", "Ahora no"), press))
                        {
                            confirming = false;
                        }
                    }

                    break;
                case Stage.Downloading:
                    GUI.Label(line1, Texts.Pick("SummerSync: downloading ", "SummerSync: bajando ") + latest + "...", text);
                    GUI.Label(line2, asking != null ? Mathf.RoundToInt(asking.Progress * 100f) + " %" : string.Empty, small);
                    break;
                case Stage.Installed:
                    GUI.Label(line1, "SummerSync " + latest + Texts.Pick(" installed.", " instalado."), text);
                    GUI.Label(line2, Texts.Pick("Close the game and open it again to use it.", "Cerrá el juego y volvé a abrirlo para usarlo."), small);
                    if (GUI.Button(button, Texts.Pick("Close the game", "Cerrar el juego"), press))
                    {
                        Application.Quit();
                    }

                    break;
                case Stage.Failed:
                    GUI.Label(line1, Texts.Pick("SummerSync: the update failed.", "SummerSync: no se pudo actualizar."), text);
                    GUI.Label(line2, problem + Texts.Pick(" Try actualizar.cmd.", " Probá con actualizar.cmd."), small);
                    if (GUI.Button(button, Texts.Pick("Close this notice", "Cerrar este aviso"), press))
                    {
                        stage = Stage.Off;
                    }

                    break;
            }
        }

        /// <summary>For the console ("ss update"): where the update stands; "install" installs what is offered.</summary>
        public static string Report(string what)
        {
            if (string.Equals(what, "install", StringComparison.OrdinalIgnoreCase))
            {
                if (!Edition.Developer)
                {
                    return "This build does not install updates: get the new one at " + Edition.Site + ".";
                }

                if (stage != Stage.Offered)
                {
                    return "Nothing to install: " + stage + ".";
                }

                Download();
                return "Downloading " + latest + ".";
            }

            return "Update: " + stage + "; this build is " + UpdateCheck.TagOf(BuildInfo.InformationalVersion) + (latest != null ? ", the latest published " + latest : string.Empty)
                + (problem != null ? "; " + problem : string.Empty) + (stage == Stage.Downloading && asking != null ? "; " + Mathf.RoundToInt(asking.Progress * 100f) + " % here" : string.Empty) + ".";
        }

        private static void Ask()
        {
            try
            {
                asking = Fetch(Edition.VersionFile);
                stage = Stage.Asking;
                ModRuntime.Log.Info(LogCategory.General, "Update: asking " + Edition.VersionFile + " which build is the latest published one (this is " + UpdateCheck.TagOf(BuildInfo.InformationalVersion) + ").");
            }
            catch (Exception exception)
            {
                stage = Stage.Off;
                ModRuntime.Log.Warning(LogCategory.General, "Update: could not ask which build is the latest (" + exception.Message + "); the main menu offers no update.");
            }
        }

        private static void Heard()
        {
            SteamDownload done = asking;
            asking = null;
            done.Dispose();
            string tag;
            string hash;
            if (done.Error != null || !UpdateCheck.ReadVersion(Encoding.UTF8.GetString(done.Bytes), out tag, out hash))
            {
                stage = Stage.Off;
                ModRuntime.Log.Info(LogCategory.General, "Update: the latest build could not be read (" + (done.Error ?? "not a version.txt") + "); the main menu offers no update.");
                return;
            }

            latest = tag;
            expectedHash = hash;
            stage = UpdateCheck.Differs(tag, BuildInfo.InformationalVersion) ? Stage.Offered : Stage.UpToDate;
            ModRuntime.Log.Info(LogCategory.General, "Update: the latest published build is " + tag + (stage == Stage.Offered ? "; this one is another, so the main menu offers it." : ", this one."));
        }

        private static void Download()
        {
            confirming = false;
            askedAt = ModRuntime.Network.Now;
            try
            {
                asking = Fetch("https://github.com/" + Repository() + "/releases/download/" + latest + "/" + ModFile);
            }
            catch (Exception exception)
            {
                Fail(Texts.Pick("It could not be downloaded (", "No se pudo bajar (") + exception.Message + ").");
                return;
            }

            stage = Stage.Downloading;
            ModRuntime.Log.Info(LogCategory.General, "Update: downloading " + latest + ".");
        }

        private static void Install()
        {
            SteamDownload done = asking;
            asking = null;
            try
            {
                if (done.Error != null)
                {
                    Fail(Texts.Pick("It could not be downloaded (", "No se pudo bajar (") + done.Error + ").");
                    return;
                }

                byte[] bytes = done.Bytes;
                string hash;
                using (var sha = new SHA256Managed())
                {
                    hash = UpdateCheck.Hex(sha.ComputeHash(bytes));
                }

                if (hash != expectedHash)
                {
                    Fail(Texts.Pick("What was downloaded does not match its SHA-256.", "Lo bajado no coincide con su SHA-256."));
                    return;
                }

                string mods = ModsFolder();
                string current = Path.Combine(mods, ModFile);
                string fresh = current + ".new";
                string previous = current + ".prev";
                File.WriteAllBytes(fresh, bytes);
                if (File.Exists(previous))
                {
                    File.Delete(previous);
                }

                // The loaded mod is moved aside, not overwritten: Windows lets a file in use be renamed.
                File.Move(current, previous);
                File.Move(fresh, current);
                stage = Stage.Installed;
                ModRuntime.Log.Info(LogCategory.General, "Update: " + latest + " installed in '" + current + "' (" + bytes.Length + " bytes, SHA-256 checked); the one before is '" + previous + "'. It is used when the game starts again.");
            }
            catch (Exception exception)
            {
                Fail(Texts.Pick("It could not be installed (", "No se pudo instalar (") + exception.Message + ").");
            }
            finally
            {
                done.Dispose();
            }
        }

        private static void Fail(string why)
        {
            problem = why;
            stage = Stage.Failed;
            ModRuntime.Log.Warning(LogCategory.General, "Update: " + why);
        }

        private static SteamDownload Fetch(string url)
        {
            return new SteamDownload(url);
        }

        // Where MSCLoader keeps the mods: the mod's settings are "<Mods>/Config/Mod Settings/SummerSync".
        private static string ModsFolder()
        {
            string folder = ModRuntime.SettingsFolder;
            for (int i = 0; i < 3 && !string.IsNullOrEmpty(folder); i++)
            {
                folder = Path.GetDirectoryName(folder);
            }

            if (string.IsNullOrEmpty(folder) || !File.Exists(Path.Combine(folder, ModFile)))
            {
                throw new InvalidOperationException("no encontré " + ModFile + " en la carpeta de mods");
            }

            return folder;
        }

        private static string Repository()
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(RepoResource))
            {
                if (stream == null)
                {
                    throw new InvalidOperationException("the mod does not carry '" + RepoResource + "'");
                }

                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string name = reader.ReadToEnd().Trim().TrimStart('﻿');
                    if (name.Length == 0 || name.IndexOf('/') < 0)
                    {
                        throw new InvalidOperationException("the repository '" + name + "' is not owner/name");
                    }

                    return name;
                }
            }
        }
    }
}
