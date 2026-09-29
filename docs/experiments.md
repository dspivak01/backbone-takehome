# Experiments

What I tested, what happened, and how far each result can be trusted. Every figure here was
measured unless it says otherwise. The files behind each experiment are in `output/experiments/`.

## 1. Which model extracts

I ran the 31 supplied documents through three models, twice each, and scored every run against
a reference worked by hand (`tests/reference/supplied-record.json`, 36 checks).

| Model | Run | Checks passed | Assertions kept | Rejected | Extraction time | Cost at list price |
|---|---|---|---|---|---|---|
| Haiku 4.5 | 1 | 28 of 36 | 289 | 7 | 66.6 s | $0.38 |
| Haiku 4.5 | 2 | 31 of 36 | 275 | 5 | 71.1 s | $0.34 |
| Sonnet 5 | 1 | 36 of 36 | 405 | 0 | 117.0 s | $0.87 |
| Sonnet 5 | 2 | 36 of 36 | 405 | 1 | 121.8 s | $0.91 |
| Opus 5 | 1 | 36 of 36 | 384 | 0 | 149.2 s | $2.34 |
| Opus 5 | 2 | 36 of 36 | 374 | 0 | 141.0 s | $2.21 |

These scores use the final rules, replayed over each run's saved assertions.

### What I learned

- **Sonnet 5 matched Opus 5 on this record at about 40% of the cost.** It is the default.
- **Haiku 4.5 lost break and presence assertions**, which turned settled weeks into undetermined ones.
- **The first scoring was lower for every model.** Sonnet 5 scored 34 and 33, and Opus 5 scored
  34 and 34. The misses were in my rules. A note that listed planned services created a phantom
  plan version, and an outreach call was treated as a possible session. I fixed the rules and
  replayed them over the saved assertions without calling a model again.
- **Saving assertions made rule changes cheap.** Replaying six runs took seconds and cost nothing.

### Limits

- One patient, 36 checks, two runs per model.
- The three models ran at the same time, so the timings may reflect shared rate limits.
- I tuned the rules while looking at these outputs. The scores are not a held-out measure.
- I debugged mostly against Sonnet's output, which favours Sonnet in the comparison.
- Opus ran 62 document extractions in total, too few to compare reliability.

Files: `output/experiments/model-comparison/`. The first scoring is in `first-scoring/`.

## 2. The model's output varies between runs

The models I used do not accept a temperature setting, so two runs over the same document can differ.

In one full run with Sonnet 5, the model returned the list for one document as a string, and one
object in it contained stray text: `"encounter_id":"HG-E118".replace? "HG-E113"`. The string did
not parse. The extractor accepted zero assertions from the document and recorded the extraction
as complete. The document was the final attendance register, so three encounters were wrong and
the score fell to 29 of 36.

The scoring caught it. The pipeline had not.

I saw a malformed result twice in about 200 document extractions with Sonnet 5.

### What I changed

- A document that yields no assertions is recorded as failed and retried, up to three attempts.
- A list returned as a string is read one object at a time, so one malformed object costs that
  object and not the document.
- A failed or incomplete document adds a warning to its patient. The warning appears in every
  answer about that patient, and the document is retried on the next ingest.

### What this does not solve

A run can still drop or alter one assertion without failing any check.

### How I would investigate next

1. Extract every document five times and measure how often each assertion appears in all five,
   by kind and by field.
2. For the fields that decide minutes, extract twice and accept a value only when both runs
   agree, marking the rest for review.
3. Test whether a separate, narrow prompt for attendance rows varies less than the single prompt
   that also extracts narrative observations.

Files: `output/experiments/silent-extraction-failure/`.

## 3. Documents the system had not seen

The supplied record has one patient and one treatment plan. I wrote three fictional patients to
test what it cannot. Each uses a layout and identifier scheme of its own.

| Patient | What it tests | Scored against a reference | Rules changed after seeing the result |
|---|---|---|---|
| Jordan Avery | A plan change, a service the first plan does not mention, a session with no identifier, a duplicate file with different line endings | 23 of 23 | One. The first result was 20 of 23: one miss was a fault in my rules, and two were mistakes in my reference |
| Imani Castell | Sessions on the same two dates as the supplied patient | Checked by hand, no reference file | None |
| Tomas Lindqvist | A patient whose inclusion in the consecutive-weeks list depends on unresolved documentation | 16 of 16 | None |

Tomas Lindqvist's result is the closest to a held-out measure. It was scored on its first run
and nothing was changed afterwards.

### What the first patient showed

| Test | Result |
|---|---|
| Duplicate with different line endings | Recognised. 5 of 6 files extracted |
| Session with no identifier | Formed its own encounter and counted |
| Family therapy under a plan that does not mention it | Shown as 0 to 45 minutes. The week is still met because the minimum meets the goal |
| Plan change | Two versions. Each contact judged by the plan in effect on its date |
| Adding this patient to the collection | Only the 5 new documents were extracted. The supplied patient's results were unchanged |

### The collection-wide question

Asked against four patients: "Which patients had two consecutive weeks below their treatment
plan's requirements, and which patients' inclusion depends on unresolved documentation?"

| Patient | Result |
|---|---|
| Rowan Mercer | Included. Weeks of January 5 and 12 |
| Jordan Avery | Included. Weeks of February 9 and 16, across a plan change |
| Tomas Lindqvist | Depends on unresolved documentation. Would be settled by a correction or an arrival record for session S-304 |
| Imani Castell | Not included |

The answer took 10.9 seconds and passed its checks.

### Limits

I wrote these documents with the same tools that built the system. Documents from someone else
are a stronger test.

Files: `test-data/`, `output/experiments/two-patients/`, `three-patients/` and `four-patients/`.

## 4. Questions that name no patient

Two of the five development questions name no patient. On a two-patient collection, before any
rule existed, the model behaved inconsistently:

| Question | What the model did |
|---|---|
| DEV-04, which mentions January 19 and 21 | Inferred the patient from the dates and answered |
| DEV-05, which mentions January 19 | Declined to guess and asked which patient |

I replaced this with one rule: a question that names no patient applies to every patient who
fits what it specifies, and code decides who fits.

On a three-patient collection, each question was asked twice:

| Question | Patients covered | Same both times |
|---|---|---|
| DEV-04 | Two. States that the third has no contact on those dates | Yes |
| DEV-05 | Two | Yes |
| "How many therapy sessions were attended, by service type?" | All three | Yes |

Two faults appeared while building this. The model added two patients' figures together, which
the number check caught. It also guessed the year 2024 before looking at the collection, which
is now prevented in code.

### Limits

- Two runs of three questions.
- Nothing in code checks that the written answer has a section for every patient listed.
- The year of a date given without one is chosen by the model from the episode dates it is shown.
- The limit of 10 patients was tested on made-up patients only.

## 5. The gap log

When no calculation holds what a question asks for, the model can search a patient's documents
by keyword. Each passage found is written to a gap log with the question and a suggested kind.

I asked which clinicians delivered the sessions and whether the treating clinician changed. No
calculation exposes clinicians, so the question fell back to search.

| Measure | Result |
|---|---|
| Passages written to the gap log | 76 |
| Under the suggested kind `clinician_identity` | 53 |
| Time to answer | 58.6 seconds |
| Finding | The individual therapist and the group facilitator both changed around January 19 |

The answer labelled these findings as coming from text search.

### Closing a gap

| Gap | What it needs |
|---|---|
| Clinician per session | A calculation. The extractor already records clinicians, so no new extraction |
| Reason for a missed session | A new field, a new extractor version, and a re-run of only the documents that already have an attendance assertion other than full attendance |

The system can quote the reasons for missed sessions today, because they are captured as
observations. It cannot count missed sessions by reason across patients, because the reason is
not a field. I left it out so that the example is real.

Files: `output/experiments/gap-log/`.

## 6. A development answer that failed its checks

After the instructions changed, DEV-01 was answered again. The model wrote "90 min block less
15 min break", doing its own arithmetic. No calculation had produced 90, so the number check
reported it. Asked once more, the answer passed, and that is the saved answer.

This is the design working as intended. The written answer can be wrong, the check reports it,
and the tables stay correct.

Files: `output/experiments/development-questions-rerun/`.

## 7. The Anthropic API path

Everything above was run through Amazon Bedrock. Before submitting, I ran one check through the
Anthropic API, on a copy of the saved abstraction.

| Step | Result |
|---|---|
| Ingest of 3 new documents for a patient the copy had not seen | 15 assertions kept, none rejected, 21 s, $0.06 |
| Sessions for that patient, compared with the same documents extracted through Bedrock | The same: 1 group, 1 individual, 2 days |
| One question about that patient | Answered in 7.3 s. Passed its checks |

Two things I saw while doing it:

- **With no credit on the account**, every document was marked failed, nothing was saved from
  them, and the next ingest tried them again. The message shown is the provider's own.
- **A question asked from a second terminal while the ingest was running** was answered from the
  abstraction as it stood before the ingest, and said the patient was not found. That was correct
  at that moment. It is the first limit listed for the review page in `docs/commands.md`, and it
  applies to the command line too.

### Limits

Three documents and one question. The full build of 31 documents was not repeated on this path.

## 8. A rule added after testing with further documents

After the experiments above, I added 50 more fictional documents through the review page: new
documents for the four patients already in the collection, and five new patients. I had worked
out the right answer for each by hand first.

Most behaved as expected, including a correction, a disagreement settled by a desk log, a plan
change with an extended episode, weeks counted Sunday to Saturday, and a roster naming two
patients. One did not.

### What went wrong

A group attendance sheet said, once, that every meeting has a break from 13:40 to 13:55. The
patient attended two meetings, booked 13:00 to 14:30.

| | Minutes for each meeting |
|---|---|
| Right answer | 75 |
| System | 90 |

| Step | What happened |
|---|---|
| Extraction | Correct. The break was recorded with its times |
| Linking | The break named no session and no date, so it was left out |
| Reporting | The patient's page listed it as left out of the counts. Each meeting also said that no break was documented, which was untrue |

The same kind of sheet for another patient came out right, because the model recorded the break
once for each meeting. When I ran the same files a second time, it recorded the break once for
both patients, and both were wrong. The outcome depended on the run.

### What I changed

A break that names no session and no date is now taken off every group session that the same
document describes and that the break falls inside. The rule is in code, so it gives the same
result whichever way the model records the break.

| Check | Result |
|---|---|
| The patient above | 125, 50 and 120 minutes by week, as worked by hand. Before: 140, 50, 135 |
| The eight other patients in that collection | Unchanged |
| The submitted abstraction, rebuilt with the rule | Unchanged. The same version, `8d999c6cbe5f02b7`, and 36 of 36 |
| Tests added | Four, in `ReconciliationTests.cs` |

### Limits

- The rule covers breaks only. A lost connection stated the same way is not covered.
- A break stated once in a document that describes both morning and afternoon groups is applied
  only to the sessions it falls inside.
- I found this by writing documents that state a break differently from the supplied ones. Other
  ways of stating one may exist that I did not think of.
- These 50 documents are not in the repository.
