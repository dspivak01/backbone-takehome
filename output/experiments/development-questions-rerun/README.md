# Development questions asked again after the instructions changed

The five development questions were asked again on 2026-09-28, after the instructions for
questions that name no patient were changed. The answers are in `output/answers/`.

| Question | First run | What happened |
|---|---|---|
| DEV-01 | Did not pass the check on numbers | The model wrote "90 min block less 15 min break" for two group sessions. No calculation produced the number 90, so the check reported it. The answer is kept here as `DEV-01-first-run.md` and `DEV-01-first-run.json` |
| DEV-02 to DEV-05 | Passed | Nothing to report |

DEV-01 was then asked once more without the cache. That answer passed and is the one in
`output/answers/DEV-01.md`. Both answers are also saved in the database.

DEV-01 names its patient, so the change to questions that name no patient does not alter how
it is answered. The fault is the model doing arithmetic of its own, which the check exists to catch.
