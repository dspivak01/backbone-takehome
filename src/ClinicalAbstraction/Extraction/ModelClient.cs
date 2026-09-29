using System.Diagnostics;
using Anthropic;
using Anthropic.Bedrock;
using Anthropic.Models.Messages;
using ClinicalAbstraction.Storage;

namespace ClinicalAbstraction.Extraction;

/// <summary>
/// Which provider and models to use. Everything comes from environment variables so the same
/// build runs against Amazon Bedrock or directly against the Anthropic API.
/// </summary>
public sealed class ModelSettings
{
    public string Provider { get; init; } = "bedrock";
    public string Region { get; init; } = "us-west-2";
    public string ExtractionModel { get; init; } = "sonnet-5";
    public string AnswerModel { get; init; } = "sonnet-5";
    public int Concurrency { get; init; } = 6;

    public static ModelSettings FromEnvironment()
    {
        var hasApiKey = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));
        return new ModelSettings
        {
            Provider = (Environment.GetEnvironmentVariable("CA_PROVIDER") ?? (hasApiKey ? "anthropic" : "bedrock")).ToLowerInvariant(),
            Region = Environment.GetEnvironmentVariable("CA_AWS_REGION") ?? Environment.GetEnvironmentVariable("AWS_REGION") ?? "us-west-2",
            // Defaults chosen from the model comparison in output/experiments/model-comparison.
            ExtractionModel = Environment.GetEnvironmentVariable("CA_EXTRACT_MODEL") ?? "sonnet-5",
            AnswerModel = Environment.GetEnvironmentVariable("CA_ANSWER_MODEL") ?? "sonnet-5",
            Concurrency = int.TryParse(Environment.GetEnvironmentVariable("CA_CONCURRENCY"), out var n) && n > 0 ? n : 6,
        };
    }

    /// <summary>Turns a short name such as "sonnet-5" into the identifier the provider expects. Full identifiers pass through.</summary>
    public string ResolveModelId(string name)
    {
        var bedrock = Provider == "bedrock";
        return name.ToLowerInvariant() switch
        {
            "haiku-4.5" or "haiku" => bedrock ? "us.anthropic.claude-haiku-4-5-20251001-v1:0" : "claude-haiku-4-5",
            "sonnet-5" or "sonnet" => bedrock ? "us.anthropic.claude-sonnet-5" : "claude-sonnet-5",
            "opus-5" or "opus" => bedrock ? "us.anthropic.claude-opus-5" : "claude-opus-5",
            _ => name,
        };
    }

    /// <summary>
    /// Anthropic list prices in US dollars per million tokens (input, output). Bedrock bills at its
    /// own rates, so costs computed from these are estimates of price, applied to measured tokens.
    /// </summary>
    public static (decimal Input, decimal Output)? ListPrice(string modelId)
    {
        var id = modelId.ToLowerInvariant();
        if (id.Contains("haiku-4-5")) return (1m, 5m);
        if (id.Contains("sonnet-5")) return (2m, 10m);
        if (id.Contains("opus-5-5")) return (4m, 20m);
        if (id.Contains("opus-5")) return (5m, 25m);
        return null;
    }

    /// <summary>Cache writes are billed at 1.25 times the input price and cache reads at 0.1 times.</summary>
    public static decimal? EstimateCost(string modelId, long inputTokens, long outputTokens, long cacheWriteTokens = 0, long cacheReadTokens = 0) =>
        ListPrice(modelId) is { } price
            ? (inputTokens * price.Input + cacheWriteTokens * price.Input * 1.25m + cacheReadTokens * price.Input * 0.1m + outputTokens * price.Output) / 1_000_000m
            : null;
}

/// <summary>Sends requests to the model and records tokens, latency and stop reason for every call.</summary>
public sealed class ModelClient
{
    private readonly IAnthropicClient _client;
    private readonly Database _database;

    public ModelSettings Settings { get; }

    private ModelClient(IAnthropicClient client, Database database, ModelSettings settings)
    {
        _client = client;
        _database = database;
        Settings = settings;
    }

    public static async Task<ModelClient> CreateAsync(Database database, ModelSettings settings)
    {
        IAnthropicClient client;
        if (settings.Provider == "anthropic")
        {
            client = new AnthropicClient();
        }
        else
        {
            Environment.SetEnvironmentVariable("AWS_REGION", settings.Region);
            var credentials = await AnthropicBedrockCredentialsHelper.FromEnv()
                ?? throw new InvalidOperationException(
                    "No AWS credentials were found for Amazon Bedrock. Configure the AWS CLI, or set ANTHROPIC_API_KEY and CA_PROVIDER=anthropic to call the Anthropic API directly.");
            client = new AnthropicBedrockClient(credentials);
        }
        return new ModelClient(client, database, settings);
    }

    public async Task<Message> SendAsync(MessageCreateParams request, string purpose, string? reference, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var response = await _client.Messages.Create(request, cancellationToken);
        stopwatch.Stop();

        _database.RecordModelCall(new ModelCall(
            purpose, Settings.Provider, Plain(request.Model.ToString()), response.Usage.InputTokens, response.Usage.OutputTokens,
            response.Usage.CacheCreationInputTokens ?? 0, response.Usage.CacheReadInputTokens ?? 0,
            stopwatch.ElapsedMilliseconds, reference, Plain(response.StopReason?.ToString())));
        return response;
    }

    /// <summary>The SDK prints some values as quoted JSON strings. This removes the quotes.</summary>
    public static string Plain(string? value) => (value ?? "").Trim('"');
}
