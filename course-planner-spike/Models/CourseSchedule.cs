namespace CoursePlanner.Models;

/// <summary>
/// A one-month course schedule: weekday sessions, one hour each.
/// Produced by the planning agent as JSON (via forced tool use) and validated here.
/// </summary>
public sealed record CourseSchedule(
    string Topic,
    DateOnly StartDate,
    List<SessionItem> Sessions);

public sealed record SessionItem(
    DateOnly Date,
    string Track,          // "theoretical" | "practical"
    string Title,
    string Summary,
    List<ResourceLink> Resources);

public sealed record ResourceLink(
    string Title,
    string Url);
