using System.Text;
using SummerSync.Core.Reports;

namespace SummerSync.Core.Tests.Reports;

public class ReportFieldsTests
{
    private static Dictionary<string, string> HeadersOf(ReportRequest request) => request.Headers().ToDictionary(h => h.Key, h => h.Value);

    [Fact]
    public void The_users_folder_becomes_USERPROFILE_with_either_slash_and_any_case()
    {
        var scrubber = new PathScrubber(new[] { @"C:\Users\Julián Pérez" });
        Assert.Equal(@"Log file: %USERPROFILE%\AppData\LocalLow\Amistech", scrubber.Scrub(@"Log file: C:\Users\Julián Pérez\AppData\LocalLow\Amistech"));
        Assert.Equal("at %USERPROFILE%/Documents/x.txt", scrubber.Scrub("at c:/users/julián pérez/Documents/x.txt"));

        // Anyone's, not only this user's, and the shared folders stay.
        var anyone = new PathScrubber();
        Assert.Equal(@"%USERPROFILE%\Desktop and %USERPROFILE%/AppData", anyone.Scrub(@"D:\Users\bob\Desktop and C:/Users/ann/AppData"));
        Assert.Equal(@"C:\Users\Public\Documents", anyone.Scrub(@"C:\Users\Public\Documents"));
        Assert.Equal(@"'%USERPROFILE%' is", anyone.Scrub(@"'C:\Users\bob' is"));
        Assert.Equal(@"%USERPROFILE%\x", anyone.Scrub(@"C:\Documents and Settings\bob\x"));

        // A Linux home under Proton.
        Assert.Equal(@"%USERPROFILE%\.steam and %USERPROFILE%/games", anyone.Scrub(@"Z:\home\deck\.steam and /home/deck/games"));
        Assert.Equal("https://example.com/home/page", anyone.Scrub("https://example.com/home/page"));
    }

    [Fact]
    public void The_profile_folder_goes_wherever_it_is_and_only_whole()
    {
        var scrubber = new PathScrubber(new[] { @"E:\Profiles\ann" });
        Assert.Equal(@"%USERPROFILE%\Saves and E:\Profiles\anna", scrubber.Scrub(@"E:\profiles\ANN\Saves and E:\Profiles\anna"));
        // A file's bytes, with the mark some editors put first.
        byte[] withMark = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes("path E:/Profiles/ann/x")).ToArray();
        Assert.Equal("path %USERPROFILE%/x", Encoding.UTF8.GetString(scrubber.Scrub(withMark)));
    }

    [Fact]
    public void Codes_are_R_and_six_of_the_alphabet()
    {
        Assert.True(ReportCode.IsValid("R-4F2K7Q"));
        Assert.Equal("R-4F2K7Q", ReportCode.Normalize(" r-4f2k7q "));
        Assert.Null(ReportCode.Normalize("R-4F2K7O"));
        Assert.Null(ReportCode.Normalize("R-4F2K7"));
        Assert.Null(ReportCode.Normalize("X-4F2K7Q"));
        Assert.Null(ReportCode.Normalize(null));
    }

    [Fact]
    public void The_headers_are_the_contracts_with_every_value_cleaned()
    {
        var request = new ReportRequest
        {
            Version = "0.2.33+903887bd (dev)",
            Edition = "dev",
            Role = ReportRequest.RoleGuest,
            Players = 9,
            Player = 2,
            Group = "r-4f2k7q",
            Description = "  El auto se hundió\r\nbajo el mapa ¿por qué?\tok  ",
            Contact = "ana#1234\n",
        };
        Assert.True(request.Tick("vehicles"));
        Assert.True(request.Tick("people-jobs"));
        Assert.False(request.Tick("cheese"));
        Assert.False(request.Tick("vehicles"));

        Dictionary<string, string> headers = HeadersOf(request);
        Assert.Equal("0.2.33+903887bddev", headers["X-SS-Version"]);
        Assert.Equal("dev", headers["X-SS-Edition"]);
        Assert.Equal("guest", headers["X-SS-Role"]);
        Assert.Equal("4", headers["X-SS-Players"]);
        Assert.Equal("2", headers["X-SS-Player"]);
        Assert.Equal("R-4F2K7Q", headers["X-SS-Group"]);
        Assert.Equal("vehicles,people-jobs", headers["X-SS-Tags"]);
        Assert.Equal(Uri.EscapeDataString("El auto se hundió\nbajo el mapa ¿por qué? ok"), headers["X-SS-Description"]);
        Assert.Equal("El auto se hundió\nbajo el mapa ¿por qué? ok", Uri.UnescapeDataString(headers["X-SS-Description"]));
        Assert.Equal("ana%231234", headers["X-SS-Contact"]);
        Assert.All(headers.Values, value => Assert.True(value.All(c => c > 0x20 && c < 0x7F), value));
    }

    [Fact]
    public void A_report_alone_says_solo_and_leaves_out_what_is_not_set()
    {
        Dictionary<string, string> headers = HeadersOf(new ReportRequest { Version = "0.2.33+903887bd", Group = "not a code" });
        Assert.Equal("solo", headers["X-SS-Role"]);
        Assert.Equal("1", headers["X-SS-Players"]);
        Assert.Equal("0", headers["X-SS-Player"]);
        Assert.Equal(string.Empty, headers["X-SS-Description"]);
        Assert.Equal("public", headers["X-SS-Edition"]);
        Assert.False(headers.ContainsKey("X-SS-Group"));
        Assert.False(headers.ContainsKey("X-SS-Tags"));
        Assert.False(headers.ContainsKey("X-SS-Contact"));
    }

    [Fact]
    public void Who_the_player_is_follows_the_servers_rules()
    {
        // A host is player 0, whatever it was given.
        Dictionary<string, string> host = HeadersOf(new ReportRequest { Role = ReportRequest.RoleHost, Players = 3, Player = 2, Group = "R-4F2K7Q" });
        Assert.Equal(("host", "3", "0", "R-4F2K7Q"), (host["X-SS-Role"], host["X-SS-Players"], host["X-SS-Player"], host["X-SS-Group"]));

        // Alone: one player, number 0, no group.
        Dictionary<string, string> solo = HeadersOf(new ReportRequest { Role = ReportRequest.RoleSolo, Players = 2, Player = 1, Group = "R-4F2K7Q" });
        Assert.Equal(("solo", "1", "0"), (solo["X-SS-Role"], solo["X-SS-Players"], solo["X-SS-Player"]));
        Assert.False(solo.ContainsKey("X-SS-Group"));

        // A guest is 1 to 3 of two or more; one without a number yet goes alone.
        Dictionary<string, string> guest = HeadersOf(new ReportRequest { Role = ReportRequest.RoleGuest, Players = 1, Player = 3 });
        Assert.Equal(("guest", "2", "3"), (guest["X-SS-Role"], guest["X-SS-Players"], guest["X-SS-Player"]));
        Dictionary<string, string> early = HeadersOf(new ReportRequest { Role = ReportRequest.RoleGuest, Players = 2, Player = 0, Group = "R-4F2K7Q" });
        Assert.Equal(("solo", "1", "0"), (early["X-SS-Role"], early["X-SS-Players"], early["X-SS-Player"]));
        Assert.False(early.ContainsKey("X-SS-Group"));
    }

    [Fact]
    public void The_encoding_is_one_the_server_takes()
    {
        // What Uri.EscapeDataString may write: A-Z a-z 0-9 - . _ ~ ! * ' ( ) and %XX; never a raw space or a lone %.
        string encoded = ReportRequest.PercentEncode("a b%c!*'()~ ñ 😀\n\"<>");
        Assert.Matches("^([A-Za-z0-9._~!*'()-]|%[0-9A-F]{2})*$", encoded);
        Assert.Equal("a b%c!*'()~ ñ 😀\n\"<>", Uri.UnescapeDataString(encoded));
    }

    [Fact]
    public void Texts_are_cut_to_their_length_without_halving_a_character()
    {
        Assert.Equal(ReportRequest.MaxDescription, ReportRequest.CleanDescription(new string('x', 900)).Length);
        string emoji = new string('x', ReportRequest.MaxDescription - 1) + "\U0001F600tail";
        string cut = ReportRequest.CleanDescription(emoji);
        Assert.Equal(ReportRequest.MaxDescription - 1, cut.Length);
        Assert.Equal("a b", ReportRequest.CleanContact("a\nb"));
        Assert.Equal(ReportRequest.MaxContact, ReportRequest.CleanContact(new string('y', 100)).Length);
        Assert.Equal("äöå%", Uri.UnescapeDataString(ReportRequest.PercentEncode("äöå%")));
    }

    [Fact]
    public void The_servers_answers_are_read_as_the_contract_says()
    {
        ReportAnswer sent = ReportAnswer.Read(200, "{\"id\": \"R-4F2K7Q\"}", null);
        Assert.Equal(ReportOutcome.Sent, sent.Outcome);
        Assert.Equal("R-4F2K7Q", sent.Id);

        Assert.Equal(ReportOutcome.ServerError, ReportAnswer.Read(200, "{\"id\": \"nope\"}", null).Outcome);
        Assert.Equal(ReportOutcome.ServerError, ReportAnswer.Read(200, "<html>", null).Outcome);
        Assert.Equal(ReportOutcome.Refused, ReportAnswer.Read(400, "{\"error\": \"bad X-SS-Role\"}", null).Outcome);
        Assert.Equal("bad X-SS-Role", ReportAnswer.Read(400, "{\"error\": \"bad X-SS-Role\"}", null).ServerError);
        Assert.Equal(ReportOutcome.GroupClosed, ReportAnswer.Read(404, "{\"error\": \"unknown group\"}", null).Outcome);
        Assert.Equal(ReportOutcome.TooBig, ReportAnswer.Read(413, null, null).Outcome);
        Assert.Equal(ReportOutcome.ServerError, ReportAnswer.Read(502, "Bad gateway", null).Outcome);

        ReportAnswer many = ReportAnswer.Read(429, "{\"error\": \"too many\"}", " 125 ");
        Assert.Equal(ReportOutcome.TooMany, many.Outcome);
        Assert.Equal(125, many.RetryAfterSeconds);
        Assert.Equal(3, many.RetryAfterMinutes);
        Assert.Equal(1, ReportAnswer.Read(429, null, "Wed, 21 Oct 2015 07:28:00 GMT").RetryAfterMinutes);

        // 503 is the limit for everyone of the day, with the seconds to midnight UTC.
        ReportAnswer full = ReportAnswer.Read(503, "{\"error\":\"daily limit reached\"}", "37800");
        Assert.Equal(ReportOutcome.ServiceFull, full.Outcome);
        Assert.Equal(11, full.RetryAfterHours);
        Assert.Equal("daily limit reached", full.ServerError);
        Assert.Equal(ReportOutcome.NoAnswer, ReportAnswer.None().Outcome);
    }
}
