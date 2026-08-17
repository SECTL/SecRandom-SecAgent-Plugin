using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SecRandom.Core.Abstraction.Services;
using SecRandom.PluginSdk;
using SecRandom.Shared.Models.Profile;

namespace SecRandom.SecAgentPlugin;

/// <summary>
/// Loopback-only REST endpoint consumed by the SecRandom SecAgent connector.
/// The host provides the draw service; this plugin owns the SecAgent transport.
/// </summary>
public sealed class SecAgentHttpHostedService(
    ILogger<SecAgentHttpHostedService> logger,
    IProfileService profileService,
    IExternalStudentDrawService externalStudentDrawService) : BackgroundService
{
    private const string Prefix = "http://127.0.0.1:3910/api/secagent/v1/";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpListener _listener = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _listener.Prefixes.Add(Prefix);
        try
        {
            _listener.Start();
            logger.LogInformation("SecAgent loopback REST endpoint started at {Prefix}.", Prefix[..^1]);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start SecAgent loopback REST endpoint at {Prefix}.", Prefix);
            return;
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var context = await _listener.GetContextAsync().WaitAsync(stoppingToken).ConfigureAwait(false);
                _ = Task.Run(() => HandleAsync(context, stoppingToken), CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (HttpListenerException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _listener.Stop();
            _listener.Close();
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        if (_listener.IsListening)
            _listener.Stop();
        return base.StopAsync(cancellationToken);
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath.TrimEnd('/') ?? string.Empty;
            var method = context.Request.HttpMethod.ToUpperInvariant();
            JsonNode result;

            if (method == "GET" && path == "/api/secagent/v1/students")
                result = ListStudents();
            else if (method == "POST" && path == "/api/secagent/v1/students")
                result = UpsertStudent(await ReadBodyAsync(context.Request, cancellationToken).ConfigureAwait(false));
            else if (method == "DELETE" && path == "/api/secagent/v1/students")
                result = RemoveStudent(await ReadBodyAsync(context.Request, cancellationToken).ConfigureAwait(false));
            else if (method == "POST" && path == "/api/secagent/v1/draw/students")
                result = await DrawStudentsAsync(
                    await ReadBodyAsync(context.Request, cancellationToken).ConfigureAwait(false),
                    cancellationToken).ConfigureAwait(false);
            else
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                result = new JsonObject { ["error"] = "Endpoint not found." };
            }

            await WriteJsonAsync(context.Response, result, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            await WriteErrorAsync(context.Response, HttpStatusCode.BadRequest, ex.Message).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            await WriteErrorAsync(context.Response, HttpStatusCode.Conflict, ex.Message).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SecRandom SecAgent REST request failed.");
            await WriteErrorAsync(context.Response, HttpStatusCode.InternalServerError, "SecRandom request failed.").ConfigureAwait(false);
        }
        finally
        {
            context.Response.Close();
        }
    }

    private JsonObject ListStudents()
    {
        var list = profileService.CurrentStudentList;
        return new JsonObject
        {
            ["profile"] = list?.Name ?? string.Empty,
            ["students"] = new JsonArray((list?.Students ?? []).Select(ToJson).ToArray())
        };
    }

    private JsonObject UpsertStudent(JsonObject arguments)
    {
        var list = profileService.CurrentStudentList ?? throw new InvalidOperationException("No current student profile.");
        var recordId = ParseGuid(arguments["record_id"]?.GetValue<string>());
        var id = StringArgument(arguments, "id");
        var student = recordId is not null ? list.Students.FirstOrDefault(item => item.RecordId == recordId) : null;
        student ??= !string.IsNullOrWhiteSpace(id) ? list.Students.FirstOrDefault(item => item.Id == id) : null;
        if (student is null)
        {
            student = new Student { RecordId = recordId ?? Guid.NewGuid() };
            list.Students.Add(student);
        }

        student.Id = id;
        student.Name = StringArgument(arguments, "name");
        student.Group = StringArgument(arguments, "group");
        student.Gender = StringArgument(arguments, "gender");
        student.Tags = StringArgument(arguments, "tags");
        student.Exists = arguments["exists"]?.GetValue<bool>() ?? true;
        if (!student.IsCandidate)
            throw new ArgumentException("Student requires a nonblank id or name.");
        profileService.SaveProfile();
        return new JsonObject { ["student"] = ToJson(student), ["profile"] = list.Name };
    }

    private JsonObject RemoveStudent(JsonObject arguments)
    {
        var list = profileService.CurrentStudentList ?? throw new InvalidOperationException("No current student profile.");
        var recordId = ParseGuid(arguments["record_id"]?.GetValue<string>());
        var id = StringArgument(arguments, "id");
        var name = StringArgument(arguments, "name");
        var matches = list.Students.Where(item =>
            (recordId is not null && item.RecordId == recordId)
            || (!string.IsNullOrWhiteSpace(id) && item.Id == id)
            || (!string.IsNullOrWhiteSpace(name) && item.Name == name)).ToList();
        if (matches.Count != 1)
            throw new InvalidOperationException(matches.Count == 0 ? "Student was not found." : "Student selector matched more than one student.");
        list.Students.Remove(matches[0]);
        profileService.SaveProfile();
        return new JsonObject { ["removed"] = ToJson(matches[0]), ["profile"] = list.Name };
    }

    private async Task<JsonObject> DrawStudentsAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var result = await externalStudentDrawService.DrawAsync(
            new ExternalStudentDrawRequest
            {
                Mode = StringArgument(arguments, "mode"),
                Count = arguments["count"]?.GetValue<int>() ?? 1,
                Gender = StringArgument(arguments, "gender"),
                IncludeTags = StringArray(arguments, "include_tags"),
                ExcludeTags = StringArray(arguments, "exclude_tags"),
                IncludeIds = StringArray(arguments, "include_ids"),
                IncludeNames = StringArray(arguments, "include_names")
            },
            cancellationToken).ConfigureAwait(false);

        return new JsonObject
        {
            ["mode"] = result.Mode,
            ["count"] = result.Students.Count,
            ["status"] = result.Status,
            ["profile"] = result.Profile,
            ["students"] = new JsonArray(result.Students.Select(ToJson).ToArray())
        };
    }

    private static JsonObject ToJson(Student student) => new()
    {
        ["record_id"] = ProfileRecordIdentity.EnsureRecordId(student),
        ["id"] = student.Id,
        ["name"] = student.Name,
        ["group"] = student.Group,
        ["gender"] = student.Gender,
        ["tags"] = student.Tags,
        ["exists"] = student.Exists
    };

    private static async Task<JsonObject> ReadBodyAsync(HttpListenerRequest request, CancellationToken cancellationToken)
    {
        var body = await JsonNode.ParseAsync(request.InputStream, cancellationToken: cancellationToken).ConfigureAwait(false) as JsonObject;
        return body ?? throw new ArgumentException("Request body must be a JSON object.");
    }

    private static string StringArgument(JsonObject arguments, string name)
        => arguments[name]?.GetValue<string>()?.Trim() ?? string.Empty;

    private static Guid? ParseGuid(string? value)
        => Guid.TryParse(value, out var result) ? result : null;

    private static IReadOnlyList<string> StringArray(JsonObject arguments, string name)
        => arguments[name] is JsonArray array
            ? array.Select(item => item?.GetValue<string>()?.Trim())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Cast<string>()
                .ToArray()
            : [];

    private static async Task WriteJsonAsync(HttpListenerResponse response, JsonNode value, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(value.ToJsonString(JsonOptions));
        response.ContentType = "application/json";
        response.ContentEncoding = Encoding.UTF8;
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static Task WriteErrorAsync(HttpListenerResponse response, HttpStatusCode status, string message)
    {
        response.StatusCode = (int)status;
        return WriteJsonAsync(response, new JsonObject { ["error"] = message }, CancellationToken.None);
    }
}
