# Benchmarks

Run on 2026-09-28 18:26 against `abstraction/abstraction.db`. Machine: Unix, 14 logical processors, .NET 10.0.4.

# Measured

## Initial processing

| Measure | Value |
|---|---|
| Documents | 31 |
| Total time | 129.2 s |
| Registering and hashing | 12 ms |
| Extraction, wall clock | 129.1 s |
| Reconciliation | 79 ms |
| Documents extracted at once | 6 |
| Extraction model | us.anthropic.claude-sonnet-5 |
| Assertions kept | 405 |
| Assertions with line numbers corrected | 0 |
| Assertions rejected | 0 (0 about attendance, presence or the plan) |
| Share of returned assertions whose quote was found | 100.0% |


## Reuse across runs

| Measure | Value |
|---|---|
| Ingesting the same folder again | 6 ms |
| Files recognised as already known | 31 of 31 |
| Documents extracted | 0 |
| Model calls made | 0 |
| Abstraction changed | No |


Rebuilding every patient's events from saved assertions, with no model: median 4.1 ms over 5 runs (1 patient(s)).

## Calculation latency

Each calculation was run 25 times against the saved abstraction. No model is involved.

| Scope | Calculation | Median | Slowest |
|---|---|---|---|
| One patient | Session counts | 0.52 ms | 1.84 ms |
| One patient | Minutes and goals by week | 0.53 ms | 1.64 ms |
| One patient | Day reconstruction | 1.87 ms | 2.88 ms |
| One patient | Symptom measures | 0.53 ms | 1.88 ms |
| One patient | Observations | 1.87 ms | 2.64 ms |
| One patient | Plan change comparison | 0.53 ms | 2.08 ms |
| Whole collection | Consecutive weeks below goal | 0.09 ms | 0.41 ms |


## Question latency

| Question | Time | Served from saved answer |
|---|---|---|
| DEV-01 | 22.6 s | No |
| DEV-02 | 17.6 s | No |
| DEV-03 | 20.0 s | No |
| DEV-04 | 18.5 s | No |
| DEV-05 | 24.4 s | No |
| DEV-01 | 23.0 s | No |
| DEV-02 | 17.6 s | No |
| DEV-03 | 22.2 s | No |
| DEV-04 | 19.3 s | No |
| DEV-05 | 25.2 s | No |
| DEV-01 | 0 ms | Yes |
| DEV-02 | 0 ms | Yes |
| DEV-03 | 0 ms | Yes |
| DEV-04 | 0 ms | Yes |
| DEV-05 | 0 ms | Yes |
| DEV-01 | 25.2 s | No |
| DEV-02 | 16.7 s | No |
| DEV-03 | 19.3 s | No |
| DEV-04 | 23.2 s | No |
| DEV-05 | 27.1 s | No |
| DEV-01 | 23.5 s | No |
| DEV-01 | 21.5 s | No |
| DEV-02 | 20.5 s | No |
| DEV-03 | 18.1 s | No |
| DEV-04 | 20.6 s | No |
| DEV-05 | 29.4 s | No |


Questions answered by the model: 21. Median 21.5 s, slowest 29.4 s. Nearly all of this time is the model; the calculations inside each answer take milliseconds.

## Model usage

Tokens are measured. Cost is the measured tokens priced at Anthropic's list prices; Amazon Bedrock may bill at different rates.

| Purpose | Model | Calls | Input tokens | Cache write | Cache read | Output tokens | Model time | Cost at list price |
|---|---|---|---|---|---|---|---|---|
| Extraction | us.anthropic.claude-sonnet-5 | 35 | 28,231 | 0 | 244,825 | 85,352 | 631.0 s | $0.96 |
| Extraction header | us.anthropic.claude-sonnet-5 | 1 | 792 | 0 | 6,995 | 1,000 | 8.5 s | $0.01 |
| Question | us.anthropic.claude-sonnet-5 | 63 | 271,792 | 14,857 | 221,249 | 35,983 | 454.5 s | $0.98 |


## Size of the saved abstraction

| Measure | Value |
|---|---|
| Database file | 3,350,528 bytes (3272 KB) |
| Source documents | 31 documents, 50,106 bytes |
| Database size per source byte | 66.9 |
| Assertions | 405 |
| Assertions per document | 13.1 |
| Events | 24 |
| Stored weekly results | 4 |
| Patients | 1 |
| Saved answers | 26 |
| Gap log entries | 0 |


The database also stores each document's text and the model's raw output for every extraction, so that any assertion can be checked against its source without the original files.

# Estimated

Nothing in this section was measured at scale. Each figure is the measured per-document figure above multiplied out.

## Per document, from the measured run

| Measure | Value |
|---|---|
| Input tokens at full price | 936 |
| Tokens written to the cache | 0 |
| Tokens read from the cache | 8,123 |
| Output tokens | 2,786 |
| Cost at list price | $0.0314 |
| Model time | 20.6 seconds |
| Wall-clock time at the measured concurrency | 4.2 seconds |


## Extraction at scale

| Documents | Cost at list price | Time, one at a time | Time at 6 at once | Time at 100 at once | Assertions |
|---|---|---|---|---|---|
| 500,000 | $15,676 | 119 days | 24 days | 29 hours | 6,532,258 |
| 1,000,000 | $31,353 | 239 days | 48 days | 2 days | 13,064,516 |


The times assume the provider's rate limits allow that many requests at once, which was not tested. Processing through a batch interface would trade speed for a lower price and was not measured.

Database size at the measured 105.5 KB per document: about 54.0 GB for 500,000 documents and 108.1 GB for 1,000,000. SQLite was not tested at that size.

