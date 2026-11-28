using System;
using System.Collections.Generic;
using System.Globalization;
using SummerSync.Core.Json;

namespace SummerSync.Core.Reports
{
    /// <summary>What became of an upload, as the player is told.</summary>
    public enum ReportOutcome
    {
        /// <summary>The server took it and gave its code.</summary>
        Sent,

        /// <summary>No answer: no connection, Steam not running, or the server not reached in time.</summary>
        NoAnswer,

        /// <summary>The report is larger than the server takes (413).</summary>
        TooBig,

        /// <summary>Too many reports from this address for now (429); <see cref="ReportAnswer.RetryAfterSeconds"/> says when.</summary>
        TooMany,

        /// <summary>The reports' service took all it takes for today from everyone (503); <see cref="ReportAnswer.RetryAfterSeconds"/> says when.</summary>
        ServiceFull,

        /// <summary>The other player's report it was to go with no longer takes files, or has this player's already (404).</summary>
        GroupClosed,

        /// <summary>The server did not accept the headers (400): a fault of this build.</summary>
        Refused,

        /// <summary>The server failed (5xx), or answered something this build cannot read.</summary>
        ServerError,
    }

    /// <summary>
    /// The server's answer to an upload, read as the contract says: 200 with <c>{"id": "R-…"}</c>, or an error status with
    /// <c>{"error": "…"}</c>, and <c>Retry-After</c> with a 429.
    /// </summary>
    public sealed class ReportAnswer
    {
        private ReportAnswer(ReportOutcome outcome, int status, string id, string error, int retryAfter)
        {
            Outcome = outcome;
            Status = status;
            Id = id;
            ServerError = error;
            RetryAfterSeconds = retryAfter;
        }

        public ReportOutcome Outcome { get; private set; }

        /// <summary>The HTTP status, 0 without an answer.</summary>
        public int Status { get; private set; }

        /// <summary>The report's code when it was sent, or null.</summary>
        public string Id { get; private set; }

        /// <summary>The server's short English text of an error, or null; for the log.</summary>
        public string ServerError { get; private set; }

        /// <summary>With <see cref="ReportOutcome.TooMany"/> or <see cref="ReportOutcome.ServiceFull"/>: seconds until another report is taken (0 when not said).</summary>
        public int RetryAfterSeconds { get; private set; }

        /// <summary>Hours to tell the player to wait, rounded up, at least 1.</summary>
        public int RetryAfterHours
        {
            get { return Math.Max(1, (RetryAfterSeconds + 3599) / 3600); }
        }

        /// <summary>Minutes to tell the player to wait, rounded up, at least 1.</summary>
        public int RetryAfterMinutes
        {
            get { return Math.Max(1, (RetryAfterSeconds + 59) / 60); }
        }

        /// <summary>No answer at all.</summary>
        public static ReportAnswer None()
        {
            return new ReportAnswer(ReportOutcome.NoAnswer, 0, null, null, 0);
        }

        /// <summary>The answer of that status, with that body and that Retry-After header (either may be null).</summary>
        public static ReportAnswer Read(int status, string body, string retryAfter)
        {
            string id = null;
            string error = null;
            Dictionary<string, object> json = null;
            try
            {
                json = string.IsNullOrEmpty(body) ? null : JsonReader.Parse(body) as Dictionary<string, object>;
            }
            catch (Exception)
            {
                // Whatever came is not the contract's JSON: read by its status alone.
                json = null;
            }

            object value;
            if (json != null && json.TryGetValue("id", out value))
            {
                id = value as string;
            }

            if (json != null && json.TryGetValue("error", out value))
            {
                error = value as string;
            }

            if (status == 200)
            {
                string code = ReportCode.Normalize(id);
                return code != null
                    ? new ReportAnswer(ReportOutcome.Sent, status, code, null, 0)
                    : new ReportAnswer(ReportOutcome.ServerError, status, null, "an answer without a report's code", 0);
            }

            switch (status)
            {
                case 400:
                    return new ReportAnswer(ReportOutcome.Refused, status, null, error, 0);
                case 404:
                    return new ReportAnswer(ReportOutcome.GroupClosed, status, null, error, 0);
                case 413:
                    return new ReportAnswer(ReportOutcome.TooBig, status, null, error, 0);
                case 429:
                    return new ReportAnswer(ReportOutcome.TooMany, status, null, error, Seconds(retryAfter));
                case 503:
                    return new ReportAnswer(ReportOutcome.ServiceFull, status, null, error, Seconds(retryAfter));
                default:
                    return new ReportAnswer(ReportOutcome.ServerError, status, null, error, 0);
            }
        }

        // Retry-After in seconds; the date form is not used by the server and counts as not said.
        private static int Seconds(string retryAfter)
        {
            int seconds;
            if (retryAfter != null && int.TryParse(retryAfter.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds) && seconds > 0)
            {
                return Math.Min(seconds, 24 * 3600);
            }

            return 0;
        }
    }
}
