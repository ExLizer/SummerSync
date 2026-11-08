using System;
using System.Text;
using Steamworks;

namespace SummerSync.Unity.Net
{
    /// <summary>
    /// A file fetched over HTTPS by the Steam client (<see cref="SteamHTTP"/>): the game's Mono and Unity's own downloads
    /// speak only TLS 1.0 ("tlsv1 alert protocol version" from GitHub, measured 2026-10-16), and a mod may not start a
    /// program that would (MSCLoader stops it). A redirect (GitHub's release downloads move twice) is followed here if Steam
    /// does not follow it itself, in the next frame (a call result set inside its own answer is cleared when it returns).
    /// Steam must be running: the game starts it.
    /// </summary>
    internal sealed class SteamDownload : IDisposable
    {
        private const int MostRedirects = 5;
        private const uint TimeoutMs = 60000;

        private CallResult<HTTPRequestCompleted_t> result;
        private HTTPRequestHandle request = HTTPRequestHandle.Invalid;
        private int redirects;
        private string redirectTo;

        public SteamDownload(string url)
        {
            result = CallResult<HTTPRequestCompleted_t>.Create(OnCompleted);
            Send(url);
        }

        public bool Done { get; private set; }

        /// <summary>What came, when <see cref="Done"/> and nothing went wrong.</summary>
        public byte[] Bytes { get; private set; }

        /// <summary>What went wrong, or null.</summary>
        public string Error { get; private set; }

        /// <summary>Once per frame: a redirect is followed here, out of Steam's answer to the request before.</summary>
        public void Update()
        {
            if (redirectTo != null)
            {
                string next = redirectTo;
                redirectTo = null;
                Send(next);
            }
        }

        /// <summary>How much has come, from 0 to 1.</summary>
        public float Progress
        {
            get
            {
                float percent;
                return !Done && request != HTTPRequestHandle.Invalid && SteamHTTP.GetHTTPDownloadProgressPct(request, out percent) ? percent / 100f : (Done ? 1f : 0f);
            }
        }

        public void Dispose()
        {
            Release();
            if (result != null)
            {
                result.Dispose();
                result = null;
            }
        }

        private void Send(string url)
        {
            try
            {
                request = SteamHTTP.CreateHTTPRequest(EHTTPMethod.k_EHTTPMethodGET, url);
                if (request == HTTPRequestHandle.Invalid)
                {
                    Finish(Texts.Pick("Steam did not build the request","Steam no armó el pedido"));
                    return;
                }

                SteamHTTP.SetHTTPRequestAbsoluteTimeoutMS(request, TimeoutMs);
                SteamAPICall_t call;
                if (!SteamHTTP.SendHTTPRequest(request, out call))
                {
                    Finish(Texts.Pick("Steam did not send the request", "Steam no mandó el pedido"));
                    return;
                }

                result.Set(call);
            }
            catch (Exception exception)
            {
                Finish(Texts.Pick("Steam is not available (" + exception.Message + ")", "Steam no está disponible (" + exception.Message + ")"));
            }
        }

        private void OnCompleted(HTTPRequestCompleted_t done, bool ioFailure)
        {
            try
            {
                if (ioFailure || !done.m_bRequestSuccessful)
                {
                    Finish(Texts.Pick("no answer", "sin respuesta"));
                    return;
                }

                int status = (int)done.m_eStatusCode;
                if (status >= 300 && status < 400)
                {
                    string next = Header("Location");
                    if (next == null || ++redirects > MostRedirects)
                    {
                        Finish(Texts.Pick("a redirect to nowhere (", "una redirección sin destino (") + status + ")");
                        return;
                    }

                    Release();
                    redirectTo = next;
                    return;
                }

                if (status != 200)
                {
                    Finish(Texts.Pick("HTTP ", "respuesta ") + status);
                    return;
                }

                uint size;
                if (!SteamHTTP.GetHTTPResponseBodySize(request, out size))
                {
                    Finish(Texts.Pick("no content", "sin contenido"));
                    return;
                }

                var body = new byte[size];
                if (size > 0 && !SteamHTTP.GetHTTPResponseBodyData(request, body, size))
                {
                    Finish(Texts.Pick("the content could not be read", "el contenido no se pudo leer"));
                    return;
                }

                Bytes = body;
                Finish(null);
            }
            catch (Exception exception)
            {
                Finish(exception.Message);
            }
        }

        private string Header(string name)
        {
            uint size;
            if (!SteamHTTP.GetHTTPResponseHeaderSize(request, name, out size) || size == 0)
            {
                return null;
            }

            var value = new byte[size];
            if (!SteamHTTP.GetHTTPResponseHeaderValue(request, name, value, size))
            {
                return null;
            }

            return Encoding.ASCII.GetString(value).TrimEnd('\0').Trim();
        }

        private void Finish(string error)
        {
            Error = error;
            Done = true;
            Release();
        }

        private void Release()
        {
            if (request != HTTPRequestHandle.Invalid)
            {
                try
                {
                    SteamHTTP.ReleaseHTTPRequest(request);
                }
                catch (Exception)
                {
                    // Steam went away: the request went with it.
                }

                request = HTTPRequestHandle.Invalid;
            }
        }
    }
}
