using PanoProxy;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// Register PanoptoApiClient as a singleton
builder.Services.AddSingleton(serviceProvider =>
{
    var config = serviceProvider.GetRequiredService<IConfiguration>();
    var hostname = config["Panopto:Hostname"];
    var username = config["Panopto:Username"];
    var password = config["Panopto:Password"];

    if (string.IsNullOrEmpty(hostname) || string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
    {
        throw new InvalidOperationException("Panopto configuration is missing or incomplete. Please configure Panopto:Hostname, Panopto:Username, and Panopto:Password.");
    }

    return new PanoptoApiClient(hostname, username, password);
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.MapGet("/recorder/state", async (Guid remoteRecorderId, PanoptoApiClient client) =>
    {
        var state = await client.GetRemoteRecorderStateAsync(remoteRecorderId);

        return Results.Ok(new { remoteRecorderId, state });
    })
    .WithName("GetRemoteRecorderState")
    .AddEndpointFilter<BasicAuthFilter>();

app.MapGet("/recorder/sessions", async (Guid remoteRecorderId, PanoptoApiClient client) =>
    {
        var allSessions = await client.GetSessionsListAsync(remoteRecorderId);

        // Filter to only include sessions where startDate = today
        var today = DateTime.UtcNow.Date;
        var sessions = allSessions
            .Where(s => s.StartTime.HasValue && s.StartTime.Value.Date == today)
            .ToList();

        return Results.Ok(new { remoteRecorderId, sessionCount = sessions.Count, sessions });
    })
    .WithName("GetSessionsList")
    .AddEndpointFilter<BasicAuthFilter>();

app.MapPost("/session/update-time", async (Guid sessionId, DateTime newStartTime, DateTime newEndTime, PanoptoApiClient client) =>
    {
        var success = await client.UpdateSessionTimeAsync(sessionId, newStartTime, newEndTime);

        if (success)
        {
            return Results.Ok(new { sessionId, newStartTime, newEndTime, success = true, message = "Session time updated successfully" });
        }
        else
        {
            return Results.BadRequest(new { sessionId, success = false, message = "Failed to update session time" });
        }
    })
    .WithName("UpdateSessionTime")
    .AddEndpointFilter<BasicAuthFilter>();

app.MapPost("/session/start", async (Guid sessionId, PanoptoApiClient client) =>
    {
        var sessions = await client.GetSessionDetailsAsync(new[] { sessionId });
        if (sessions == null || sessions.Count == 0)
        {
            return Results.NotFound(new { sessionId, success = false, message = "Session not found" });
        }

        var session = sessions[0];
        var newStartTime = DateTime.UtcNow;
        var newEndTime = newStartTime.AddSeconds(session.Duration.GetValueOrDefault(3600)); // Default 1hr if duration missing

        var success = await client.UpdateSessionTimeAsync(sessionId, newStartTime, newEndTime);

        if (success)
        {
            return Results.Ok(new { sessionId, originalStartTime = session.StartTime, newStartTime, endTime = newEndTime, success = true, message = "Session started successfully" });
        }
        else
        {
            return Results.BadRequest(new { sessionId, success = false, message = "Failed to start session" });
        }
    })
    .WithName("StartSession")
    .AddEndpointFilter<BasicAuthFilter>();

app.MapPost("/session/pause", async (Guid sessionId, PanoptoApiClient client) =>
    {
        var internalSessionId = await client.GetInternalSessionIdAsync(sessionId);

        if (!internalSessionId.HasValue)
        {
            return Results.BadRequest(new { sessionId, success = false, message = "Failed to pause session" });
        }

        var pauseId = await client.PauseSessionAsync(internalSessionId.Value);

        if (pauseId.HasValue)
        {
            return Results.Ok(new { sessionId, pauseId = pauseId.Value, success = true, message = "Session paused successfully" });
        }
        else
        {
            return Results.BadRequest(new { sessionId, success = false, message = "Failed to pause session" });
        }
    })
    .WithName("PauseSession")
    .AddEndpointFilter<BasicAuthFilter>();

app.MapPost("/session/resume", async (Guid sessionId, Guid pauseId, DateTime pauseStartTime, PanoptoApiClient client) =>
    {
        var internalSessionId = await client.GetInternalSessionIdAsync(sessionId);

        if (!internalSessionId.HasValue)
        {
            return Results.BadRequest(new { sessionId, success = false, message = "Failed to resume session" });
        }

        var durationSeconds = (int)Math.Max(1, (DateTime.UtcNow - pauseStartTime).TotalSeconds);

        var success = await client.UpdatePauseDurationAsync(internalSessionId.Value, pauseId, durationSeconds);

        if (success)
        {
            return Results.Ok(new { sessionId, pauseId, pauseStartTime, resumeTime = DateTime.UtcNow, durationSeconds, success = true, message = "Session resumed successfully" });
        }
        else
        {
            return Results.BadRequest(new { sessionId, pauseId, success = false, message = "Failed to resume session" });
        }
    })
    .WithName("ResumeSession")
    .AddEndpointFilter<BasicAuthFilter>();

app.MapPost("/session/stop", async (Guid sessionId, PanoptoApiClient client) =>
    {
        var sessions = await client.GetSessionDetailsAsync(new[] { sessionId });
        if (sessions == null || sessions.Count == 0)
        {
            return Results.NotFound(new { sessionId, success = false, message = "Session not found" });
        }

        var session = sessions[0];
        if (!session.StartTime.HasValue)
        {
            return Results.BadRequest(new { sessionId, success = false, message = "Session start time is not available" });
        }

        var currentStartTime = session.StartTime.Value;
        var newEndTime = DateTime.UtcNow;

        var success = await client.UpdateSessionTimeAsync(sessionId, currentStartTime, newEndTime);

        if (success)
        {
            return Results.Ok(new { sessionId, originalStartTime = currentStartTime, newEndTime, success = true, message = "Session stopped successfully" });
        }
        else
        {
            return Results.BadRequest(new { sessionId, success = false, message = "Failed to stop session" });
        }
    })
    .WithName("StopSession")
    .AddEndpointFilter<BasicAuthFilter>();

app.MapPost("/session/create", async (Guid remoteRecorderId, string sessionName, DateTime startTime, TimeSpan duration, PanoptoApiClient client, IConfiguration config) =>
    {
        var folderIdString = config["Panopto:DefaultFolder"];
        if (string.IsNullOrEmpty(folderIdString) || !Guid.TryParse(folderIdString, out Guid folderId))
        {
            return Results.BadRequest(new { success = false, message = "Panopto:DefaultFolder is not configured or invalid in appsettings" });
        }

        var sessionId = await client.CreateRecordingAsync(remoteRecorderId, sessionName, startTime, duration, folderId);

        if (sessionId.HasValue)
        {
            return Results.Ok(new { sessionId = sessionId.Value, remoteRecorderId, sessionName, startTime, endTime = startTime.Add(duration), folderId, success = true, message = "Recording created successfully" });
        }
        else
        {
            return Results.BadRequest(new { remoteRecorderId, sessionName, success = false, message = "Failed to create recording" });
        }
    })
    .WithName("CreateRecording")
    .AddEndpointFilter<BasicAuthFilter>();

app.Run();
