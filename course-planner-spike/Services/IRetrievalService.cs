namespace CoursePlanner.Services;

/// <summary>
/// Retrieval over the shared content corpus (ADR-008).
/// RESEARCH STUB: returns a fixed set of reputable sources so the
/// planner wiring can be exercised end-to-end. Replace with the real
/// vector-database retrieval (the Rag infrastructure) in production.
/// </summary>
public interface IRetrievalService
{
    Task<IReadOnlyList<RetrievedSource>> RetrieveAsync(string topic, CancellationToken ct = default);
}

public sealed record RetrievedSource(string Title, string Url, string Snippet);

public sealed class StubRetrievalService : IRetrievalService
{
    public Task<IReadOnlyList<RetrievedSource>> RetrieveAsync(string topic, CancellationToken ct = default)
    {
        // Stub: in production this queries the shared vector database.
        IReadOnlyList<RetrievedSource> sources = new List<RetrievedSource>
        {
            new("Khan Academy — course catalog", "https://www.khanacademy.org", "Free courses across math, science, computing and humanities."),
            new("MIT OpenCourseWare", "https://ocw.mit.edu", "Lecture notes, assignments and videos from MIT courses."),
            new("Coursera — course catalog", "https://www.coursera.org", "University courses with audit and certificate tracks."),
            new("MDN Web Docs", "https://developer.mozilla.org", "Reference documentation for web technologies."),
        };
        return Task.FromResult(sources);
    }
}
