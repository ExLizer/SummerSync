using System;
using System.Collections.Generic;
using System.Text;
using Steamworks;

namespace SummerSync.Unity.Net
{
    /// <summary>
    /// A POST of some bytes over HTTPS by the Steam client (<see cref="SteamHTTP"/>), for the same reason as
    /// <see cref="SteamDownload"/>: the game's Mono speaks only TLS 1.0. The body goes whole with
    /// <c>SetHTTPRequestRawPostBody</c> and the headers one by one; what comes back is the status, the body as text and the
    /// <c>Retry-After</c> header. Redirects are not followed (a POST would have to be sent again). Steam must be running.
    /// </summary>
    internal sealed class SteamUpload : IDisposable
    {
        private CallResult<HTTPRequestCompleted_t> result;
        private HTTPRequestHandle request = HTTPRequestHandle.Invalid;

        public SteamUpload(string url, IList<KeyValuePair<string, string>> headers, string contentType, byte[] body, uint timeoutMs, string userAgent)
        {
            try
            {
                result = CallResult<HTTPRequestCompleted_t>.Create(OnCompleted);
                request = SteamHTTP.CreateHTTPRequest(EHTTPMethod.k_EHTTPMethodPOST, url);
                if (request == HTTPRequestHandle.Invalid)
                {
                    Finish("Steam did not build the request (" + url + ")");
                    return;
                }

                SteamHTTP.SetHTTPRequestAbsoluteTimeoutMS(request, timeoutMs);
                if (!string.IsNullOrEmpty(userAgent))
                {
                    SteamHTTP.SetHTTPRequestUserAgentInfo(request, userAgent);
                }

                for (int i = 0; i < headers.Count; i++)
                {
                    if (!SteamHTTP.SetHTTPRequestHeaderValue(request, headers[i].Key, headers[i].Value))
                    {
                        Finish("Steam did not take the header " + headers[i].Key);
                        return;
                    }
                }

                if (!SteamHTTP.SetHTTPRequestRawPostBody(request, contentType, body, (uint)body.Length))
                {
                    Finish("Steam did not take the body (" + body.Length + " bytes)");
                    return;
                }

                SteamAPICall_t call;
                if (!SteamHTTP.SendHTTPRequest(request, out call))
                {
                    Finish("Steam did not send the request");
                    return;
                }

                result.Set(call);
            }
            catch (Exception exception)
            {
                // Without Steam every call of Steamworks throws.
                SteamMissing = true;
                Finish("Steam is not available (" + exception.Message + ")");
            }
        }

        public bool Done { get; private set; }

        /// <summary>Whether a server answered at all (with any status).</summary>
        public bool Answered { get; private set; }

        /// <summary>Whether it failed because Steam is not running here.</summary>
        public bool SteamMissing { get; private set; }

        public int Status { get; private set; }

        /// <summary>What the server answered, as UTF-8 text; empty for none.</summary>
        public string Body { get; private set; }

        /// <summary>The answer's Retry-After header, or null.</summary>
        public string RetryAfter { get; private set; }

        /// <summary>Why there is no answer, in English for the log; null when there is one.</summary>
        public string Error { get; private set; }

        /// <summary>Whether Steam gave up waiting.</summary>
        public bool TimedOut { get; private set; }

        public void Dispose()
        {
            Release();
            if (result != null)
            {
                try
                {
                    result.Dispose();
                }
                catch (Exception)
                {
                    // Steam went away: the call went with it.
                }

                result = null;
            }
        }

        private void OnCompleted(HTTPRequestCompleted_t done, bool ioFailure)
        {
            try
            {
                if (ioFailure || !done.m_bRequestSuccessful)
                {
                    bool late;
                    TimedOut = request != HTTPRequestHandle.Invalid && SteamHTTP.GetHTTPRequestWasTimedOut(request, out late) && late;
                    Finish(TimedOut ? "no answer in time" : "no answer (no connection, or the server could not be reached)");
                    return;
                }

                Answered = true;
                Status = (int)done.m_eStatusCode;
                RetryAfter = Header("Retry-After");
                uint size;
                if (SteamHTTP.GetHTTPResponseBodySize(request, out size) && size > 0)
                {
                    var bytes = new byte[size];
                    if (SteamHTTP.GetHTTPResponseBodyData(request, bytes, size))
                    {
                        Body = Encoding.UTF8.GetString(bytes);
                    }
                }

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
            Body = Body ?? string.Empty;
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
