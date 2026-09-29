using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Anthropic.Models.Messages;
using ClinicalAbstraction.Calculation;
using ClinicalAbstraction.Domain;
using ClinicalAbstraction.Extraction;
using ClinicalAbstraction.Reporting;
using ClinicalAbstraction.Storage;

namespace ClinicalAbstraction.Questions;

public sealed record Answer(string Markdown, string Json, long LatencyMs, bool FromCache)
{
    /// <summary>The row the answer was saved as, so the review page can open it again.</summary>
    public long SavedId { get; init; }
}

/// <summary>
/// One calculation the model asked for, reported while a question is being answered. A calculation
/// held back because it gave a date before the patients were listed is reported too.
/// </summary>
public sealed record CalculationProgress(string Name, string Input, bool HeldBack, bool Failed);

public sealed class ToolCallRecord
{
    public string Name { get; set; } = "";
    public string Input { get; set; } = "";
    public double ElapsedMs { get; set; }
    public bool Failed { get; set; }
    public string Rendered { get; set; } = "";
    public JsonElement Result { get; set; }
}

public sealed class AnswerRecord
{
    public string? QuestionId { get; set; }
    public string Question { get; set; } = "";
    public string Model { get; set; } = "";
    public string AbstractionVersion { get; set; } = "";
    public string Narrative { get; set; } = "";
    public NarrativeCheckResult Checks { get; set; } = new();
    public List<ToolCallRecord> Calculations { get; set; } = [];

    /// <summary>Calculations the model asked for that were held back because they gave a date before the patients had been listed.</summary>
    public int CalculationsNotRun { get; set; }
    public int ModelCalls { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long LatencyMs { get; set; }
}

/// <summary>
/// Answers a question from the saved abstraction. The model picks calculations and writes the
/// prose. Code runs the calculations, checks the prose against their results, and appends the
/// tables and sources itself. Nothing here changes the abstraction.
/// </summary>
public sealed class QuestionAnswerer(Database database, ModelClient model, Log log)
{
    private const int MaxRounds = 8;

    private const string NotBeforeFindPatient =
        "This calculation was not run, because it was given a date before find_patient had returned. Call find_patient first if you have not. Then call this calculation again if the question still needs it, and take the year of any date from the episode dates that find_patient lists.";

    /// <summary>The inputs that carry a date. A calculation given one of these waits until find_patient has returned.</summary>
    private static readonly string[] DateInputs = ["date", "from", "to"];

    private const string Instructions = """
        You answer questions about patients' clinical records for a reviewer who will check your work. You have no access to the documents. You have tools that run calculations over a saved abstraction of the record and return results with their sources.

        How to work
        1. Make find_patient your first call, and make no other call until it has returned. Give it the name or number the question gives, or an empty query when the question names no patient or is about every patient. Its result lists each patient's episode dates. When the question gives a day and a month without a year, take the year from those episode dates and say in the answer which year you used. Never guess a year. If the episodes fall in more than one year, run the calculations for each of those years.
        2. Settle who the question is about. The tools decide this from the record. You never decide it.
           - The question names a patient: if find_patient returned no match or more than one, say so and stop. Never guess which patient is meant.
           - The question is about every patient, for example "which patients": use the tools that examine the whole collection, and say how many patients were examined. This is not a question that names no patient, so the points below do not apply to it.
           - The question names no patient and a default patient was set for the question: use the default patient and say that you did. The default patient takes priority over everything below.
           - The question names no patient, no default patient was set, and the collection holds exactly one patient: use that patient and begin the answer by saying the question named no patient and that the only patient in the collection was used.
           - The question names no patient, no default patient was set, and the collection holds more than one patient: the question applies to every patient who fits what the question does specify. Find who fits with a tool:
               for a question about one or more single dates, call day_reconstruction once for each date, without a patient_key;
               for any other question that gives a date or a period, call patients_with_contacts with those dates, once for each date or period, and every patient listed by any of those results fits;
               for a question about sessions, therapy days, minutes or goals, call collection_summary, with the dates the question gives, or with no dates when it gives none;
               for a question that gives no date and is not covered by collection_summary, every patient that find_patient lists fits.
             Then answer for every patient who fits, in the order the results list them, under a heading for each patient that gives the patient's name and patient_key. Run the other calculations the question needs once for each of those patients. If the record holds nothing on part of the question for one of them, say so under that patient's heading.
             Begin the answer by saying that the question named no patient and which patients it is answered for. Say which patients are left out and why, as the results state it, including when no other patient fits.
             Never answer for only one of the patients who fit. Never leave out a patient who fits, and never add one who does not. Never work out which patient was meant from dates, services, names of staff or anything else. Never ask which patient is meant.
             When a result says that more patients fit than the limit for full detail, give the summary table for all of them, give full detail for none, run no further calculations for individual patients, and repeat what the result says about how to narrow the question.
        3. Call the calculations the question needs. Leave out from and to when the question gives no dates, so the patient's episode is used. Both dates of a range are included.
        4. When a question asks how something changed over time, gather the measures and the observations for the whole period, not for one date, and include what the clinicians themselves wrote about progress.
        5. Use search_passages only when the calculations do not hold what the question asks for.
        6. Then write the answer.

        Rules for the answer
        - Use only numbers that appear in tool results. Do not add, subtract, average, convert or round numbers yourself. If a number the question asks for is not in a result, say that it is not available.
        - When the answer covers more than one patient, give each patient's figures under that patient's heading. Never add, average or otherwise combine figures from different patients. Give a figure for several patients together only when a result states that figure.
        - Report ranges and "cannot be determined" exactly as the results give them. When something is unsettled, say what it is, which documents disagree, and what documentation would settle it.
        - Support every statement about the record with the sources given in the results, in square brackets, for example [BH-D103 L7]. Use the source identifiers exactly as they appear.
        - Say only what the record says. Do not add severity labels, diagnoses, interpretations of scores, or causes. Do not say a treatment worked or why a score changed unless you are quoting the record, and then quote it.
        - When a question asks what can and cannot be concluded, take the limits from the tool results and from what the record does not contain.
        - State the assumptions the results list when they affect the answer.
        - Label anything that came from search_passages as found by text search.
        - Be direct and brief. Lead with the answer. Use short paragraphs, lists or small tables. Do not describe your process, and do not mention tools, calculations or results by name. The full calculation tables are appended to your answer automatically, so do not reproduce every row.
        """;

    // The words that carry a default patient into the question. They are part of the key a saved
    // answer is found by, so they must not change.
    private const string DefaultPatientBefore = "\n\n(Default patient set by the person asking: ";
    private const string DefaultPatientAfter = ". Use it if the question names no patient, and say that you did.)";

    /// <summary>The question as it is sent to the model and saved: with the default patient, when one is set.</summary>
    public static string Asked(string question, string? defaultPatient) =>
        defaultPatient is null ? question : question + DefaultPatientBefore + defaultPatient + DefaultPatientAfter;

    /// <summary>A saved question split back into the question and its default patient.</summary>
    public static (string Question, string? DefaultPatient) ReadAsked(string asked)
    {
        var at = asked.LastIndexOf(DefaultPatientBefore, StringComparison.Ordinal);
        return at >= 0 && asked.EndsWith(DefaultPatientAfter, StringComparison.Ordinal) && at + DefaultPatientBefore.Length <= asked.Length - DefaultPatientAfter.Length
            ? (asked[..at], asked[(at + DefaultPatientBefore.Length)..^DefaultPatientAfter.Length])
            : (asked, null);
    }

    public async Task<Answer> AnswerAsync(string? id, string question, bool useCache, string? defaultPatient = null, Action<CalculationProgress>? progress = null)
    {
        var asked = Asked(question, defaultPatient);

        var modelId = model.Settings.ResolveModelId(model.Settings.AnswerModel);
        // A saved answer is reused only while the abstraction, the model and these instructions are all unchanged.
        var version = $"{database.CollectionVersion()}-{Hashing.Sha256(Instructions + NotBeforeFindPatient)[..8]}";

        if (useCache && database.FindCachedAnswer(asked, version, modelId) is { } cached)
        {
            var timer = Stopwatch.StartNew();
            var reused = database.SaveAnswer(id, asked, version, modelId, timer.ElapsedMilliseconds, true, cached.Markdown, cached.Json);
            return new Answer(cached.Markdown, cached.Json, timer.ElapsedMilliseconds, true) { SavedId = reused };
        }

        var stopwatch = Stopwatch.StartNew();
        var record = new AnswerRecord { QuestionId = id, Question = question, Model = modelId, AbstractionVersion = version };
        var tools = new QuestionTools(new Calculations(database), question);
        var messages = new List<MessageParam> { new() { Role = Role.User, Content = asked } };
        var narrative = new StringBuilder();
        var collectionSeen = false;

        for (var round = 1; round <= MaxRounds; round++)
        {
            var response = await model.SendAsync(new MessageCreateParams
            {
                Model = modelId,
                MaxTokens = 16000,
                System = new List<TextBlockParam> { new() { Text = Instructions, CacheControl = new CacheControlEphemeral() } },
                Tools = QuestionTools.Definitions(),
                Messages = messages,
            }, "question", id);

            record.ModelCalls++;
            record.InputTokens += response.Usage.InputTokens + (response.Usage.CacheReadInputTokens ?? 0) + (response.Usage.CacheCreationInputTokens ?? 0);
            record.OutputTokens += response.Usage.OutputTokens;

            var assistant = new List<ContentBlockParam>();
            var results = new List<ContentBlockParam>();
            var text = new StringBuilder();
            var listedPatients = false;

            foreach (var block in response.Content)
            {
                if (block.TryPickText(out TextBlock? t))
                {
                    assistant.Add(new TextBlockParam { Text = t.Text });
                    text.Append(t.Text);
                }
                else if (block.TryPickThinking(out ThinkingBlock? thinking))
                {
                    assistant.Add(new ThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
                }
                else if (block.TryPickRedactedThinking(out RedactedThinkingBlock? redacted))
                {
                    assistant.Add(new RedactedThinkingBlockParam { Data = redacted.Data });
                }
                else if (block.TryPickToolUse(out ToolUseBlock? call))
                {
                    assistant.Add(new ToolUseBlockParam { ID = call.ID, Name = call.Name, Input = call.Input });

                    // A calculation that is given a date does not run until the model has been shown who is in
                    // the collection and the dates their episodes cover. A question often gives a day and a
                    // month with no year, and without this the model asks about a year it has guessed.
                    if (!collectionSeen && call.Name != "find_patient" && DateInputs.Any(name => call.Input.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String))
                    {
                        log.Detail($"calculation {call.Name} {JsonSerializer.Serialize(call.Input)} -> not run, because it gives a date and find_patient had not yet returned");
                        results.Add(new ToolResultBlockParam { ToolUseID = call.ID, Content = Json.Write(new { not_run = NotBeforeFindPatient }), IsError = true });
                        record.CalculationsNotRun++;
                        progress?.Invoke(new CalculationProgress(call.Name, JsonSerializer.Serialize(call.Input), true, false));
                        continue;
                    }

                    var (content, failed) = Run(tools, call, record);
                    results.Add(new ToolResultBlockParam { ToolUseID = call.ID, Content = content, IsError = failed });
                    listedPatients |= call.Name == "find_patient";
                    progress?.Invoke(new CalculationProgress(call.Name, record.Calculations[^1].Input, false, failed));
                }
            }

            collectionSeen |= listedPatients;

            if (results.Count == 0)
            {
                narrative.Append(text);
                break;
            }

            messages.Add(new MessageParam { Role = Role.Assistant, Content = assistant });
            messages.Add(new MessageParam { Role = Role.User, Content = results });
            if (round == MaxRounds) narrative.Append("The question could not be answered within the allowed number of calculation rounds.");
        }

        stopwatch.Stop();
        record.Narrative = narrative.ToString().Trim();
        record.LatencyMs = stopwatch.ElapsedMilliseconds;
        record.Checks = NarrativeChecks.Check(record.Narrative, question, record.Calculations.Select(c => c.Result.GetRawText()));

        var markdown = Render(record);
        var json = Json.Write(record, indented: true);
        var saved = database.SaveAnswer(id, asked, version, modelId, record.LatencyMs, false, markdown, json);
        return new Answer(markdown, json, record.LatencyMs, false) { SavedId = saved };
    }

    private (string Content, bool Failed) Run(QuestionTools tools, ToolUseBlock call, AnswerRecord record)
    {
        var timer = Stopwatch.StartNew();
        var entry = new ToolCallRecord { Name = call.Name, Input = JsonSerializer.Serialize(call.Input) };
        string content;
        try
        {
            var result = tools.Run(call.Name, call.Input);
            content = Json.Write(result);
            entry.Rendered = Markdown.Render(result);
        }
        catch (Exception e)
        {
            entry.Failed = true;
            content = Json.Write(new { error = e.Message });
            entry.Rendered = $"The calculation failed: {e.Message}\n";
        }
        entry.ElapsedMs = timer.Elapsed.TotalMilliseconds;
        entry.Result = JsonDocument.Parse(content).RootElement.Clone();
        record.Calculations.Add(entry);
        log.Detail($"calculation {call.Name} {entry.Input} -> {(entry.Failed ? "failed" : "ok")} in {entry.ElapsedMs:0.0} ms");
        return (content, entry.Failed);
    }

    /// <summary>Who wrote each part of an answer, as the answer file and the review page both say it.</summary>
    public const string NarrativeBy = "Written by the model from the calculation results below.";
    public const string ChecksBy = "Run by code after the model finished.";
    public const string CalculationsBy = "Produced by code from the saved abstraction. No model wrote or edited these tables.";

    /// <summary>The rows of the checks table, as the answer file and the review page both show them.</summary>
    public static List<string[]> CheckRows(NarrativeCheckResult checks) =>
    [
        ["Sources cited", $"{checks.CitationsFound} cited, {checks.CitationsVerified} found in the calculation results"],
        ["Sources not found in the results", checks.CitationsNotInResults.Count == 0 ? "None" : string.Join(", ", checks.CitationsNotInResults)],
        ["Numbers in the text", $"{checks.NumbersFound} found"],
        ["Numbers not found in the results or the question", checks.NumbersNotInResults.Count == 0 ? "None" : string.Join(", ", checks.NumbersNotInResults)],
        ["Outcome", checks.Passed ? "Passed" : "Review the items listed above against the tables below. The tables are authoritative."],
    ];

    /// <summary>The calculations whose tables an answer shows, in the order they ran. Finding the patient is not one of them.</summary>
    public static IEnumerable<ToolCallRecord> Tabled(AnswerRecord r) => r.Calculations.Where(c => c.Name != "find_patient");

    private static string Render(AnswerRecord r)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {(r.QuestionId is null ? "Answer" : r.QuestionId)}\n");
        b.AppendLine($"**Question**: {r.Question}\n");
        b.AppendLine("## Answer\n");
        b.AppendLine($"*{NarrativeBy}*\n");
        b.AppendLine(r.Narrative + "\n");

        b.AppendLine("## Checks on the answer\n");
        b.AppendLine($"*{ChecksBy}*\n");
        b.AppendLine(Markdown.Table(["Check", "Result"], CheckRows(r.Checks)));

        b.AppendLine("## Calculations\n");
        b.AppendLine($"*{CalculationsBy}*\n");
        var number = 0;
        foreach (var c in Tabled(r))
        {
            b.AppendLine($"### {++number}. {c.Name.Replace('_', ' ')}\n");
            b.AppendLine($"Inputs chosen by the model: `{c.Input}`. Ran in {c.ElapsedMs:0.0} ms.\n");
            b.AppendLine(c.Rendered);
        }

        b.AppendLine("## How this answer was produced\n");
        b.AppendLine(Markdown.Table(["Item", "Value"],
        [
            ["Model", r.Model],
            ["Model calls", r.ModelCalls.ToString()],
            ["Tokens", $"{r.InputTokens} input, {r.OutputTokens} output"],
            ["Calculations run", string.Join(", ", r.Calculations.Select(c => c.Name))],
            ["Calculations asked for and not run", r.CalculationsNotRun == 0 ? "None" : $"{r.CalculationsNotRun}. Each gave a date before the list of patients and their episode dates had been returned, so it was held back and asked for again"],
            ["Time", $"{r.LatencyMs} ms"],
            ["Abstraction version", r.AbstractionVersion],
        ]));
        return b.ToString();
    }
}
