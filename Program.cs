using PanoProxy;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.MapGet("/recorder/state", async (Guid remoteRecorderId, IConfiguration config) =>
    {
        var hostname = config["Panopto:Hostname"];
        var username = config["Panopto:Username"];
        var password = config["Panopto:Password"];

        if (string.IsNullOrEmpty(hostname) || string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            return Results.BadRequest(new { error = "Panopto configuration is missing or incomplete" });
        }

        using var client = new PanoptoApiClient(hostname);

        var loginSuccess = await client.LoginAsync(username, password);
        if (!loginSuccess)
        {
            return Results.Unauthorized();
        }

        var state = await client.GetRemoteRecorderStateAsync(remoteRecorderId);

        return Results.Ok(new { remoteRecorderId, state });
    })
    .WithName("GetRemoteRecorderState")
    .AddEndpointFilter<BasicAuthFilter>();

app.MapGet("/recorder/sessions", async (Guid remoteRecorderId, DateTime startDate, DateTime endDate, IConfiguration config) =>
    {
        var hostname = config["Panopto:Hostname"];
        var username = config["Panopto:Username"];
        var password = config["Panopto:Password"];

        if (string.IsNullOrEmpty(hostname) || string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            return Results.BadRequest(new { error = "Panopto configuration is missing or incomplete" });
        }

        using var client = new PanoptoApiClient(hostname);

        var loginSuccess = await client.LoginAsync(username, password);
        if (!loginSuccess)
        {
            return Results.Unauthorized();
        }

        var sessions = await client.GetSessionsListAsync(remoteRecorderId, startDate, endDate);

        return Results.Ok(new { remoteRecorderId, startDate, endDate, sessionCount = sessions.Count, sessions });
    })
    .WithName("GetSessionsList")
    .AddEndpointFilter<BasicAuthFilter>();

app.MapPost("/session/update-time", async (Guid sessionId, DateTime newStartTime, DateTime newEndTime, IConfiguration config) =>
    {
        var hostname = config["Panopto:Hostname"];
        var username = config["Panopto:Username"];
        var password = config["Panopto:Password"];

        if (string.IsNullOrEmpty(hostname) || string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            return Results.BadRequest(new { error = "Panopto configuration is missing or incomplete" });
        }

        using var client = new PanoptoApiClient(hostname);

        var loginSuccess = await client.LoginAsync(username, password);
        if (!loginSuccess)
        {
            return Results.Unauthorized();
        }

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

app.MapPost("/session/start", async (Guid sessionId, IConfiguration config) =>
    {
        var hostname = config["Panopto:Hostname"];
        var username = config["Panopto:Username"];
        var password = config["Panopto:Password"];

        if (string.IsNullOrEmpty(hostname) || string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            return Results.BadRequest(new { error = "Panopto configuration is missing or incomplete" });
        }

        using var client = new PanoptoApiClient(hostname);

        var loginSuccess = await client.LoginAsync(username, password);
        if (!loginSuccess)
        {
            return Results.Unauthorized();
        }

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

app.MapPost("/session/pause", async (Guid sessionId, IConfiguration config) =>
    {
        var hostname = config["Panopto:Hostname"];
        var username = config["Panopto:Username"];
        var password = config["Panopto:Password"];

        if (string.IsNullOrEmpty(hostname) || string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            return Results.BadRequest(new { error = "Panopto configuration is missing or incomplete" });
        }

        using var client = new PanoptoApiClient(hostname);

        var loginSuccess = await client.LoginAsync(username, password);
        if (!loginSuccess)
        {
            return Results.Unauthorized();
        }

        var pauseId = await client.PauseSessionAsync(sessionId);

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

app.MapPost("/session/resume", async (Guid sessionId, Guid pauseId, DateTime pauseStartTime, IConfiguration config) =>
    {
        var hostname = config["Panopto:Hostname"];
        var username = config["Panopto:Username"];
        var password = config["Panopto:Password"];

        if (string.IsNullOrEmpty(hostname) || string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            return Results.BadRequest(new { error = "Panopto configuration is missing or incomplete" });
        }

        using var client = new PanoptoApiClient(hostname);

        var loginSuccess = await client.LoginAsync(username, password);
        if (!loginSuccess)
        {
            return Results.Unauthorized();
        }

        var durationSeconds = (int)Math.Max(1, (DateTime.UtcNow - pauseStartTime).TotalSeconds);

        var success = await client.UpdatePauseDurationAsync(sessionId, pauseId, durationSeconds);

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

app.MapPost("/session/stop", async (Guid sessionId, IConfiguration config) =>
    {
        var hostname = config["Panopto:Hostname"];
        var username = config["Panopto:Username"];
        var password = config["Panopto:Password"];

        if (string.IsNullOrEmpty(hostname) || string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            return Results.BadRequest(new { error = "Panopto configuration is missing or incomplete" });
        }

        using var client = new PanoptoApiClient(hostname);

        var loginSuccess = await client.LoginAsync(username, password);
        if (!loginSuccess)
        {
            return Results.Unauthorized();
        }

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

app.Run();
