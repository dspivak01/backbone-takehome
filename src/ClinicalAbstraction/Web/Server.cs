using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ClinicalAbstraction.Domain;
using ClinicalAbstraction.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClinicalAbstraction.Web;

/// <summary>
/// The web layer behind the serve command. It maps each request to a method of
/// <see cref="ReviewApi"/> and turns every failure into a plain message. It listens on 127.0.0.1
/// only, answers only requests addressed to this machine by name, and tells the browser to load
/// nothing from anywhere else and to run no script except the page's own.
/// </summary>
public static class Server
{
    public const int DefaultPort = 5173;

    private const string PageResource = "ClinicalAbstraction.Web.page.html";

    /// <summary>Starts the server for the serve command and keeps it running until Ctrl+C or a stop signal.</summary>
    public static async Task<int> RunAsync(Options options)
    {
        var port = ReadPort(options.Get("port"));
        var path = options.DatabasePath;
        var existed = File.Exists(path);

        using var database = new Database(path);
        await using var app = Build(database, options.OutputDirectory, port);
        try
        {
            await app.StartAsync();
        }
        catch (IOException e) when (e.InnerException is AddressInUseException)
        {
            throw new InvalidOperationException($"Port {port} on 127.0.0.1 is already in use. Choose another with --port <number>.");
        }

        Console.WriteLine(existed
            ? $"Serving the abstraction in {Path.GetFullPath(path)}"
            : $"No database existed at {Path.GetFullPath(path)}, so an empty one was created. The page will say that no documents have been processed.");
        Console.WriteLine($"Open http://127.0.0.1:{port}/ in a browser on this machine. Other machines cannot connect.");
        Console.WriteLine("The Patients, Trace and Gap log screens only read the saved abstraction. Ask and Documents call the model, as the ask and ingest commands do, and save what they add under the output folder.");
        Console.WriteLine("Press Ctrl+C to stop.");

        await app.WaitForShutdownAsync();
        Console.WriteLine("Stopped.");
        return 0;
    }

    private static int ReadPort(string? text)
    {
        if (text is null) return DefaultPort;
        return int.TryParse(text, out var port) && port is >= 1 and <= 65535
            ? port
            : throw new ArgumentException($"--port takes a number from 1 to 65535, not \"{text}\".");
    }

    /// <summary>
    /// Builds the server without starting it. Port 0 asks the system for any free port, which the
    /// tests use. No configuration file or environment variable is read, so nothing outside this
    /// method can add an address to listen on. The model is the one the environment configures,
    /// unless a stand-in is given, as the tests give one.
    /// </summary>
    public static WebApplication Build(Database database, string outputFolder, int port, ModelAccess? model = null)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseKestrelCore().ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(IPAddress.Loopback, port);
            kestrel.AddServerHeader = false;
        });
        builder.Services.AddRoutingCore();

        // A web page elsewhere could point its own name at 127.0.0.1 to read this server as if it
        // were the page's own. Answering only requests addressed to this machine by name stops that.
        builder.Services.AddHostFiltering(filter =>
        {
            filter.AllowedHosts = ["127.0.0.1", "localhost"];
            filter.AllowEmptyHosts = false;
            filter.IncludeFailureMessage = false;
        });

        var app = builder.Build();
        var page = Page();
        var policy = ContentSecurityPolicy(page);

        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers.ContentSecurityPolicy = policy;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            headers["Cross-Origin-Resource-Policy"] = "same-origin";
            headers.CacheControl = "no-store";
            await next(context);
        });
        app.UseHostFiltering();

        // A page on another site can send a POST to this address from the reviewer's own browser.
        // The browser names the page it came from in the Origin header, and a page elsewhere cannot
        // change that, so a request that changes something is taken only from this server's own page.
        app.Use(async (context, next) =>
        {
            if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) && !FromThisPage(context))
            {
                await Failure(StatusCodes.Status403Forbidden, RefusedOrigin).ExecuteAsync(context);
                return;
            }
            await next(context);
        });

        var api = new ReviewApi(database, outputFolder);
        MapPage(app, page);
        MapStageOne(app, api);
        MapStageTwo(app, new LibraryApi(database), new WorkApi(database, outputFolder, model ?? ModelAccess.FromEnvironment(), new JobStore()));

        // Anything else gets a plain message, never a framework error page.
        app.MapFallback("/api/{**rest}", () => Failure(StatusCodes.Status404NotFound, "There is no such request."));
        app.MapFallback(() => Results.Text("Not found. The review page is at /.", "text/plain; charset=utf-8", statusCode: StatusCodes.Status404NotFound));
        return app;
    }

    private static void MapPage(WebApplication app, string page) =>
        app.MapGet("/", () => Results.Text(page, "text/html; charset=utf-8"));

    /// <summary>
    /// The requests the Patients and Trace screens make. Stage 2 adds its requests, including the
    /// POST requests and jobs, in a method of its own beside this one.
    /// </summary>
    private static void MapStageOne(WebApplication app, ReviewApi api)
    {
        app.MapGet("/api/status", () => Answer("The database status could not be read.", api.Status));
        app.MapGet("/api/patients", (string? search) => Answer("The patient list could not be made.", () => api.Patients(search)));
        app.MapGet("/api/patients/{key}", (string key) => Answer("The patient's record could not be shown.", () => api.Patient(key)));
        app.MapGet("/api/patients/{key}/weekly", (string key, string? from, string? to) => Answer("The weekly results could not be worked out.", () => api.Weekly(key, from, to)));
        app.MapGet("/api/patients/{key}/sessions", (string key, string? from, string? to) => Answer("The session counts could not be worked out.", () => api.Sessions(key, from, to)));
        app.MapGet("/api/patients/{key}/encounters/{id}", (string key, string id) => Answer("The encounter could not be shown.", () => api.Encounter(key, id)));
        app.MapGet("/api/patients/{key}/day", (string key, string? date) => Answer("The day could not be worked out.", () => api.Day(key, date)));
        app.MapGet("/api/patients/{key}/measures", (string key, string? instrument) => Answer("The symptom assessments could not be listed.", () => api.Measures(key, instrument)));
        app.MapGet("/api/patients/{key}/observations", (string key, string? from, string? to, string? category) =>
            Answer("The quoted observations could not be listed.", () => api.Observations(key, from, to, category)));
        app.MapGet("/api/collection/consecutive", (string? from, string? to) => Answer("The consecutive weeks below the goal could not be worked out.", () => api.Consecutive(from, to)));
        app.MapGet("/api/collection/summary", (string? from, string? to) => Answer("The collection summary could not be worked out.", () => api.CollectionSummary(from, to)));
        app.MapGet("/api/source", (string? document, string? from, string? to, string? context, string? patient) =>
            Answer("The source lines could not be shown.", () => api.Source(document, from, to, context, patient)));

        // The same requests with the patient and the encounter in the query string. Identifiers
        // come from documents and can hold "/", "%", "#" or "?", or be "." or "..", which a
        // browser or the server would read as part of the path. The page uses these.
        app.MapGet("/api/patient", (string? key) => Answer("The patient's record could not be shown.", () => api.Patient(key ?? "")));
        app.MapGet("/api/patient/weekly", (string? key, string? from, string? to) => Answer("The weekly results could not be worked out.", () => api.Weekly(key ?? "", from, to)));
        app.MapGet("/api/patient/sessions", (string? key, string? from, string? to) => Answer("The session counts could not be worked out.", () => api.Sessions(key ?? "", from, to)));
        app.MapGet("/api/patient/encounter", (string? key, string? id) => Answer("The encounter could not be shown.", () => api.Encounter(key ?? "", id ?? "")));
        app.MapGet("/api/patient/day", (string? key, string? date) => Answer("The day could not be worked out.", () => api.Day(key ?? "", date)));
        app.MapGet("/api/patient/measures", (string? key, string? instrument) => Answer("The symptom assessments could not be listed.", () => api.Measures(key ?? "", instrument)));
        app.MapGet("/api/patient/observations", (string? key, string? from, string? to, string? category) =>
            Answer("The quoted observations could not be listed.", () => api.Observations(key ?? "", from, to, category)));
    }

    public const string RefusedOrigin =
        "This request was refused, because it did not come from the review page served at this address. Documents can be added and questions asked only from that page.";

    /// <summary>
    /// Whether a request names this server's own page as where it came from. The page's own
    /// requests always carry the header; a request without it, or from any other page, is refused.
    /// </summary>
    public static bool FromThisPage(HttpContext context)
    {
        var origin = context.Request.Headers.Origin;
        if (origin.Count != 1) return false;
        var port = context.Connection.LocalPort;
        return origin[0] == $"http://127.0.0.1:{port}" || origin[0] == $"http://localhost:{port}";
    }

    /// <summary>
    /// The requests the Ask, Documents and Gap log screens make. Identifiers go in the query string,
    /// as the stage 1 requests the page uses do. The two POST requests start jobs.
    /// </summary>
    private static void MapStageTwo(WebApplication app, LibraryApi library, WorkApi work)
    {
        app.MapGet("/api/model", () => AnswerAsync("Whether a model is configured could not be checked.", async () => await work.Model()));
        app.MapGet("/api/jobs", (string? id) => Answer("The progress could not be read.", () => work.Job(id)));
        app.MapGet("/api/jobs/{id}", (string id) => Answer("The progress could not be read.", () => work.Job(id)));
        app.MapGet("/api/answers", () => Answer("The earlier questions could not be listed.", library.Answers));
        app.MapGet("/api/answer", (string? id) => Answer("The answer could not be shown.", () => library.Answer(id)));
        app.MapGet("/api/documents", () => Answer("The documents could not be listed.", library.Documents));
        app.MapGet("/api/document", (string? id) => Answer("The document could not be shown.", () => library.Document(id)));
        app.MapGet("/api/documents/{id}", (string id) => Answer("The document could not be shown.", () => library.Document(id)));
        app.MapGet("/api/gaps", () => Answer("The gap log could not be read.", library.Gaps));

        // Typed as a function returning its result, so the result is written to the response.
        Func<HttpContext, Task<IResult>> ask = context => AnswerAsync("The question could not be sent.", async () =>
        {
            var request = await ReadJson<AskRequest>(context);
            return await work.Ask(request.Question, request.Patient);
        });
        app.MapPost("/api/ask", ask);

        Func<HttpContext, Task<IResult>> add = context => AnswerAsync("The documents could not be added.", async () =>
        {
            if (!context.Request.HasFormContentType) throw new RequestException("Send the files or the folder from the Documents screen.");
            IFormCollection form;
            try
            {
                form = await context.Request.ReadFormAsync();
            }
            catch (Exception e) when (e is InvalidDataException or IOException or BadHttpRequestException)
            {
                throw new RequestException("The files could not be received. Together they may be too large: add them in smaller groups, each file no larger than 1 MB.");
            }

            if (form.Files.Count == 0) return await work.AddFolder(form["folder"].ToString());
            var files = new List<UploadedFile>();
            foreach (var file in form.Files)
            {
                // A file over the limit is refused before it is read into memory.
                if (file.Length > Uploads.MaxBytes) throw new RequestException(Uploads.TooLarge(file.FileName));
                using var stream = new MemoryStream();
                await file.CopyToAsync(stream);
                files.Add(new UploadedFile(file.FileName, stream.ToArray()));
            }
            return await work.AddFiles(files);
        });
        app.MapPost("/api/documents", add);
    }

    /// <summary>A small JSON request body. Anything else, including a form a page elsewhere could send without asking, is refused.</summary>
    private static async Task<T> ReadJson<T>(HttpContext context) where T : new()
    {
        if (!(context.Request.ContentType ?? "").StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            throw new RequestException("Send the question from the Ask screen.");
        if (context.Request.ContentLength > 64 * 1024) throw new RequestException("The request is too large.");
        try
        {
            return await System.Text.Json.JsonSerializer.DeserializeAsync<T>(context.Request.Body, Json.Options) ?? new T();
        }
        catch (System.Text.Json.JsonException)
        {
            throw new RequestException("The request could not be read.");
        }
    }

    /// <summary>
    /// Runs one request and writes its result as JSON. The result is serialised here, inside the
    /// error handling, so no failure can reach the browser as a stack trace. A failure the reviewer
    /// can act on, which this program raises with a message of its own, keeps that message after
    /// the sentence saying what failed. Anything else, including every message .NET writes, says
    /// what failed and nothing more.
    /// </summary>
    public static IResult Answer(string failure, Func<object> work)
    {
        try
        {
            return Results.Text(Json.Write(work()), "application/json; charset=utf-8");
        }
        catch (NotFoundException e)
        {
            return Failure(StatusCodes.Status404NotFound, e.Message);
        }
        catch (RequestException e)
        {
            return Failure(StatusCodes.Status400BadRequest, $"{failure} {e.Message}");
        }
        catch (ConflictException e)
        {
            return Failure(StatusCodes.Status409Conflict, e.Message);
        }
        catch (UnavailableException e)
        {
            return Failure(StatusCodes.Status503ServiceUnavailable, e.Message);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"{failure} {e}");
            return Failure(StatusCodes.Status500InternalServerError,
                $"{failure} Something unexpected went wrong. The details are printed in the window where the server was started.");
        }
    }

    /// <summary>The same as <see cref="Answer"/>, for work that waits on something, such as reading a request body.</summary>
    public static async Task<IResult> AnswerAsync(string failure, Func<Task<object>> work)
    {
        object result;
        try
        {
            result = await work();
        }
        catch (Exception e)
        {
            return Answer(failure, () =>
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(e);
                return null!;
            });
        }
        return Answer(failure, () => result);
    }

    private static IResult Failure(int status, string message) =>
        Results.Text(Json.Write(new ErrorView { Error = message }), "application/json; charset=utf-8", statusCode: status);

    // ---------- the page and its policy ----------

    /// <summary>The page, read from inside the program. Line endings are made plain so the hashes below match what the browser reads.</summary>
    public static string Page()
    {
        using var stream = typeof(Server).Assembly.GetManifestResourceStream(PageResource)
            ?? throw new InvalidOperationException("The review page is missing from the program. Rebuild it with dotnet build.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Replace("\r\n", "\n");
    }

    /// <summary>
    /// The Content-Security-Policy for the page. The page's one script and one style block are
    /// allowed by their hashes, so no other script can run: not one from another site, not one
    /// inserted into the page, and not an inline event handler such as onerror. Requests go only
    /// to this server. Where the browser supports it, writing text into the page as markup is
    /// refused outright.
    /// </summary>
    public static string ContentSecurityPolicy(string page) =>
        "default-src 'none'; " +
        $"script-src {Hashes(page, "script")}; " +
        $"style-src {Hashes(page, "style")}; " +
        "connect-src 'self'; img-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'; " +
        "require-trusted-types-for 'script'; trusted-types 'none'";

    private static string Hashes(string page, string tag)
    {
        var hashes = Regex.Matches(page, $"<{tag}>(.*?)</{tag}>", RegexOptions.Singleline)
            .Select(m => $"'sha256-{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(m.Groups[1].Value)))}'")
            .ToList();
        return hashes.Count == 0 ? "'none'" : string.Join(' ', hashes);
    }
}
