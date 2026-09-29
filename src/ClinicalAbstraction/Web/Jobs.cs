using System.Diagnostics;
using System.Security.Cryptography;

namespace ClinicalAbstraction.Web;

/// <summary>
/// Work that takes longer than a request: adding documents and answering a question. Each job runs
/// in the background and the page asks for its progress once a second. Jobs are kept in memory
/// only, so they are forgotten when the server stops.
/// </summary>
public sealed class JobStore
{
    /// <summary>How many finished jobs are remembered. A running job is never forgotten.</summary>
    public const int Kept = 50;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Job> _jobs = [];
    private readonly List<string> _order = [];

    /// <summary>
    /// Starts a job. With <paramref name="alone"/>, it is refused while another job of the same kind
    /// is running, and <paramref name="refusal"/> says why. A failure is reported as
    /// <paramref name="failure"/> followed by plain words for what went wrong.
    /// </summary>
    public Job Start(string kind, string title, bool alone, string refusal, string failure, Func<Job, Task> work)
    {
        Job job;
        lock (_gate)
        {
            if (alone && _jobs.Values.Any(j => j.Kind == kind && j.Running)) throw new ConflictException(refusal);
            job = new Job(Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant(), kind, title);
            _jobs[job.Id] = job;
            _order.Add(job.Id);
            foreach (var old in _order.Where(id => !_jobs[id].Running).Take(Math.Max(0, _order.Count - Kept)).ToList())
            {
                _order.Remove(old);
                _jobs.Remove(old);
            }
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await work(job);
                job.Finish();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"{failure} {e}");
                job.Fail($"{failure} {ModelAccess.Describe(e)}");
            }
        });
        return job;
    }

    public Job? Find(string id)
    {
        lock (_gate) return _jobs.GetValueOrDefault(id);
    }

    public bool AnyRunning(string kind)
    {
        lock (_gate) return _jobs.Values.Any(j => j.Kind == kind && j.Running);
    }
}

/// <summary>One job's progress. Everything in it is written by this program; nothing is passed through from a provider.</summary>
public sealed class Job(string id, string kind, string title)
{
    private readonly Lock _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<(JobFileView View, string? Path, string? Hash)> _files = [];
    private readonly List<JobStepView> _steps = [];
    private readonly List<string> _summary = [];
    private readonly List<string> _warnings = [];
    private string _stage = "Starting.";
    private string? _error;
    private long? _answerId;
    private bool? _fromSaved;
    private bool _running = true;

    public string Id { get; } = id;
    public string Kind { get; } = kind;

    /// <summary>What the job is doing, in words: the question, or the files or folder being added.</summary>
    public string Title { get; } = title;

    public bool Running
    {
        get { lock (_gate) return _running; }
    }

    public void Stage(string text)
    {
        lock (_gate) _stage = text;
    }

    public void AddStep(JobStepView step)
    {
        lock (_gate) _steps.Add(step);
    }

    /// <summary>Adds a row for a file, or updates the row already there for that path.</summary>
    public void File(string path, string? hash, string name, string status, string tone, string detail)
    {
        lock (_gate)
        {
            var at = _files.FindIndex(f => f.Path == path);
            var view = new JobFileView { Name = name, Status = status, Tone = tone, Detail = detail, Document = hash };
            if (at >= 0) _files[at] = (view, path, hash);
            else _files.Add((view, path, hash));
        }
    }

    /// <summary>Updates every row of this job for one document, whichever path it was first seen at.</summary>
    public void Document(string hash, string status, string tone, string detail)
    {
        lock (_gate)
            for (var i = 0; i < _files.Count; i++)
                if (_files[i].Hash == hash)
                    _files[i] = (new JobFileView { Name = _files[i].View.Name, Status = status, Tone = tone, Detail = detail, Document = hash }, _files[i].Path, hash);
    }

    /// <summary>Marks every row still waiting as not read, when the job stops before reaching it.</summary>
    public void Unfinished(string detail)
    {
        lock (_gate)
            for (var i = 0; i < _files.Count; i++)
                if (_files[i].View.Status is "New" or "Extracting")
                    _files[i] = (new JobFileView { Name = _files[i].View.Name, Status = "Failed", Tone = "not-met", Detail = detail, Document = _files[i].Hash }, _files[i].Path, _files[i].Hash);
    }

    public void Summary(IEnumerable<string> lines, IEnumerable<string> warnings)
    {
        lock (_gate)
        {
            _summary.AddRange(lines);
            _warnings.AddRange(warnings);
        }
    }

    public void Answered(long answerId, bool fromSaved)
    {
        lock (_gate) (_answerId, _fromSaved) = (answerId, fromSaved);
    }

    public void Finish()
    {
        lock (_gate)
        {
            _running = false;
            _stage = "Finished.";
            _clock.Stop();
        }
    }

    public void Fail(string message)
    {
        lock (_gate)
        {
            _running = false;
            _error = message;
            _stage = "Stopped.";
            _clock.Stop();
        }
    }

    public JobView View()
    {
        lock (_gate)
        {
            var seconds = (int)_clock.Elapsed.TotalSeconds;
            var time = seconds == 1 ? "1 second" : $"{seconds} seconds";
            return new JobView
            {
                Id = Id, Kind = Kind, Title = Title, Running = _running, Failed = _error is not null,
                State = _running ? "Running" : _error is null ? "Finished" : "Failed",
                Stage = _stage, ElapsedSeconds = seconds,
                Elapsed = _running ? $"Running for {time}." : $"Took {time}.",
                Files = _files.Select(f => f.View).ToList(), Calculations = [.. _steps], Error = _error,
                AnswerId = _answerId, FromSaved = _fromSaved, Summary = [.. _summary], Warnings = [.. _warnings],
            };
        }
    }
}
