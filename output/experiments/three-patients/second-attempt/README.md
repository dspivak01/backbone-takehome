# Second attempt, kept as a record

These four answers were produced by the second wording of the instructions. All four passed the
checks and covered the right patients, and none added figures across patients.

One fault remained. For `NO-NAME-DEV-04` the model asked about 19 and 21 January 2024, a year it
had guessed, in the same step in which it asked who was in the collection. It then used 2026.
The two calculations for 2024 found nothing, and they appear in that answer's list of
calculations.

After this attempt the program was changed so that a calculation that is given a date does not
run until the list of patients, with the dates of their episodes, has been returned to the
model. The answers produced after that change are in `../answers/` and `../second-run/answers/`.
