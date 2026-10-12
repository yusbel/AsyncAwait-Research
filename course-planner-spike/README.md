# Course Planner — research spike

Blazor 10, Interactive Server, .NET 10.

## The hypothesis

AsyncAwait's *course tuning* intelligence feature does not need a hand-built
rules engine. A conversational AI agent (Claude API) can do the job: a member
describes what they want to learn in plain words, and the agent returns a
**structured, machine-readable course schedule as JSON** — weekdays only, one
hour per day, one month, split into theoretical and practical tracks —
grounded in reputable sources.

Sub-hypotheses under test:

1. **Forced tool use produces reliable JSON.** Declaring a `generate_schedule`
   tool with an `input_schema` and forcing it via `tool_choice` yields
   schema-shaped output consistently enough that server-side validation passes
   without retries in the common case.
2. **The key stays server-side.** A Blazor Interactive Server chat page can hold
   the conversation (scoped service, one planner per circuit) while the API key
   never leaves the backend.
3. **RAG grounding plugs in cleanly.** Retrieved sources are injected into the
   prompt as the reputable-source list (stubbed here; the real shared vector
   corpus comes later — see ADR-008 in AsyncAwait-Project-Files).

## How the app was made

- Scaffolded with `dotnet new blazor --interactivity Server` (.NET 10 SDK).
- `Services/AnthropicCoursePlanner.cs` — the core. Builds the system prompt and
  the `generate_schedule` tool definition, POSTs to
  `https://api.anthropic.com/v1/messages`, extracts the `tool_use` block from
  the response, and validates every field before the UI ever sees it: dates
  parse, weekdays only, no duplicates, track is `theoretical`/`practical`,
  resource URLs are absolute http/https. Never trusts the model blindly.
- `Services/IRetrievalService.cs` — the seam for the shared content corpus.
  `StubRetrievalService` returns a fixed set of reputable sources so the loop
  runs end-to-end; replace it with real vector-database retrieval.
- `Models/CourseSchedule.cs` — `CourseSchedule`, `SessionItem`, `ResourceLink`
  records.
- `Components/Pages/Planner.razor` (`/planner`) — chat log, input box, the
  schedule rendered week-by-week as cards with theoretical/practical badges and
  resource links, plus a collapsible view of the **raw JSON the agent returned**.
- Conversation history lives in the scoped planner service, so refinements
  ("make week 3 all practical") re-plan with full context.
- `ANTHROPIC_API_KEY` environment variable (or `Anthropic:ApiKey` in
  appsettings) supplies the key. The model name is configurable
  (`Anthropic:Model`).

## What was verified

- Builds clean with zero warnings/errors; serves `/` and `/planner` (HTTP 200).
- JSON validation logic reviewed field-by-field.
- **Not verified:** the live Claude API round-trip — no API key was available in
  this environment, so the HTTP call path is code-reviewed, not live-tested.

## Cost note

Every generated plan is one paid API call (model + retrieved sources + history
in context). This is exactly why the product prices intelligence per cadence —
monthly free, weekly/daily paid — instead of offering unlimited replans.

## Run

```bash
export ANTHROPIC_API_KEY=sk-ant-...
dotnet run
```

Open `/planner` and try: "bread baking for a beginner", then
"make week 3 all practical".
