# Approach

How the system turns documents into answers, and why it is built this way.

## 1. Two kinds of record

| | Assertion | Event |
|---|---|---|
| Meaning | What one document says | What the system concludes happened |
| Created by | A model, one document at a time | Rules in code, one patient at a time |
| Can conflict with others | Yes | No. Conflicts are resolved or marked unresolved |
| Changes after creation | Never | Rebuilt whenever the patient gets new assertions |

I avoided the word "claim" because in healthcare it means a billing submission.

Keeping the two apart is what makes the system auditable. An assertion that was overruled is
still stored, with the rule that overruled it.

## 2. Pipeline

| Stage | What it does | Model | Code |
|---|---|---|---|
| Ingest | Hashes each file's text and skips content already seen | No | `Ingest/DocumentRegistry.cs` |
| Extract | Turns a document into assertions, each with line numbers and a quote | One call per new document | `Extraction/Extractor.cs` |
| Verify | Keeps an assertion only if its quote is found in the document | No | `Extraction/QuoteVerifier.cs` |
| Reconcile | Applies the rules in section 4 to produce events | No | `Reconciliation/` |
| Calculate | Counts, sums by week, checks goals | No | `Calculation/` |
| Answer | The model picks calculations and writes prose. Code checks the prose and appends the tables | Two to seven calls per question | `Questions/` |

## 3. Extraction

The model is given the document with line numbers, a fixed schema, and instructions that
contain nothing about any patient or document. The main instructions are:

- Record only what the document states.
- Copy times and numbers. Never calculate.
- Quote exactly, with a line range.
- Describe the source of an assertion without judging it. A draft that says "attended" is
  recorded as attended, from an unsigned draft.
- A copy keeps the signing time of the record it copies.

The model returns intervals, such as arrival 10:15 and a break from 10:45 to 11:00. Code does
the arithmetic.

Service types come from a fixed list: individual therapy, group therapy, family therapy,
collateral contact, medication management, care coordination, administrative, other. The
documents call one service "Coping skills group", "Skills group" and "Group psychotherapy".
Code compares the fixed value, never the printed label.

### What code checks

| Check | On failure |
|---|---|
| The kind is one of the 13 known kinds | The assertion is rejected |
| The quote appears at the cited lines | The line numbers are corrected if the quote is elsewhere. Otherwise the assertion is rejected |
| Field values are in the fixed lists | The value is dropped |
| The document produced at least one assertion | The extraction is marked failed and retried, up to three attempts |
| The patient is identified | A short second request asks for it |

Dashes, quotation marks and spacing are normalised before quotes are compared, because a model
often returns a hyphen where the document has an en dash.

## 4. Reconciliation

### Tiers

| Tier | Source | Treated as |
|---|---|---|
| 1 | Signed clinical note, signed attendance record, signed correction | Evidence of what happened |
| 2 | Appointment export, desk log, platform connection log | Evidence, used when tier 1 is silent |
| 3 | Unsigned draft, billing charge | Never evidence of what happened. Compared and reported |

The model states the record type and whether it is signed. Code derives the tier.

### Rules

| Rule | What it does |
|---|---|
| R1 Link | Groups assertions by encounter identifier. Without one, an assertion joins the single encounter that matches its date and service type, or starts an encounter of its own. One that fits several is left out and reported |
| R2 Copies | A copy takes the signing time of the record it copies, so a stale copy received later adds nothing |
| R3 Corrections | A correction replaces one named field, and only in assertions made before it |
| R4 Resolve | For each field, the most authoritative tier that says anything decides. Disagreement within that tier leaves the field unresolved |
| R5 Report | A less authoritative assertion that disagrees is recorded and changes nothing |
| R6 Impossible evidence | An assertion that the patient attended, made before the session began, is set aside |
| R7 Minutes | The patient's presence, less breaks and lost connections |

### Further rules

| Situation | Rule |
|---|---|
| Two signed records disagree and a lower-tier record supports exactly one of them | Resolved to that value, labelled "resolved by corroboration" |
| The appointment is marked completed but the patient was absent | Not a session. "Completed" never implies presence |
| An appointment is known only from an export | Zero up to the booked length, flagged as having no clinical documentation |
| A correction's original was never supplied | The correction stands as a signed assertion, and is flagged |
| A correction quotes an old value that no record states | Both values are kept and the field is unresolved |
| One document says both "present" and "not present", and gives the patient's times | Present for part. Without times it is a contradiction and stays unresolved |
| Arrival or departure falls outside the session | Roster times are limited to the session. A clinician's stated contact interval never is |
| A group has no break recorded | No break is assumed. The minutes are flagged as an upper bound |
| A break is stated once for every session of a group, naming no session and no date | Taken off each group session that the same document describes and that the break falls inside. Each session says that this was done |
| Scheduling calls, outreach and notices | Never a clinical service, whatever a plan says |
| A document mentions planned services without a threshold | Not a version of the plan |

### How time is used

The brief says a later document does not automatically override an earlier one. Time is used in
two rules only: R3, where a correction must come after what it corrects, and R6. The date a
document was received is stored and never used.

### Unresolved values

When two signed records disagree and nothing settles it, the event carries every candidate. The
minutes are computed once per candidate, giving a minimum and a maximum. Each unresolved field
names the documentation that would settle it.

## 5. Calculations

Events record what happened. They do not record whether a contact counts, because that depends
on the treatment plan. Each contact is judged at calculation time by the plan in effect on its
own date, so a plan change never requires reconciliation to run again.

### A contact

| Check, in order | Result |
|---|---|
| It was not a session | Not counted, with the reason |
| It is an administrative contact | Not counted |
| The plan excludes the service | Not counted |
| The plan does not mention the service | Shown as zero up to its minutes |
| Attendance is not established | Shown as zero up to its minutes |
| Otherwise | Counted |

### A week

| For each of days and minutes | Result |
|---|---|
| The minimum meets the goal | Met |
| The maximum is below the goal | Not met |
| The goal lies between them | Cannot be determined |

A week is not met if either is not met, met if both are met, and otherwise cannot be determined.

### Two consecutive weeks

| Both weeks | The pair is |
|---|---|
| Not met | Definitely below |
| Not met or cannot be determined, with at least one undetermined | Possibly below |

A patient with a definite pair is included. A patient with only possible pairs depends on
unresolved documentation. Pairs that include a week only partly inside the period are reported
separately.

### Assumptions

| Assumption | Alternative |
|---|---|
| A week partly outside the period is judged against the full goal | Prorate by days covered. The plan says "in each Monday–Sunday week" and mentions no prorating |
| A week with a plan change uses the goal in effect on its Monday | Use the stricter goal. The other plan's goal is shown alongside |
| Arrival and departure from a desk record are taken as time in the session | None available. The assumption is printed with the answer |

Each answer prints the assumptions that affect it.

## 6. Answers

| Part | Written by |
|---|---|
| The written answer | The model |
| Checks on the answer | Code |
| Calculation tables | Code, from the saved abstraction |

### Checks

| Check | Catches |
|---|---|
| Every source in square brackets was supplied by a calculation | An invented source |
| Every number appears in a calculation result or the question | A figure the model worked out itself |

The checks confirm that sources and numbers exist. They cannot confirm that a sentence says what
its source says. That is why the tables are appended and are the authoritative part.

### Questions that name no patient

A question that names no patient applies to every patient in the collection who fits what the
question does specify. Code decides who fits, by reading the saved events. The system never
picks one patient.

| Patients who fit | Answer |
|---|---|
| One | That patient, stating that no other patient fits |
| Up to 10 | One section per patient |
| More than 10 | A summary table for all, full detail for none, and how to narrow the question |

### What a question can change

| Store | Written when a question is asked |
|---|---|
| Assertions and events | No. Changed only by ingest |
| Saved answers | Yes |
| Record of model calls | Yes |
| Gap log | Yes, when text search is used |

Results therefore cannot depend on which questions were asked, or in what order. Anything
typed into a question is not evidence.

## 7. Storage

One SQLite file. Each assertion has fixed columns for what every assertion shares and a JSON
payload for details specific to its kind, so a new kind needs no change to the tables. All SQL
is in `Storage/Database.cs`.

The production equivalent is PostgreSQL with a JSONB column, which adds concurrent writers.

## 8. What the supplied record required

| What the record does | Example | How it is handled |
|---|---|---|
| Two notes for one session | Jan 9 family visit | One event |
| Late arrival, early departure, a break | Jan 6 group: 45 minutes, not the booked 90 | Presence less the break |
| A correction, then a stale copy of the uncorrected roster | Jan 19 departure, 11:15 not 11:30 | R2 and R3 |
| One video visit, two call records | Jan 21 | One contact with a gap |
| Two signed notes disagree | Jan 26: 40 or 50 minutes | Left unresolved |
| A draft says attended and a charge was posted, but the signed register says no show | Jan 27 | No session. Both reported |
| The partner was alone for the first 15 minutes | Jan 30 family visit | Only the patient's 30 minutes count |
| "Completed" with the patient absent | Jan 16 collateral visit | Not a session |
| A symptom score imported again ten days later | PHQ-9 of Jan 16 | Three assessments, not four |

Using the booked 90-minute slot for the January 6 group would make the first week 185 minutes
and wrongly mark it as met.
