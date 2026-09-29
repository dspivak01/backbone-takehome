# Commands

Every command, with an example. `ca help` prints the same list.

## Setup

```bash
dotnet build
alias ca="dotnet run --no-build --project src/ClinicalAbstraction --"
```

Commands that call a model need one of:

| Access | Setup | Tested |
|---|---|---|
| Amazon Bedrock | AWS credentials for the AWS CLI, with access to Claude Sonnet 5 in `us-west-2` | Yes |
| Anthropic API | Set `ANTHROPIC_API_KEY` in the terminal that runs the command. The program then uses it automatically | Yes, once: 3 new documents and one question |

The repository includes the saved abstraction, so every command that needs no model works
straight away.

## The review page

```bash
ca serve
```

Opens at `http://127.0.0.1:5173`. It answers on this machine only.

| Screen | What it does | Calls a model |
|---|---|---|
| Patients | Each patient's weeks, sessions and symptom measures | No |
| Trace | From any number to its contacts, then to each assertion, then to the document lines | No |
| Ask | Ask a question, watch the steps, read the answer with its checks and tables | Yes |
| Documents | Add files, choose a folder, or type a folder's path. Watch each file, open any document | Yes, for new documents |
| Gap log | Passages found by text search, grouped by suggested kind | No |

Ask and Documents write to the database the page is serving. To try them without changing the
submitted abstraction, serve a copy:

```bash
cp abstraction/abstraction.db /tmp/copy.db
ca serve --db /tmp/copy.db --out /tmp/copy-output
```

Files and chosen folders are uploaded: text files ending in `.txt`, 1 MB each, 100 at a time.
They are saved under `<out>/uploads/` with generated names. From a chosen folder, files that are
not text, hidden or empty are left out and named on the screen before anything is sent. A folder
given by its path is read where it is, with no limit. Original files are never changed.

### Known limits of the review page

Two reviews of the page found these and they are not fixed.

| Limit | What to do |
|---|---|
| A question asked while documents are being added can mix results from before and after | Wait for the documents to finish |
| The page keeps figures it has already loaded when the database is changed from elsewhere | Reload the page |
| In a document that names two patients, every source opens the first patient's trace | The document lines shown are right. The weeks and sessions above them are the first patient's |
| A file uploaded with no document identifier is named 001, 002, and the numbers start again with each upload | Give each document a `Document ID:` line |
| A document whose extraction was cut off is described as not in the abstraction, but what was read is counted | Add the file again |
| A numbered list in a written answer can restart at 1 | The tables are unaffected |
| Jobs are kept in memory | After a restart, ask again |
| Tried in one browser only | |

## Commands that call a model

| Command | What it does |
|---|---|
| `ca ingest documents` | Registers every file, extracts the ones whose content is new, rebuilds the patients affected |
| `ca ask "How many group sessions did Rowan Mercer attend?"` | Answers one question from the saved abstraction |
| `ca ask --file questions.json` | Answers a file of questions and saves each to `output/answers/` |
| `ca ask "..." --patient HG-M042` | Answers for one patient, when the question names none |
| `ca ask "..." --no-cache` | Asks the model again even if the same question has a saved answer |

`ingest` reads files ending in `.txt` or with no extension, in every subfolder. It skips hidden
files and anything inside a hidden folder. A file seen before costs nothing, whatever it is
called. A folder has no limit on size or number of files, so name the folder that holds the
documents and nothing wider.

A question that names no patient is answered for every patient who fits what it specifies.

## Commands that never call a model

### Reading the abstraction

| Command | What it shows |
|---|---|
| `ca patients` | The patients in the collection |
| `ca show` | One patient's timeline: plan, encounters, measures, weeks, warnings |
| `ca show --encounter HG-E110` | One encounter with every assertion, every rule applied, and what was overruled |
| `ca source BH-D103 7` | The lines a source points to. A range is written `7-9`. `--context 3` shows more lines around it |
| `ca gaps` | What the gap log holds |

### Calculations

| Command | Result |
|---|---|
| `ca calc sessions --from 2026-01-05 --to 2026-01-30` | Sessions by type, total, and distinct days, with every exclusion explained |
| `ca calc weekly` | Days, minutes and goal result for each week |
| `ca calc consecutive` | Patients with two consecutive weeks below the goal, and those who depend on unresolved documentation |
| `ca calc day --date 2026-01-19` | Everything recorded for one day. With several patients and no `--patient`, every patient with a contact that day |
| `ca calc measures` | Symptom assessments in date order |
| `ca calc observations --category sleep` | Quoted observations |
| `ca calc plan-change` | Care delivered before and after a plan change |
| `ca calc summary` | One row per patient |
| `ca calc patients --from 2026-01-19 --to 2026-01-21` | Patients with a contact in the period |
| `ca calc search --terms parking,late` | Keyword search over a patient's documents |

Add `--json` to print any calculation as JSON.

`calc search` is the one calculation that writes anything: each passage it finds is added to the
gap log.

### Rebuilding, exporting, measuring

| Command | What it does |
|---|---|
| `ca reconcile` | Rebuilds every patient's events from saved assertions. Use after changing a rule |
| `ca export` | Writes the abstraction to `abstraction/export/` as JSON and readable text |
| `ca benchmark documents` | Writes `output/benchmarks.md` |
| `ca evaluate --reference tests/reference/supplied-record.json` | Scores the abstraction against expected results. `--save <file>` keeps the report |
| `dotnet test` | Runs the 120 tests |

## Options

| Option | Meaning | Default |
|---|---|---|
| `--db <file>` | Database file | `abstraction/abstraction.db` |
| `--out <folder>` | Output folder | `output` |
| `--patient <key>` | Patient key, normally the MRN | The only patient, when there is one |
| `--from`, `--to` | Dates as `yyyy-MM-dd`, both included | The patient's episode |
| `--port <number>` | Port for `serve` | 5173 |

## Environment variables

| Variable | Meaning | Default |
|---|---|---|
| `CA_PROVIDER` | `bedrock` or `anthropic` | `anthropic` when `ANTHROPIC_API_KEY` is set, otherwise `bedrock` |
| `CA_EXTRACT_MODEL` | `haiku-4.5`, `sonnet-5`, `opus-5`, or a full model identifier | `sonnet-5` |
| `CA_ANSWER_MODEL` | As above | `sonnet-5` |
| `CA_AWS_REGION` | Bedrock region | `us-west-2` |
| `CA_CONCURRENCY` | Documents extracted at once | 6 |

## Following a number back to its source

From the command line:

```bash
ca calc weekly                    # week of January 19: 180 minutes
ca show --encounter HG-E110       # the January 19 group: 75 minutes, and why
ca source BH-D103 7               # the line in the correction that says so
```

On the review page, select the number.

## Trying the other test patients

Three fictional patients are in `test-data/`. A collection that already holds all four patients
is saved in `output/experiments/four-patients/collection.db`.

```bash
cp output/experiments/four-patients/collection.db /tmp/four.db
ca serve --db /tmp/four.db --out /tmp/four-output
```
