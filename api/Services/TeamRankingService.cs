using System.Net.Http.Headers;

namespace Arcl.Api.Services;

public sealed record TeamRankingResult(string TeamName, string Season, string Division, string? Rank, string? RankDescription);

/// <summary>
/// Live team standings lookup — NOT part of the ingested rule_chunks corpus. arcl.org has no
/// static page listing standings (they're only ever shown as live, per-division pages,
/// Pages/UI/DivHome.aspx?league_id=...), but Pages/UI/TeamHistory.aspx?team_name=X is public,
/// needs no login, keyed by team name, and its last "Team History" row is the team's current,
/// in-progress standing for the active season — confirmed directly (e.g. "Avengers" showed
/// "Summer 2026 | Div C | 9" while that season was still underway). So this fetches and parses
/// that page live, per request, rather than ingesting anything ahead of time — the data is
/// exactly as stale as the page itself, by design.
/// </summary>
public sealed class TeamRankingService
{
    private readonly HttpClient _http;

    public TeamRankingService(HttpClient http)
    {
        _http = http;
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ArclChatbot", "1.0"));
    }

    /// <summary>Returns null if arcl.org has no team by that exact name (its own page always
    /// returns 200 OK either way — a non-match just renders a Team History table with zero rows,
    /// see the note on FindDataRows below).</summary>
    public async Task<TeamRankingResult?> LookupAsync(string teamName, CancellationToken ct = default)
    {
        var url = $"https://arcl.org/Pages/UI/TeamHistory.aspx?team_name={Uri.EscapeDataString(teamName)}";
        var html = await _http.GetStringAsync(url, ct);

        var doc = new HtmlAgilityPack.HtmlDocument();
        doc.LoadHtml(html);

        // Stable ASP.NET control ID — much less fragile than matching on heading text (see the
        // ingest tool's anchor-matching approach for rulebook pages, which this deliberately
        // avoids: that approach exists because Word-exported prose has no real markup to hook
        // into; this page is a normal ASP.NET GridView with a real, stable id).
        var table = doc.GetElementbyId("ctl00_ContentPlaceHolder1_gvTeamHistory");
        var rows = table?.SelectNodes(".//tr");
        if (rows is null || rows.Count <= 1) // row 0 is the header; no data rows means no such team
            return null;

        // Last data row = most recent season = current standing (rows run oldest-to-newest,
        // confirmed against the live page).
        var cells = rows[^1].SelectNodes("./td");
        if (cells is null || cells.Count < 4)
            return null;

        var season = CellText(cells[0]);
        var division = CellText(cells[1]);
        var rank = CellText(cells[2]);
        var rankDescription = CellText(cells[3]);

        return new TeamRankingResult(
            teamName, season, division,
            string.IsNullOrWhiteSpace(rank) ? null : rank,
            string.IsNullOrWhiteSpace(rankDescription) ? null : rankDescription);
    }

    // Cells with no real value contain a literal "&nbsp;" rather than being empty.
    private static string CellText(HtmlAgilityPack.HtmlNode cell) =>
        HtmlAgilityPack.HtmlEntity.DeEntitize(cell.InnerText).Trim();
}
