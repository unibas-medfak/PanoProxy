using PanoProxy;
using Serilog;

var Recorders = new[]
{
    new { name = "Recorder 1", id = Guid.Parse("11111111-1111-1111-1111-111111111111") },
    new { name = "Recorder 2", id = Guid.Parse("22222222-2222-2222-2222-222222222222") },
    new { name = "Recorder 3", id = Guid.Parse("33333333-3333-3333-3333-333333333333") }
};

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console()
    .WriteTo.File("/app/logs/panoproxy-.log", rollingInterval: RollingInterval.Day)
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();

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
        Log.Debug("Getting remote recorder state for {RemoteRecorderId}", remoteRecorderId);
        var state = await client.GetRemoteRecorderStateAsync(remoteRecorderId);

        return Results.Ok(new { remoteRecorderId, state });
    })
    .WithName("GetRemoteRecorderState")
    .AddEndpointFilter<BasicAuthFilter>();

app.MapGet("/recorder/sessions", async (Guid remoteRecorderId, PanoptoApiClient client) =>
    {
        Log.Debug("Getting sessions list for recorder {RemoteRecorderId}", remoteRecorderId);
        var allSessions = await client.GetSessionsListAsync(remoteRecorderId);

        // Filter to only include sessions where startDate = today
        var today = DateTime.UtcNow.Date;
        var sessions = allSessions
            .Where(s => s.StartTime.HasValue && s.StartTime.Value.Date == today)
            .ToList();

        Log.Information("Found {SessionCount} sessions for today for recorder {RemoteRecorderId}", sessions.Count, remoteRecorderId);
        return Results.Ok(new { remoteRecorderId, sessionCount = sessions.Count, sessions });
    })
    .WithName("GetSessionsList")
    .AddEndpointFilter<BasicAuthFilter>();

app.MapGet("/sessions", async (PanoptoApiClient client) =>
    {
        Log.Debug("Getting sessions list for all recorders");
        var today = DateTime.UtcNow.Date;

        var tasks = Recorders.Select(async recorder =>
        {
            var allSessions = await client.GetSessionsListAsync(recorder.id);
            var sessions = allSessions
                .Where(s => s.StartTime.HasValue && s.StartTime.Value.Date == today)
                .ToList();
            return new { recorder.id, recorder.name, sessionCount = sessions.Count, sessions };
        });

        var recorders = await Task.WhenAll(tasks);
        var totalCount = recorders.Sum(r => r.sessionCount);

        Log.Information("Found {SessionCount} sessions for today across all recorders", totalCount);
        return Results.Ok(new { totalSessionCount = totalCount, recorders });
    })
    .WithName("GetAllSessions")
    .AddEndpointFilter<BasicAuthFilter>();

app.MapPost("/session/update-time", async (Guid sessionId, DateTime newStartTime, DateTime newEndTime, PanoptoApiClient client) =>
    {
        Log.Information("Updating session time for {SessionId}: {NewStartTime} to {NewEndTime}", sessionId, newStartTime, newEndTime);
        var success = await client.UpdateSessionTimeAsync(sessionId, newStartTime, newEndTime);

        if (success)
        {
            Log.Information("Successfully updated session time for {SessionId}", sessionId);
            return Results.Ok(new { sessionId, newStartTime, newEndTime, success = true, message = "Session time updated successfully" });
        }

        Log.Warning("Failed to update session time for {SessionId}", sessionId);
        return Results.BadRequest(new { sessionId, success = false, message = "Failed to update session time" });
    })
    .WithName("UpdateSessionTime")
    .AddEndpointFilter<BasicAuthFilter>();

app.MapPost("/session/start", async (Guid sessionId, PanoptoApiClient client) =>
    {
        Log.Information("Starting session {SessionId}", sessionId);
        var sessions = await client.GetSessionDetailsAsync([sessionId]);
        if (sessions == null || sessions.Count == 0)
        {
            Log.Warning("Session {SessionId} not found", sessionId);
            return Results.NotFound(new { sessionId, success = false, message = "Session not found" });
        }

        var session = sessions[0];
        var newStartTime = DateTime.UtcNow;

        // TODO: do we keep duration or endTime?
        var newEndTime = session.StartTime.Value.AddSeconds(session.Duration.GetValueOrDefault(3600));
        //var newEndTime = newStartTime.AddSeconds(session.Duration.GetValueOrDefault(3600)); // Default 1hr if duration missing

        var success = await client.UpdateSessionTimeAsync(sessionId, newStartTime, newEndTime);

        if (success)
        {
            Log.Information("Successfully started session {SessionId} at {NewStartTime}", sessionId, newStartTime);
            return Results.Ok(new { sessionId, originalStartTime = session.StartTime, newStartTime, endTime = newEndTime, success = true, message = "Session started successfully" });
        }

        Log.Warning("Failed to start session {SessionId}", sessionId);
        return Results.BadRequest(new { sessionId, success = false, message = "Failed to start session" });
    })
    .WithName("StartSession")
    .AddEndpointFilter<BasicAuthFilter>();

app.MapPost("/session/pause", async (Guid sessionId, PanoptoApiClient client) =>
    {
        Log.Information("Pausing session {SessionId}", sessionId);
        var internalSessionId = await client.GetInternalSessionIdAsync(sessionId);

        if (!internalSessionId.HasValue)
        {
            Log.Warning("Failed to get internal session ID for {SessionId}", sessionId);
            return Results.BadRequest(new { sessionId, success = false, message = "Failed to pause session" });
        }

        var pauseId = await client.PauseSessionAsync(internalSessionId.Value);

        if (pauseId.HasValue)
        {
            Log.Information("Successfully paused session {SessionId} with pause ID {PauseId}", sessionId, pauseId.Value);
            return Results.Ok(new { sessionId, pauseId = pauseId.Value, success = true, message = "Session paused successfully" });
        }

        Log.Warning("Failed to pause session {SessionId}", sessionId);
        return Results.BadRequest(new { sessionId, success = false, message = "Failed to pause session" });
    })
    .WithName("PauseSession")
    .AddEndpointFilter<BasicAuthFilter>();

app.MapPost("/session/resume", async (Guid sessionId, Guid pauseId, DateTime pauseStartTime, PanoptoApiClient client) =>
    {
        Log.Information("Resuming session {SessionId} from pause {PauseId}", sessionId, pauseId);
        var internalSessionId = await client.GetInternalSessionIdAsync(sessionId);

        if (!internalSessionId.HasValue)
        {
            Log.Warning("Failed to get internal session ID for {SessionId}", sessionId);
            return Results.BadRequest(new { sessionId, success = false, message = "Failed to resume session" });
        }

        var durationSeconds = (int)Math.Max(1, (DateTime.UtcNow - pauseStartTime).TotalSeconds);

        var success = await client.UpdatePauseDurationAsync(internalSessionId.Value, pauseId, durationSeconds);

        if (success)
        {
            Log.Information("Successfully resumed session {SessionId} after {DurationSeconds}s pause", sessionId, durationSeconds);
            return Results.Ok(new { sessionId, pauseId, pauseStartTime, resumeTime = DateTime.UtcNow, durationSeconds, success = true, message = "Session resumed successfully" });
        }

        Log.Warning("Failed to resume session {SessionId}", sessionId);
        return Results.BadRequest(new { sessionId, pauseId, success = false, message = "Failed to resume session" });
    })
    .WithName("ResumeSession")
    .AddEndpointFilter<BasicAuthFilter>();

app.MapPost("/session/stop", async (Guid sessionId, PanoptoApiClient client) =>
    {
        Log.Information("Stopping session {SessionId}", sessionId);
        var sessions = await client.GetSessionDetailsAsync([sessionId]);
        if (sessions == null || sessions.Count == 0)
        {
            Log.Warning("Session {SessionId} not found", sessionId);
            return Results.NotFound(new { sessionId, success = false, message = "Session not found" });
        }

        var session = sessions[0];
        if (!session.StartTime.HasValue)
        {
            Log.Warning("Session {SessionId} has no start time", sessionId);
            return Results.BadRequest(new { sessionId, success = false, message = "Session start time is not available" });
        }

        var currentStartTime = session.StartTime.Value;
        var newEndTime = DateTime.UtcNow;

        var success = await client.UpdateSessionTimeAsync(sessionId, currentStartTime, newEndTime);

        if (success)
        {
            Log.Information("Successfully stopped session {SessionId} at {NewEndTime}", sessionId, newEndTime);
            return Results.Ok(new { sessionId, originalStartTime = currentStartTime, newEndTime, success = true, message = "Session stopped successfully" });
        }

        Log.Warning("Failed to stop session {SessionId}", sessionId);
        return Results.BadRequest(new { sessionId, success = false, message = "Failed to stop session" });
    })
    .WithName("StopSession")
    .AddEndpointFilter<BasicAuthFilter>();

app.MapGet("/recorders", () => Results.Ok(Recorders))
    .WithName("GetRecorders")
    .AddEndpointFilter<BasicAuthFilter>();

app.MapPost("/session/create", async (Guid remoteRecorderId, string sessionName, DateTime startTime, TimeSpan duration, PanoptoApiClient client, IConfiguration config) =>
    {
        Log.Information("Creating session {SessionName} for recorder {RemoteRecorderId} at {StartTime}", sessionName, remoteRecorderId, startTime);
        var folderIdString = config["Panopto:DefaultFolder"];
        if (string.IsNullOrEmpty(folderIdString) || !Guid.TryParse(folderIdString, out var folderId))
        {
            Log.Warning("Panopto:DefaultFolder is not configured or invalid");
            return Results.BadRequest(new { success = false, message = "Panopto:DefaultFolder is not configured or invalid in appsettings" });
        }

        var sessionId = await client.CreateRecordingAsync(remoteRecorderId, sessionName, startTime, duration, folderId);

        if (sessionId.HasValue)
        {
            Log.Information("Successfully created session {SessionId} for recorder {RemoteRecorderId}", sessionId.Value, remoteRecorderId);
            return Results.Ok(new { sessionId = sessionId.Value, remoteRecorderId, sessionName, startTime, endTime = startTime.Add(duration), folderId, success = true, message = "Recording created successfully" });
        }

        Log.Warning("Failed to create session {SessionName} for recorder {RemoteRecorderId}", sessionName, remoteRecorderId);
        return Results.BadRequest(new { remoteRecorderId, sessionName, success = false, message = "Failed to create recording" });
    })
    .WithName("CreateRecording")
    .AddEndpointFilter<BasicAuthFilter>();

app.Run();
