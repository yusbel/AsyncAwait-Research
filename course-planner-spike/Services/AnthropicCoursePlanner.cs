using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CoursePlanner.Models;

namespace CoursePlanner.Services;

/// <summary>
/// Plans a one-month course schedule through the Claude API.
/// The agent is FORCED to answer with the <c>generate_schedule</c> tool
/// (tool_choice), so the response arrives as schema-shaped JSON.
/// The JSON is then validated field-by-field — never trusted blindly.
/// </summary>
public sealed class AnthropicCoursePlanner
{
    private const string Endpoint = "https://api.anthropic.com/v1/messages";
    private const string ApiVersion = "2023-06-01";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;
    private readonly IRetrievalService _retrieval;
    private readonly string _apiKey;
    private readonly string _model;

    // Conversation history for this circuit (Blazor Server scoped lifetime).
    private readonly List<JsonElement> _history = new();

    public AnthropicCoursePlanner(
        HttpClient http,
        IRetrievalService retrieval,
        IConfiguration config)
    {
        _http = http;
        _retrieval = retrieval;
        _apiKey = config["Anthropic:ApiKey"] ?? "";
        _model = config["Anthropic:Model"] ?? "claude-sonnet-4-5-20250929";
    }

    public IReadOnlyList<ChatTurn> History =>
        _history.Select(e => new ChatTurn(
            e.GetProperty("role").GetString() ?? "?",
            e.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() ?? ""
                : "[schedule generated]")).ToList();

    /// <summary>
    /// Sends the user's message (topic or refinement) and returns a validated schedule.
    /// </summary>
    public async Task<PlanResult> PlanAsync(string userMessage, CancellationToken ct = new())
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            return PlanResult.Failure(
                "Anthropic:ApiKey is not configured. Set it in appsettings.json, " +
                "user secrets, or the ANTHROPIC_API_KEY environment variable.");

        var sources = await _retrieval.RetrieveAsync(userMessage, ct);
        var sourceBlock = string.Join("\n", sources.Select((s, i) =>
            $"{i + 1}. {s.Title} — {s.Url}\n   {s.Snippet}"));

        var system = """
            You are a course planner for AsyncAwait, an app that helps adults build learning rhythms.
            Rules you always follow:
            - Split every plan into THEORETICAL and PRACTICAL tracks. Theory first (readings, concepts), then hands-on practice.
            - Schedule ONLY on weekdays (Monday–Friday), one hour per session, for exactly one month starting on the given start date.
            - Use ONLY the reputable sources provided below for readings and courses. Never invent URLs.
            - Privacy: never mention other members or what others do. The plan is for this member alone.
            - You MUST answer by calling the generate_schedule tool. Never answer in plain text.
            """;

        var userText =
            $"Start date (first Monday on or after today): {NextMonday():yyyy-MM-dd}\n" +
            $"Member request: {userMessage}\n\n" +
            $"Reputable sources you may use:\n{sourceBlock}";

        var request = new
        {
            model = _model,
            max_tokens = 4096,
            system,
            messages = BuildMessages(userText),
            tools = new[] { ScheduleTool() },
            tool_choice = new { type = "tool", name = "generate_schedule" }
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        httpRequest.Headers.Add("x-api-key", _apiKey);
        httpRequest.Headers.Add("anthropic-version", ApiVersion);
        httpRequest.Content = new StringContent(
            JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(httpRequest, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            return PlanResult.Failure($"Claude API error {(int)response.StatusCode}: {Truncate(body, 300)}");

        try
        {
            using var doc = JsonDocument.Parse(body);
            var toolInput = ExtractToolInput(doc);
            if (toolInput is null)
                return PlanResult.Failure("The agent did not return a schedule (no generate_schedule tool call).");

            var schedule = ValidateSchedule(toolInput.Value);
            AppendHistory(userText, schedule);
            return PlanResult.Success(schedule);
        }
        catch (JsonException ex)
        {
            return PlanResult.Failure($"Invalid JSON from agent: {ex.Message}");
        }
        catch (ScheduleValidationException ex)
        {
            return PlanResult.Failure($"Schedule failed validation: {ex.Message}");
        }
    }

    // ---------- request building ----------

    private static DateOnly NextMonday()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var add = ((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7;
        return today.AddDays(add == 0 ? 7 : add); // next Monday, not today
    }

    private object[] BuildMessages(string latestUserText)
    {
        var msgs = new List<object>();
        foreach (var h in _history)
        {
            msgs.Add(JsonSerializer.Deserialize<object>(h.GetRawText(), JsonOptions)!);
        }
        msgs.Add(new { role = "user", content = latestUserText });
        return msgs.ToArray();
    }

    private static object ScheduleTool() => new
    {
        name = "generate_schedule",
        description = "Generate a one-month course schedule: weekdays only, one hour per session, split into theoretical and practical tracks.",
        input_schema = new
        {
            type = "object",
            properties = new
            {
                topic = new { type = "string", description = "The learning topic." },
                sessions = new
                {
                    type = "array",
                    description = "One entry per weekday session, in chronological order.",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            date = new { type = "string", description = "Session date, ISO format YYYY-MM-DD. Must be a weekday." },
                            track = new { type = "string", @enum = new[] { "theoretical", "practical" } },
                            title = new { type = "string" },
                            summary = new { type = "string", description = "One or two sentences on what happens in this hour." },
                            resources = new
                            {
                                type = "array",
                                items = new
                                {
                                    type = "object",
                                    properties = new
                                    {
                                        title = new { type = "string" },
                                        url = new { type = "string", description = "Must be one of the provided reputable sources." }
                                    },
                                    required = new[] { "title", "url" }
                                }
                            }
                        },
                        required = new[] { "date", "track", "title", "summary", "resources" }
                    }
                }
            },
            required = new[] { "topic", "sessions" }
        }
    };

    // ---------- response parsing & validation ----------

    private static JsonElement? ExtractToolInput(JsonDocument doc)
    {
        foreach (var block in doc.RootElement.GetProperty("content").EnumerateArray())
        {
            if (block.GetProperty("type").GetString() == "tool_use"
                && block.GetProperty("name").GetString() == "generate_schedule")
            {
                return block.GetProperty("input");
            }
        }
        return null;
    }

    private static CourseSchedule ValidateSchedule(JsonElement input)
    {
        var topic = input.GetProperty("topic").GetString()
            ?? throw new ScheduleValidationException("missing topic");

        var sessions = new List<SessionItem>();
        var seen = new HashSet<DateOnly>();
        foreach (var s in input.GetProperty("sessions").EnumerateArray())
        {
            var dateStr = s.GetProperty("date").GetString()
                ?? throw new ScheduleValidationException("session missing date");
            if (!DateOnly.TryParse(dateStr, out var date))
                throw new ScheduleValidationException($"bad date '{dateStr}'");
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                throw new ScheduleValidationException($"'{dateStr}' is a weekend — weekdays only");
            if (!seen.Add(date))
                throw new ScheduleValidationException($"duplicate date '{dateStr}'");

            var track = s.GetProperty("track").GetString()
                ?? throw new ScheduleValidationException("session missing track");
            if (track is not ("theoretical" or "practical"))
                throw new ScheduleValidationException($"bad track '{track}'");

            var title = s.GetProperty("title").GetString()?.Trim()
                ?? throw new ScheduleValidationException("session missing title");
            var summary = s.GetProperty("summary").GetString()?.Trim() ?? "";

            var resources = new List<ResourceLink>();
            foreach (var r in s.GetProperty("resources").EnumerateArray())
            {
                var rt = r.GetProperty("title").GetString()?.Trim() ?? "Untitled";
                var url = r.GetProperty("url").GetString()?.Trim() ?? "";
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                    || uri.Scheme is not ("http" or "https"))
                    throw new ScheduleValidationException($"bad resource URL '{url}'");
                resources.Add(new ResourceLink(rt, url));
            }

            sessions.Add(new SessionItem(date, track, title, summary, resources));
        }

        if (sessions.Count == 0)
            throw new ScheduleValidationException("no sessions returned");
        sessions.Sort((a, b) => a.Date.CompareTo(b.Date));
        return new CourseSchedule(topic, sessions[0].Date, sessions);
    }

    private void AppendHistory(string userText, CourseSchedule schedule)
    {
        _history.Add(JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            role = "user",
            content = userText
        })).RootElement.Clone());
        _history.Add(JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            role = "assistant",
            content = $"[Generated a {schedule.Sessions.Count}-session schedule on '{schedule.Topic}', "
                      + string.Join("; ", schedule.Sessions.Select(s => $"{s.Date:MMM d} ({s.Track}): {s.Title}"))
        })).RootElement.Clone());
    }

    private static string Truncate(string s, int n) =>
        s.Length <= n ? s : s[..n] + "…";
}

public sealed record ChatTurn(string Role, string Text);

public sealed class PlanResult
{
    public bool Ok { get; }
    public CourseSchedule? Schedule { get; }
    public string? Error { get; }

    private PlanResult(bool ok, CourseSchedule? schedule, string? error)
        => (Ok, Schedule, Error) = (ok, schedule, error);

    public static PlanResult Success(CourseSchedule s) => new(true, s, null);
    public static PlanResult Failure(string e) => new(false, null, e);
}

public sealed class ScheduleValidationException(string message) : Exception(message);
