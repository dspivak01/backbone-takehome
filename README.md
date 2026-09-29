# Clinical abstraction prototype

This program reads clinical documents, saves an account of the care each patient received, and
answers questions from that account. Code calculates every number. Every conclusion can be
followed back to the document lines that support it.

On the 31 supplied documents it matches a reference worked by hand on 36 of 36 checks.

| Week | Therapy days | Minutes | Goal: 3 days and 150 minutes |
|---|---|---|---|
| Jan 5 to 11 | 3 | 140 | Not met |
| Jan 12 to 18 | 2 | 120 | Not met |
| Jan 19 to 25 | 3 | 180 | Met |
| Jan 26 to Feb 1 | 3 | 145 to 155 | Cannot be determined |

The last week is a range because two signed notes give different start times for the January 26
session and no record settles it.

## Start here

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet build
alias ca="dotnet run --no-build --project src/ClinicalAbstraction --"
ca serve
```

Open `http://127.0.0.1:5173`. Select any number to follow it to its contacts, its assertions, and
the document lines.

| To see | Look at |
|---|---|
| Answers to the five development questions | `output/answers/DEV-01.md` to `DEV-05.md` |
| The saved abstraction | `abstraction/abstraction.db`, and as text in `abstraction/export/` |
| One encounter with all its evidence | `ca show --encounter HG-E110` |
| Every command | `docs/commands.md` |

The saved abstraction is included, so everything except asking a new question and adding
documents works with no model access. Those two call a model. See "Model access" below.

```bash
ca ask "How many group sessions did Rowan Mercer attend?"
ca ingest <folder>
```

**Adding documents.** Use `ca ingest <folder>` or the Documents screen. Only content not seen
before is sent to the model, and only the patients it names are rebuilt. Everything else in the
abstraction is left as it was.

**Starting from nothing.** Any command given a database file that does not exist creates it.
This rebuilds the abstraction from the supplied documents in about 2 minutes, for about $1:

```bash
ca ingest documents --db fresh.db --out fresh-output
ca serve --db fresh.db --out fresh-output
```

The model's output varies between runs, so a fresh build can differ slightly from the saved one.

### Model access

Use either of these. If an Anthropic API key is set, the program uses it. Otherwise it uses
Amazon Bedrock.

**Anthropic API**

1. Create a key at `console.anthropic.com`, under API keys. The account needs credit.
2. Set the key in the terminal you will run the commands from.

   macOS or Linux:

   ```bash
   export ANTHROPIC_API_KEY=sk-ant-your-key
   ```

   Windows PowerShell:

   ```powershell
   $env:ANTHROPIC_API_KEY = "sk-ant-your-key"
   ```

3. Run the commands in that same terminal. For the review page, set the key before `ca serve`.

The key lasts until that terminal is closed. A new terminal does not have it.

**Amazon Bedrock**

Configure AWS credentials as for the AWS CLI, with access to Claude Sonnet 5 in `us-west-2`.
Nothing else needs setting. `CA_AWS_REGION` changes the region.

**Which one was used**

| Where | What it shows |
|---|---|
| The last table of every answer | `claude-sonnet-5` for the Anthropic API, `us.anthropic.claude-sonnet-5` for Bedrock |
| The foot of every screen of the review page | The provider and the models |

To use Bedrock while a key is set, also set `CA_PROVIDER=bedrock`.

## How it works

| Stage | What it does | Model |
|---|---|---|
| Ingest | Hashes each file's text. Content seen before is skipped | No |
| Extract | Turns one document into assertions, each with a quote and line numbers | One call per new document |
| Verify | Keeps an assertion only if its quote is found in the document | No |
| Reconcile | Seven rules turn assertions into events | No |
| Calculate | Counts, sums by week, checks goals | No |
| Answer | The model picks calculations and writes the answer. Code checks its sources and numbers, and appends the tables | Yes |

An **assertion** is what one document says. It is never edited. An **event** is what the system
concludes happened, and is rebuilt from assertions by rules in code.

- **Conflicts.** Signed clinical records outrank exports and logs. Drafts and billing entries are
  never evidence of attendance. When two signed records disagree, the value stays unresolved and
  the minutes become a range.
- **Time.** A later document does not override an earlier one. Time is used in two rules only: a
  correction must come after what it corrects, and a record of attendance written before the
  session began is set aside.
- **The treatment plan is data.** Goals and counted services are extracted with their dates. A
  plan change is a new row.
- **Questions never change the abstraction.** Results cannot depend on what was asked before.

Detail: `docs/approach.md`.

## What the abstraction holds

### Assertions

An assertion is one statement from one document. Every assertion carries the document, the line
range, the exact quote, the kind of record it came from, whether that record is signed, who
signed it and when, and whether it is a copy of an earlier record.

There are 13 kinds. The model chooses the kind from this fixed list and cannot add to it. The
count is how many the 31 supplied documents produced, 405 in all.

| Kind | What it states | A quote from the supplied documents | Used for | Count |
|---|---|---|---|---|
| `encounter` | That a service was booked or held: type, date, booked times, clinicians | "Family psychotherapy \| Encounter HG-E119" | The event's service type and session times | 39 |
| `attendance` | What happened to the appointment, and whether the patient took part | "Rowan was present from opening through closing." | Whether the event is a session | 41 |
| `presence` | When the patient was in contact | "10:00 \| 11:30 \| Attended" | Minutes | 21 |
| `excluded_interval` | A break, or a lost connection | "Nontherapeutic break 10:45–11:00." | Taken off the minutes | 7 |
| `stated_duration` | A duration the document gives in minutes | "Total patient psychotherapy contact: 45 minutes." | Checked against the minutes worked out from times. Used when no times are given | 12 |
| `correction` | A change to one value in an earlier record | "Patient departure for HG-E110 is 11:15, replacing the original roster value of 11:30." | Replaces that value in earlier assertions | 1 |
| `no_service_contact` | That something did not take place | "No patient treatment contact occurred." | Explains exclusions. Changes no count | 23 |
| `plan_requirement` | The treatment plan's goal, what counts, and the dates it applies | "Patient-present individual, group, and family therapy contribute to the minute goal." | Plan versions and the episode | 6 |
| `measure` | A symptom score | "PHQ-9 completed by Rowan on 2026-01-05: total score 18." | Symptom assessments | 6 |
| `observation` | A clinical statement about symptoms, functioning, safety, or the reason for a contact | "Sleep was still variable." | Questions about the patient's course | 185 |
| `authorization` | An approval of services | "Authorized quantity: 8 group sessions." | Stored. No calculation uses it | 1 |
| `charge` | A billing entry | "Charge ID: CH-116 \| Encounter: HG-E116" | Compared with the event and reported. Never evidence | 1 |
| `other` | A fact that fits none of the above, with a name the model proposes | "Attendance is recorded on the group desk roster." | Stored and searchable. No rule uses it | 62 |

### Events

An event is what the system concludes. Rules in code build events from assertions, one patient
at a time. There are three types.

| Event | One per | What it holds | Built from |
|---|---|---|---|
| Encounter | Booked or delivered contact | Service type, outcome, whether the patient was present, start and end of presence, breaks, minutes as a minimum and maximum | `encounter`, `attendance`, `presence`, `excluded_interval`, `stated_duration`, `correction`, `charge` |
| Symptom assessment | Questionnaire completed | Instrument, score, date. Copies and later mentions are attached to it, not counted again | `measure` |
| Plan version | Period a goal applies | Minimum days and minutes a week, which services count and which do not, how the week is counted | `plan_requirement` |

Each field of an encounter records how it was decided:

| Decision | Meaning |
|---|---|
| Resolved | The most authoritative records agree |
| Resolved by corroboration | Two signed records disagree, and exactly one lower-ranked record supports one of them |
| Unresolved | Signed records disagree and nothing settles it. Every value is kept, with what would settle it |
| Missing | No record states it |

An encounter's outcome is one of session, not a session, or uncertain. Assertions that were
overruled stay on the event with the rule that overruled them.

Whether an encounter counts toward the goal is not stored on the event. It is decided when a
question is asked, by the plan version in effect on the encounter's date.

### Service types

Documents name services in many ways. The model maps each to one of eight fixed types, and code
compares only these.

| Type | Meaning |
|---|---|
| Individual, group, family therapy | Psychotherapy with the patient alone, in a group, or with family |
| Collateral contact | A contact held with a partner or family member |
| Medication management | A prescriber visit |
| Care coordination | Contact between professionals about the patient |
| Administrative | Scheduling, reminders, notices. Never counted, whatever a plan says |
| Other | Any other service. Counted only if the plan says so |

## What I tested

**Design decision: which model extracts.** I ran the supplied documents through three models,
twice each, and scored each run against the reference.

| Model | Checks passed, of 36 | Cost at list price |
|---|---|---|
| Haiku 4.5 | 28 and 31 | $0.38 and $0.34 |
| Sonnet 5 | 36 and 36 | $0.87 and $0.91 |
| Opus 5 | 36 and 36 | $2.34 and $2.21 |

Sonnet 5 matched Opus 5 at about 40% of the cost, so it is the default. On first scoring Sonnet
got 34 and 33. The misses were in my rules. Because assertions are saved, I fixed the rules and
replayed them without calling a model.

| Other checks | Result |
|---|---|
| Automated tests | 124 pass |
| Three fictional patients I wrote, with other layouts, a plan change, and an unresolved week | 23 of 23 and 16 of 16 against their references. The third was checked by hand |
| Every document ingested twice | Each registered once. No result changed |
| Assertions processed in three different orders | Identical abstraction |

## What I found

**The model's output varies between runs.** In one run the model returned a malformed list for
the final attendance register. The extractor kept nothing from it and recorded the extraction as
complete. The score fell to 29 of 36. My scoring caught it. The pipeline had not.

A document that yields nothing is now marked failed, retried, and reported in every answer about
that patient. A run can still drop one assertion without failing a check. I would measure this
next by extracting each document five times and comparing.

In one run, the written answer to DEV-01 contained arithmetic the model did itself. The number
check reported it, and I asked again. The tables are written by code and were correct both times.

Detail: `docs/experiments.md`.

## Scale

| Measured on 31 documents | Result |
|---|---|
| Initial processing | 129 s. All but 0.1 s is the model |
| Same folder again | 6 ms, no model calls |
| A calculation | 0.1 to 1.9 ms |
| A question | 18 to 29 s. Under 1 ms when asked again |
| Extraction cost | $0.97, or $0.031 per document |

**Estimated, not tested:** one million documents would cost about $31,400 to extract and take
about 2 days at 100 requests at once.

**The first bottleneck is model extraction**, in `Extractor.ExtractAsync`: one call per document,
six at once, about 2,800 output tokens each. Output tokens are 89% of the cost, and 61% of
assertions are narrative observations that no count depends on. I would extract attendance
first with a narrow prompt, and observations only when a question needs them.

Detail: `docs/performance.md`.

## What's not done

| Item | State |
|---|---|
| The reference scores | I tuned the rules while looking at scored output. Only the 16 of 16 result was scored once and left alone |
| Anthropic API path | Run once, with 3 new documents and one question. Everything else here was built through Bedrock |
| One document that names several patients | Tried once, with a roster naming two. Both were rebuilt. On the review page, its sources open the first patient's trace |
| Meaning of the written answer | Code checks that sources and numbers exist. It cannot check that a sentence says what its source says |
| Corrections to a break | Detected and reported, not applied |
| Time zones and sessions that cross midnight | Not handled |
| Review page | Tested in one browser. Jobs are lost on restart. A question asked while documents are being added can mix old and new results. A file uploaded with no document identifier is named 001, 002. The full list is in `docs/commands.md` |

## Models, cost, and assistance

| Item | Value |
|---|---|
| Model | Claude Sonnet 5 (`us.anthropic.claude-sonnet-5`) on Amazon Bedrock, for extraction and answers |
| Settings | Extraction forces one tool call with a JSON schema. Instructions are cached. These models accept no temperature setting. Maximum 16,000 output tokens |
| Submitted run | 129 s and $0.97 to process the documents. 110 s and $0.26 to answer the five questions |
| All development | About $14 at list price, from 548 recorded model calls |

**Coding assistance.** I built this with Claude Code, using Claude Fable 5.1. It wrote the code,
the tests, the reference answers used for scoring, and the first draft of these documents.
Separate agents running Claude Opus built the review page and reviewed the design and the code.
I set the design, and reviewed each decision the assistant made on its own during the build.

## Where to find more

| File | Content |
|---|---|
| `docs/approach.md` | Every rule, with the reason for it |
| `docs/experiments.md` | Each experiment, its result, and its limits |
| `docs/performance.md` | Measured results and estimates, kept apart |
| `docs/commands.md` | Every command, with an example |
