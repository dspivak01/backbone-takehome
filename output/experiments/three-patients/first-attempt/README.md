# First attempt, kept as a record

These four answers were produced by the first wording of the instructions for questions that
name no patient. They are kept because they show two faults that the final wording corrects.

| Answer | What went wrong |
|---|---|
| `NO-NAME-DEV-04` | The model guessed the year 2024 before looking at the collection, then used 2026. It also added the two patients' figures together, giving 140 minutes for January 19. No calculation produced that number, so the check on the answer reported it and the outcome was not "Passed" |
| `NO-NAME-DEV-05`, `NO-NAME-GENERIC`, `COLLECTION-01` | Passed the checks. Kept for comparison |

The patients covered were correct in all four: Rowan Mercer and Imani Castell for the two
questions about January 19, and all three patients for the other two.

The answers produced by the final wording are in `../answers/` and `../second-run/answers/`.
