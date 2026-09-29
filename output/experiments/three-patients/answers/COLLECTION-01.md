# COLLECTION-01

**Question**: Which patients had two consecutive weeks below their treatment plan's requirements?

## Answer

*Written by the model from the calculation results below.*

The question named no patient, so it was answered for all 3 patients in the collection.

**Patients with two consecutive weeks below their treatment-plan goal:**

- **Rowan Mercer (HG-M042)**: Week of 2026-01-05 (3 therapy days vs. goal of 3 — met; but 140 minutes vs. goal of 150 — not met) followed by week of 2026-01-12 (2 therapy days vs. goal of 3 — not met; 120 minutes vs. goal of 150 — not met). [consecutive_weeks_below_goal]

- **Jordan Avery (NS-77120)**: Week of 2026-02-09 (1 therapy day vs. goal of 2 — not met; 45 minutes vs. goal of 90 — not met) followed by week of 2026-02-16 (2 therapy days vs. goal of 3 — not met; 105 minutes vs. goal of 150 — not met). [consecutive_weeks_below_goal]

**Not included:** Imani Castell (WD-40815) — her record does not show a qualifying pair of consecutive weeks below goal. [consecutive_weeks_below_goal]

No pairs were flagged as depending on unresolved documentation or as involving only a partial week for any patient.

## Checks on the answer

*Run by code after the model finished.*

| Check | Result |
|---|---|
| Sources cited | 0 cited, 0 found in the calculation results |
| Sources not found in the results | None |
| Numbers in the text | 21 found |
| Numbers not found in the results or the question | None |
| Outcome | Passed |


## Calculations

*Produced by code from the saved abstraction. No model wrote or edited these tables.*

### 1. consecutive weeks below goal

Inputs chosen by the model: `{}`. Ran in 16.2 ms.

Patients examined: 3. Period: each patient's episode start to each patient's episode end.

| Group | Patients |
|---|---|
| Two consecutive weeks below the goal | Rowan Mercer (HG-M042), Jordan Avery (NS-77120) |
| Inclusion depends on unresolved documentation | None |
| Below only when a partial week is counted | None |
| Not included | Imani Castell (WD-40815) |


**Rowan Mercer (HG-M042)**: included

| Pair | Kind | First week | Second week | Would be settled by |
|---|---|---|---|---|
| 2026-01-05 and 2026-01-12 | Definite | not met. 3 therapy day(s) against a goal of 3: met. 140 minutes against a goal of 150: not met. | not met. 2 therapy day(s) against a goal of 3: not met. 120 minutes against a goal of 150: not met. | Nothing needed |


**Jordan Avery (NS-77120)**: included

| Pair | Kind | First week | Second week | Would be settled by |
|---|---|---|---|---|
| 2026-02-09 and 2026-02-16 | Definite | not met. 1 therapy day(s) against a goal of 2: not met. 45 minutes against a goal of 90: not met. | not met. 2 therapy day(s) against a goal of 3: not met. 105 minutes against a goal of 150: not met. | Nothing needed |


**Assumptions**

- Two weeks are consecutive when one begins seven days after the other.
- A pair that includes a week only partly inside the period examined is reported separately, because the goal is not prorated.



## How this answer was produced

| Item | Value |
|---|---|
| Model | us.anthropic.claude-sonnet-5 |
| Model calls | 2 |
| Tokens | 10371 input, 689 output |
| Calculations run | find_patient, consecutive_weeks_below_goal |
| Calculations asked for and not run | None |
| Time | 9441 ms |
| Abstraction version | 7069255b5cc58700-31f9775b |


