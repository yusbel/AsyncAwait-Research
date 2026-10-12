using CoursePlanner.Components;
using CoursePlanner.Services;

var builder = WebApplication.CreateBuilder(args);

// Research spike: support ANTHROPIC_API_KEY env var as well as appsettings.
var envKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
if (!string.IsNullOrWhiteSpace(envKey))
    builder.Configuration["Anthropic:ApiKey"] = envKey;

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Chat -> service -> Claude API. Scoped = one planner (and its conversation
// history) per Blazor Server circuit.
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IRetrievalService, StubRetrievalService>();
builder.Services.AddScoped<AnthropicCoursePlanner>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
