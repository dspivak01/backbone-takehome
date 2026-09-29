using Anthropic.Bedrock;
using ClinicalAbstraction.Extraction;
using ClinicalAbstraction.Storage;

namespace ClinicalAbstraction.Web;

/// <summary>
/// How the review page reaches the model: whether one is configured, the client the ask and ingest
/// commands create, and plain words for anything that goes wrong on the way. The tests put a
/// stand-in here, so no test calls a model.
/// </summary>
public sealed class ModelAccess(Func<Task<string?>> missing, Func<Database, Task<ModelClient>> create)
{
    private const string BedrockMissing =
        "No model is configured, because no AWS credentials were found for Amazon Bedrock on this machine. " +
        "Sign in with the AWS command line tool, for example with aws configure or aws sso login, then try again. " +
        "To use the Anthropic API instead, stop the server, set ANTHROPIC_API_KEY and CA_PROVIDER=anthropic, and start it again.";

    private const string AnthropicMissing =
        "No model is configured, because the Anthropic API is chosen and ANTHROPIC_API_KEY is not set. " +
        "Stop the server, set ANTHROPIC_API_KEY, and start it again. To use Amazon Bedrock instead, set CA_PROVIDER=bedrock and sign in with the AWS command line tool.";

    private const string Unexpected = "Something unexpected went wrong. The details are printed in the window where the server was started.";

    /// <summary>The model the ask and ingest commands use, as the environment configures it.</summary>
    public static ModelAccess FromEnvironment() =>
        new(MissingFromEnvironment, database => ModelClient.CreateAsync(database, ModelSettings.FromEnvironment()));

    /// <summary>A stand-in with no model, which says what is missing.</summary>
    public static ModelAccess None(string message) =>
        new(() => Task.FromResult<string?>(message), _ => throw new RequestException(message));

    /// <summary>What must be configured before a model can be called, in plain words, or null when nothing is missing.</summary>
    public Task<string?> Missing() => missing();

    /// <summary>A client for the model, or a plain message saying why there is none.</summary>
    public async Task<ModelClient> Create(Database database)
    {
        if (await missing() is { } what) throw new RequestException(what);
        try
        {
            return await create(database);
        }
        catch (Exception e) when (e is not RequestException)
        {
            Console.Error.WriteLine($"The model client could not be set up. {e}");
            throw new RequestException($"The model client could not be set up. {Describe(e)}");
        }
    }

    private static async Task<string?> MissingFromEnvironment()
    {
        var settings = ModelSettings.FromEnvironment();
        if (settings.Provider == "anthropic")
            return string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")) ? AnthropicMissing : null;
        if (settings.Provider != "bedrock")
            return $"No model is configured, because CA_PROVIDER is set to \"{settings.Provider}\". Stop the server, set CA_PROVIDER to bedrock or anthropic, and start it again.";
        try
        {
            // The same lookup the ask and ingest commands make. It reads the credentials and calls no model.
            Environment.SetEnvironmentVariable("AWS_REGION", settings.Region);
            return await AnthropicBedrockCredentialsHelper.FromEnv() is null ? BedrockMissing : null;
        }
        catch (Exception e)
        {
            // The AWS library says it found no credentials by throwing. One line is enough here.
            Console.Error.WriteLine($"No AWS credentials were found for Amazon Bedrock ({e.GetType().Name}).");
            return BedrockMissing;
        }
    }

    /// <summary>
    /// A failure in plain words. Messages this program writes are kept. A failure reaching the model
    /// is described by what the reviewer can do about it, never by the provider's own message or
    /// error code, which are printed in the server's window instead.
    /// </summary>
    public static string Describe(Exception e)
    {
        foreach (var x in Chain(e))
        {
            if (x is RequestException or NotFoundException or ConflictException or UnavailableException) return x.Message;
            var name = x.GetType().Name;
            var space = x.GetType().Namespace ?? "";
            if (name is "AnthropicUnauthorizedException" or "AnthropicForbiddenException")
                return "The model provider refused the credentials. For Amazon Bedrock, sign in again with the AWS command line tool, for example with aws sso login, and check that the account may use the model. For the Anthropic API, check ANTHROPIC_API_KEY.";
            if (name is "AnthropicRateLimitException" || name.StartsWith("Anthropic5xx", StringComparison.Ordinal))
                return "The model provider is busy or unavailable at the moment. Try again in a minute.";
            if (name is "AnthropicBadRequestException" or "AnthropicNotFoundException" or "AnthropicUnprocessableEntityException")
                return "The model provider did not accept the request. The model named in the settings may not be available to this account or region. The details are printed in the window where the server was started.";
            if (space.StartsWith("Amazon", StringComparison.Ordinal))
                return "The AWS credentials could not be used. Sign in again with the AWS command line tool, for example with aws sso login, then try again.";
            if (name is "AnthropicIOException" || x is HttpRequestException or System.Net.Sockets.SocketException or TimeoutException or TaskCanceledException)
                return "The model could not be reached. Check the network connection, then try again.";
        }
        return Unexpected;
    }

    private static IEnumerable<Exception> Chain(Exception e)
    {
        var queue = new Queue<Exception>([e]);
        for (var seen = 0; queue.Count > 0 && seen < 20; seen++)
        {
            var x = queue.Dequeue();
            yield return x;
            if (x is AggregateException many) foreach (var inner in many.InnerExceptions) queue.Enqueue(inner);
            else if (x.InnerException is { } inner) queue.Enqueue(inner);
        }
    }
}
