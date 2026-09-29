# Performance

Measured results first, then estimates. The two are kept apart throughout.

The measured figures are from the run of 2026-09-28 on a laptop with 14 logical processors. The
full output is in `output/benchmarks.md`. To produce it again:

```bash
ca benchmark documents
```

# Measured

## Initial processing

| Measure | Result |
|---|---|
| Documents | 31 |
| Total time | 129.2 s |
| Registering and hashing | 12 ms |
| Extraction | 129.1 s |
| Reconciliation | 79 ms |
| Documents extracted at once | 6 |
| Assertions kept | 405 |
| Assertions rejected | 0 |
| Quotes found at the cited lines | 100% |

Extraction is more than 99.9% of the time.

## Reuse

| Measure | Result |
|---|---|
| Ingesting the same folder again | 6 ms, 0 model calls, abstraction unchanged |
| Rebuilding every event from saved assertions | 4.1 ms |
| A question asked a second time | Under 1 ms, from the saved answer |
| Adding a second patient's 5 documents to the collection | 5 documents extracted. The first patient's results unchanged |

### How work is reused

| Across | How |
|---|---|
| Runs | A document is identified by a hash of its text. A file seen before costs nothing, whatever it is called. Line endings and trailing spaces do not make a new document |
| Documents | The instructions and schema are the same for every document and are cached by the provider. About 8,100 of 9,000 input tokens per document were read from the cache |
| Questions | Every question reads the same saved events. Weekly results are stored when a patient is reconciled |
| Rule changes | Assertions are saved, so rules can be changed and replayed without a model |

## Calculations

Each was run 25 times against the saved abstraction. No model is involved.

| Scope | Calculation | Median | Slowest |
|---|---|---|---|
| One patient | Session counts | 0.52 ms | 1.84 ms |
| One patient | Minutes and goals by week | 0.53 ms | 1.64 ms |
| One patient | Day reconstruction | 1.87 ms | 2.88 ms |
| One patient | Symptom measures | 0.53 ms | 1.88 ms |
| One patient | Observations | 1.87 ms | 2.64 ms |
| One patient | Plan change comparison | 0.53 ms | 2.08 ms |
| Whole collection | Consecutive weeks below goal | 0.09 ms | 0.41 ms |

The collection held one patient when these were measured.

## Questions

| Question | Time |
|---|---|
| One patient, five development questions, final run | 18.1 s to 29.4 s, 110 s for all five |
| The same five questions over every run, 21 answers | Median 21.5 s, slowest 29.4 s |
| Whole collection, four patients | 10.9 s |
| One that fell back to text search | 58.6 s |

Nearly all of this time is the model. The calculations inside each answer take milliseconds.

## Model usage

Tokens are measured. Cost applies Anthropic's list prices to them. Amazon Bedrock may bill differently.

| Purpose | Calls | Input tokens | Read from cache | Output tokens | Cost at list price |
|---|---|---|---|---|---|
| Extraction, 31 documents | 36 | 29,023 | 251,820 | 86,352 | $0.97 |
| Five questions, final run | 15 | 72,616 | 61,838 | 8,814 | $0.26 |

Output tokens are 89% of the cost of extraction.

## Size

| Part | Bytes |
|---|---|
| Assertions | 266,429 |
| Events and patient abstraction | 162,331 |
| The model's raw output, kept for audit | 175,059 |
| Document text, kept for showing sources | 49,931 |
| Source documents, for comparison | 50,106 |

Assertions and events together are about 13.8 KB per document. With raw output and document
text, about 21 KB per document.

The database file is 3,350,528 bytes. It also holds 26 saved answers and the record of every
model call, which are the largest part of it. `output/benchmarks.md` divides the whole file by
31 documents and reports 105.5 KB per document and 108 GB at a million. That overstates growth,
because saved answers grow with questions asked and not with documents. The 21 KB figure is the
one I would plan with.

## What the assertions are

| Kind | Share |
|---|---|
| Observations | 46% |
| Other | 15% |
| Encounters, attendance, presence, breaks, durations | 30% |
| Assertions that no service occurred | 6% |
| Plan, measures, corrections, charges, authorizations | 4% |

Only the third row decides counts and minutes.

# Estimated

Nothing below was tested at scale. Each figure is a measured per-document figure multiplied out.

## Per document

| Measure | Value |
|---|---|
| Output tokens | 2,786 |
| Cost at list price | $0.031 |
| Model time | 20.6 s |
| Time at 6 documents at once | 4.2 s |

## Extraction at scale

| Documents | Cost at list price | Time at 6 at once | Time at 100 at once | Assertions |
|---|---|---|---|---|
| 500,000 | about $15,700 | about 24 days | about 29 hours | about 6.5 million |
| 1,000,000 | about $31,400 | about 48 days | about 2 days | about 13 million |

The times assume the provider's rate limits allow that many requests at once. I did not test that.

## The first bottleneck at a million documents

Model extraction, in throughput and in cost.

The constraint is in `Extractor.ExtractAsync`, called by `IngestPipeline.RunAsync`: one model
call per document, about 2,800 output tokens each, with a fixed limit of six at once.

What I would change, in order:

1. **Cut output tokens.** 61% of assertions are observations and `other` notes. Attendance and
   presence could be extracted first with a narrow prompt, and observations only for documents
   a question needs.
2. **Use a batch interface** for the first load, which trades speed for price. Not measured.
3. **Replace the fixed limit of six** with a queue that follows the provider's rate limits.
4. **Parse known tables in code**, keeping the model for free text.

## What becomes slow after that

| Constraint | Where | Change |
|---|---|---|
| One writer at a time | `Storage/Database.cs` holds one SQLite connection behind a lock | PostgreSQL, with a JSONB column for the same design |
| Whole-patient rebuild | `ReconcileService` reloads all of a patient's assertions | Fine at tens of documents per patient. Very long records would need partial rebuild |
| Collection questions with dates | `Calculations.ConsecutiveWeeks`, `CollectionSummary` and `DayAcrossPatients` load each patient who fits | Store per-patient results by week and read rows |
| Text search | `Calculations.Search` scans a patient's documents line by line | A full-text index |
| Database size | About 21 KB per document, so about 21 GB at a million | Move raw output and document text to object storage |
| Re-extraction after the extractor changes | Every affected document is a model call | Re-run only documents whose saved assertions show they could hold the new field, with a narrow prompt |
