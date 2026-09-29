# Synthetic third patient, sharing dates with the supplied record

These documents are fictional test data written for this project. No real person, clinic or
record is described.

They exist to test one rule: a question that names no patient applies to every patient who
fits what the question does specify. This patient has one session on 19 January 2026 and one on
21 January 2026, the same two dates on which the supplied record has contacts, so a question
about those dates that names nobody must cover both patients.

| File | What it is |
|---|---|
| `treatment_plan.txt` | A plan with a weekly goal of 2 therapy days and 100 minutes, for an episode from 19 to 25 January 2026 |
| `individual_note_jan19.txt` | A signed note for an individual session on 19 January 2026, 14:00 to 14:50 |
| `attendance_register_jan.txt` | A certified register with that session and a group session on 21 January 2026, 10:00 to 11:00 |

Expected results, worked by hand: 1 therapy contact and 50 minutes on 19 January, 1 therapy
contact and 60 minutes on 21 January, and one week with 2 therapy days and 110 minutes, which
meets the goal.

The layout, labels and identifiers differ from the supplied documents and from
`test-data/synthetic-patient/` on purpose. These documents are not part of the submitted
abstraction of the supplied record.
