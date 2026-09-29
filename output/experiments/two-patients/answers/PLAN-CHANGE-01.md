# PLAN-CHANGE-01

**Question**: How did the types and amounts of care delivered to Jordan Avery change before and after the treatment-plan change?

## Answer

*Written by the model from the calculation results below.*

**Jordan Avery – care before and after the treatment-plan change**

The record contains two plan versions [NS-0001 L8-11; NS-0014 L9-12, L12].

**Before the change (2026-02-02 to 2026-02-15, 14 days)**
- Plan required: 2 days/week, 90 minutes [NS-0001 L8-11]
- Counted categories: Group therapy, Individual therapy
- Sessions: Group therapy 1 (60 min), Individual therapy 2 (95 min)
- Total: 3 sessions, 155 minutes
- Rate: 1.5 sessions/week, 77.5 minutes/week

**After the change (2026-02-16 to 2026-02-27, 12 days)**
- Plan required: 3 days/week, 150 minutes [NS-0014 L9-12, L12]
- Counted categories: Family therapy, Group therapy, Individual therapy (family therapy added)
- Sessions: Family therapy 1 (45 min), Group therapy 2 (120 min), Individual therapy 2 (95 min)
- Total: 5 sessions, 260 minutes
- Rate: 2.92 sessions/week, 151.7 minutes/week

**Change summary**
- A new service type — Family therapy — appeared after the plan change, which was not counted before.
- Group therapy sessions increased from 1 (60 min) to 2 (120 min).
- Individual therapy stayed the same in both count (2) and minutes (95).
- Total counted sessions rose from 3 to 5, and total minutes from 155 to 260.
- The required target itself also increased, from 2 days/90 minutes per week to 3 days/150 minutes per week [NS-0001 L8-11; NS-0014 L9-12, L12].

Rates per week were calculated by dividing each period's totals by its length in days over seven, since the two periods are of different lengths (14 vs. 12 days).

## Checks on the answer

*Run by code after the model finished.*

| Check | Result |
|---|---|
| Sources cited | 6 cited, 6 found in the calculation results |
| Sources not found in the results | None |
| Numbers in the text | 44 found |
| Numbers not found in the results or the question | None |
| Outcome | Passed |


## Calculations

*Produced by code from the saved abstraction. No model wrote or edited these tables.*

### 1. plan change comparison

Inputs chosen by the model: `{"patient_key":"NS-77120"}`. Ran in 15.7 ms.

| Parameter | Value | Source |
|---|---|---|
| Patient | Jordan Avery (NS-77120) | Matched by medical record number |
| Dates | 2026-02-02 to 2026-02-27 | Not given, so the episode dates stated in the record (NS-0001 L8-11) are used; both dates are included |
| Week | Monday to Sunday | From the treatment plan |
| Abstraction version | 0cd5d5acddc1168b | Changes only when a conclusion about this patient changes |


The record contains 2 plan versions. Each period below runs from the day a version takes effect to the day before the next one.

| Period | Days | Goal | Sessions | Minutes | Sessions per week | Minutes per week | Plan source |
|---|---|---|---|---|---|---|---|
| 2026-02-02 to 2026-02-15 | 14 | 2 days and 90 minutes | 3 | 155 | 1.5 | 77.5 | NS-0001 L8-11 |
| 2026-02-16 to 2026-02-27 | 12 | 3 days and 150 minutes | 5 | 260 | 2.92 | 151.7 | NS-0014 L9-12, NS-0014 L12 |


**By service type**

| Period | Service | Sessions | Minutes |
|---|---|---|---|
| 2026-02-02 to 2026-02-15 | Group therapy | 1 | 60 |
| 2026-02-02 to 2026-02-15 | Individual therapy | 2 | 95 |
| 2026-02-16 to 2026-02-27 | Family therapy | 1 | 45 |
| 2026-02-16 to 2026-02-27 | Group therapy | 2 | 120 |
| 2026-02-16 to 2026-02-27 | Individual therapy | 2 | 95 |


**Assumptions**

- Rates per week divide each period's totals by its length in days over seven, so periods of different lengths can be compared.
- Each contact is judged by the plan in effect on the day it happened.



## How this answer was produced

| Item | Value |
|---|---|
| Model | us.anthropic.claude-sonnet-5 |
| Model calls | 3 |
| Tokens | 10671 input, 767 output |
| Calculations run | find_patient, plan_change_comparison |
| Time | 12128 ms |
| Abstraction version | d0c9c62d81aa43e7-b02d950f |


