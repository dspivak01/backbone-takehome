# GAP-01

**Question**: Why did Rowan miss or cut short sessions during the episode?

## Answer

*Written by the model from the calculation results below.*

Rowan Mercer is the only patient matching "Rowan" in the collection.

**Missed sessions (no-show or cancelled, not counted toward therapy minutes)**

| Date | Encounter | Type | Reason recorded |
|---|---|---|---|
| 2026-01-08 | HG-E103 | Individual therapy | Marked a no-show; reception said Rowan had not checked in, and when Rowan called back that afternoon they said "the morning had gotten away from them after a poor night of sleep" [BH-D015 L9, BH-D015 L15]. |
| 2026-01-15 | HG-E108 | Group therapy | Cancelled by the clinic: "A covering facilitator is unavailable for this morning's group" [BH-D016 L9]. |
| 2026-01-27 | HG-E116 | Group therapy | Marked a no-show [BH-D108 L10, L16]. A billing charge and an unsigned draft both suggested attendance, but the record notes "Billing is not evidence of attendance" and "A draft is not evidence of attendance" [BH-D112 L18-23, L9-11]. |
| 2026-01-28 | HG-E117 | Individual therapy | Cancelled by the patient [BH-D108 L11, L18]. No further reason is recorded. |

Two other January contacts were not psychotherapy at all (so not "missed sessions" in the treatment sense), but the patient was not present in person:
- 2026-01-16, HG-E109: "a collateral discussion with Casey only, focused on observations at home… Patient did not join by telephone or video" [BH-D012 L11, BH-D109 L13 context/BH-D006 L20].
- 2026-01-23, HG-E114: a care-coordination call; "No patient contact occurred during this care coordination call" — an outside social worker relayed that Rowan wanted help arranging a return-to-work schedule [BH-D109 L7, L9].

**Sessions cut short**

- 2026-01-06, HG-E102 (group therapy): Rowan called ahead to say they were running behind "after difficulty finding parking," then told the facilitator partway through that they "would need to leave early for a previously arranged ride"; departure was logged when the visitor badge was returned, and "Rowan did not re-enter the group room after departing" [BH-D005 L13]. The counted time (45 minutes) already reflects this late arrival/early departure and an intervening break [BH-D004 L4-8, BH-D005 L9-11].
- 2026-01-21, HG-E112 (individual therapy): the session ran 13:00–13:55, but a connection was lost from 13:20–13:30; the counted 45 minutes excludes that lost-connection interval [BH-D106 L17-18, L3-5, L7]. No further reason for the connection loss is documented.

No other counted sessions in the episode are flagged as shortened or interrupted. If you need the exact reason a no-show occurred (e.g., 1/27) beyond what's above, the record does not contain one — that would need a signed clinician or patient statement to settle.

## Checks on the answer

*Run by code after the model finished.*

| Check | Result |
|---|---|
| Sources cited | 14 cited, 14 found in the calculation results |
| Sources not found in the results | None |
| Numbers in the text | 20 found |
| Numbers not found in the results or the question | None |
| Outcome | Passed |


## Calculations

*Produced by code from the saved abstraction. No model wrote or edited these tables.*

### 1. session counts

Inputs chosen by the model: `{"patient_key":"HG-M042"}`. Ran in 10.7 ms.

| Parameter | Value | Source |
|---|---|---|
| Patient | Rowan Mercer (HG-M042) | Matched by medical record number |
| Dates | 2026-01-05 to 2026-01-30 | Not given, so the episode dates stated in the record (BH-D003 L6, BH-D003 L12) are used; both dates are included |
| Week | Monday to Sunday | From the treatment plan |
| Abstraction version | c9e505146e97ad8e | Changes only when a conclusion about this patient changes |


**Sessions attended**

| Service type | Sessions |
|---|---|
| Family therapy | 2 |
| Group therapy | 5 |
| Individual therapy | 5 |
| Total | 12 |
| Distinct days | 11 |


**Counted sessions**

| Date | Encounter | Service | Minutes | How the minutes were worked out | Sources |
|---|---|---|---|---|---|
| 2026-01-05 Mon | HG-E101 | Individual therapy | 50 | present 09:00-09:50 = 50 minutes | BH-D002 L6-8, BH-D002 L8, BH-D006 L10 |
| 2026-01-06 Tue | HG-E102 | Group therapy | 45 | present 10:15-11:15, less break 10:45-11:00 = 45 minutes | BH-D004 L12, BH-D004 L4-8, BH-D005 L10, BH-D005 L9-10, BH-D006 L11 |
| 2026-01-09 Fri | HG-E104 | Family therapy | 45 | present 14:00-14:45 = 45 minutes | BH-D006 L13, BH-D007 L4-9, BH-D007 L7-9, BH-D008 L4-9, BH-D008 L8, BH-D015 L13 |
| 2026-01-12 Mon | HG-E105 | Group therapy | 75 | present 10:00-11:30, less break 10:40-10:55 = 75 minutes | BH-D005 L11, BH-D005 L9-11, BH-D006 L14, BH-D009 L12, BH-D009 L4-8 |
| 2026-01-14 Wed | HG-E107 | Individual therapy | 45 | present 11:00-11:45 = 45 minutes | BH-D006 L16, BH-D011 L4-9, BH-D011 L7 |
| 2026-01-19 Mon | HG-E110 | Group therapy | 60 | present 10:00-11:15, less break 10:45-11:00 = 60 minutes | BH-D101 L3-6, BH-D101 L6, BH-D102 L4-9, BH-D102 L9, BH-D102 L9-10, BH-D103 L7, BH-D104 L10-14, BH-D104 L14 |
| 2026-01-19 Mon | HG-E111 | Individual therapy | 30 | present 11:15-11:45 = 30 minutes | BH-D105 L3-7, BH-D105 L6 |
| 2026-01-21 Wed | HG-E112 | Individual therapy | 45 | present 13:00-13:55, less connection lost 13:20-13:30 = 45 minutes | BH-D106 L17, BH-D106 L18, BH-D106 L3-5, BH-D106 L7 |
| 2026-01-22 Thu | HG-E113 | Group therapy | 45 | present 10:30-11:30, less break 10:45-11:00 = 45 minutes | BH-D107 L7-8, BH-D107 L8, BH-D107 L9, BH-D108 L14, BH-D108 L9 |
| 2026-01-26 Mon | HG-E115 | Individual therapy | 40 to 50 | present 09:00-09:50 = 50 minutes; or present 09:10-09:50 = 40 minutes | BH-D110 L3-6, BH-D110 L7, BH-D111 L10, BH-D111 L14, BH-D111 L4-8 |
| 2026-01-29 Thu | HG-E118 | Group therapy | 75 | present 10:00-11:30, less break 10:45-11:00 = 75 minutes | BH-D107 L12-13, BH-D107 L13, BH-D107 L14, BH-D108 L12, BH-D108 L20 |
| 2026-01-30 Fri | HG-E119 | Family therapy | 30 | present 13:15-13:45 = 30 minutes | BH-D113 L3-6, BH-D113 L7, BH-D113 L7-9, BH-D113 L9 |


**Appointments and contacts not counted**

| Date | Encounter | Service | Why it is not counted | Sources |
|---|---|---|---|---|
| 2026-01-08 | HG-E103 | Individual therapy | The appointment was a no show. | BH-D006 L12, BH-D015 L11, BH-D015 L6 |
| 2026-01-13 | HG-E106 | Medication management | The treatment plan says medication management does not count toward the goal (BH-D003 L6, BH-D003 L12). | BH-D006 L15, BH-D010 L4-9, BH-D010 L7 |
| 2026-01-15 | HG-E108 | Group therapy | The appointment was cancelled by the clinic. | BH-D006 L17, BH-D016 L9 |
| 2026-01-16 | HG-E109 | Collateral contact | The patient was not present. | BH-D006 L18, BH-D006 L20, BH-D012 L11, BH-D012 L4-7, BH-D012 L8 |
| 2026-01-23 | HG-E114 | Care coordination | The patient was not present. | BH-D109 L2-6, BH-D109 L7 |
| 2026-01-27 | HG-E116 | Group therapy | The appointment was a no show. | BH-D108 L10, BH-D108 L16 |
| 2026-01-28 | HG-E117 | Individual therapy | The appointment was cancelled by the patient. | BH-D108 L11, BH-D108 L18 |
| 2026-01-30 | HG-E120 | Medication management | The treatment plan says medication management does not count toward the goal (BH-D003 L6, BH-D003 L12). | BH-D114 L3-6, BH-D114 L5 |


**Records that disagree with the conclusion**

- 2026-01-26 HG-E115: R4: Unresolved presence start: 09:00 (BH-D110 L7) versus 09:10 (BH-D111 L10). (BH-D110 L7, BH-D111 L10)
- 2026-01-27 HG-E116: R5: A charge was posted for this encounter (Group psychotherapy, quantity 1). The appointment was a no show. Billing is not evidence of attendance. (BH-D112 L18-23)
- 2026-01-27 HG-E116: R5: An unsigned draft says the patient attended. The appointment was a no show. A draft is not evidence of attendance. (BH-D112 L9-11)


**Contacts described by more than one document, counted once**

- HG-E101 on 2026-01-05 is described by 2 documents (BH-D002, BH-D006) and is one contact.
- HG-E102 on 2026-01-06 is described by 3 documents (BH-D004, BH-D005, BH-D006) and is one contact.
- HG-E103 on 2026-01-08 is described by 2 documents (BH-D006, BH-D015) and is one contact.
- HG-E104 on 2026-01-09 is described by 4 documents (BH-D006, BH-D007, BH-D008, BH-D015) and is one contact.
- HG-E105 on 2026-01-12 is described by 3 documents (BH-D005, BH-D006, BH-D009) and is one contact.
- HG-E106 on 2026-01-13 is described by 2 documents (BH-D006, BH-D010) and is one contact.
- HG-E107 on 2026-01-14 is described by 2 documents (BH-D006, BH-D011) and is one contact.
- HG-E108 on 2026-01-15 is described by 2 documents (BH-D006, BH-D016) and is one contact.
- HG-E109 on 2026-01-16 is described by 2 documents (BH-D006, BH-D012) and is one contact.
- HG-E110 on 2026-01-19 is described by 4 documents (BH-D101, BH-D102, BH-D103, BH-D104) and is one contact.
- HG-E113 on 2026-01-22 is described by 2 documents (BH-D107, BH-D108) and is one contact.
- HG-E115 on 2026-01-26 is described by 2 documents (BH-D110, BH-D111) and is one contact.
- HG-E116 on 2026-01-27 is described by 2 documents (BH-D108, BH-D112) and is one contact.
- HG-E118 on 2026-01-29 is described by 2 documents (BH-D107, BH-D108) and is one contact.


**Records that are not appointments**

- BH-D001 L10: Authorization does not cover individual therapy, family therapy, or medication appointments under the group service quantity.
- BH-D001 L15: No service attendance record accompanies this letter.
- BH-D006 L20: Patient did not arrive by the close of the appointment slot
- BH-D006 L20: The group session did not take place; it was removed from the active room schedule due to staff illness
- BH-D010 L17: No separate psychotherapy component was provided or documented during this visit.
- BH-D012 L17: A new treatment decision made with Rowan
- BH-D012 L17: Patient-present psychotherapy
- BH-D012 L17: Rowan joining the contact in person, by telephone, or by video
- BH-D013 L11: Submission of an additional questionnaire with this update
- BH-D013 L15: A clinical appointment at the time of this review
- BH-D014 L17: Completion of a new patient questionnaire in this batch
- BH-D014 L19: A visit with the patient
- BH-D015 L9: arrival call or cancellation message on the scheduling line
- BH-D015 L17: therapy intervention during the callback
- BH-D016 L15: The coping skills group was not held and no participants were seen.
- BH-D016 L15: No replacement group was conducted in the January 15 slot.
- BH-D103 L13: additional clinical service in making this correction
- BH-D108 L16: Patient treatment contact
- BH-D108 L18: A replacement appointment being booked within January 2026
- BH-D109 L7: No patient contact occurred during this care coordination call.
- BH-D109 L13: Patient did not join by telephone or video, and no psychotherapy was delivered during the call.
- BH-D112 L16: clinician attestation or finalized patient-specific narrative in the draft
- BH-D115 L14: A separate treatment appointment or additional patient-contact interval beyond the questionnaire review
- BH-D013 L11: a copy or later mention of the PHQ-9 result completed on 2026-01-05, not a new assessment or visit.
- BH-D014 L12-13: a copy or later mention of the PHQ-9 result completed on 2026-01-16, not a new assessment or visit.



### 2. observations

Inputs chosen by the model: `{"patient_key":"HG-M042","category":"reason_for_contact"}`. Ran in 20.3 ms.

| Parameter | Value | Source |
|---|---|---|
| Patient | Rowan Mercer (HG-M042) | Matched by medical record number |
| Dates | 2026-01-05 to 2026-01-30 | Not given, so the episode dates stated in the record (BH-D003 L6, BH-D003 L12) are used; both dates are included |
| Week | Monday to Sunday | From the treatment plan |
| Abstraction version | c9e505146e97ad8e | Changes only when a conclusion about this patient changes |


Category: reason for contact

| Date | Category | What the record says | Source | Record |
|---|---|---|---|---|
| 2026-01-05 | reason for contact | "Rowan describes several weeks of low mood, reduced interest in usual activities, fragmented sleep, and difficulty beginning ordinary tasks." | BH-D002 L11 | clinical note, signed |
| 2026-01-06 | reason for contact | "Group focus: recognizing early physical signs of anxiety and choosing a coping response before withdrawing from a task." | BH-D004 L10 | clinical note, signed |
| 2026-01-06 | reason for contact | "Rowan called from the building entrance to say they were running behind after difficulty finding parking." | BH-D005 L13 | desk log, unsigned |
| 2026-01-06 | reason for contact | "Rowan advised the facilitator that they would need to leave early for a previously arranged ride." | BH-D005 L13 | desk log, unsigned |
| 2026-01-06 | reason for contact | "Departure was marked when Rowan returned their visitor badge. Rowan did not re-enter the group room after departing." | BH-D005 L13 | desk log, unsigned |
| 2026-01-08 | reason for contact | "11:12: Reception notified the clinician that Rowan had not checked in." | BH-D015 L9 | desk log, unsigned |
| 2026-01-08 | reason for contact | "15:36: Rowan returned the call. They said the morning had gotten away from them after a poor night of sleep" | BH-D015 L15 | desk log, unsigned |
| 2026-01-08 | reason for contact | "confirmed that they still intended to attend the next day's visit with Casey." | BH-D015 L15 | desk log, unsigned |
| 2026-01-09 | reason for contact | "The appointment focused on patterns of support at home as Rowan attempts to rebuild a routine." | BH-D007 L12 | clinical note, signed |
| 2026-01-09 | reason for contact | "Casey described worry that giving Rowan space might leave them isolated." | BH-D007 L12 | clinical note, signed |
| 2026-01-10 | reason for contact | "Casey explained that the reminders were an attempt to help, while also recognizing that repeated prompts increased tension." | BH-D008 L11 | clinical note, signed |
| 2026-01-12 | reason for contact | "Rowan checked in before the group began and remained until the group was released." | BH-D005 L15 | desk log, unsigned |
| 2026-01-12 | reason for contact | "No transport concern was reported at that time." | BH-D005 L15 | desk log, unsigned |
| 2026-01-15 | reason for contact | "A covering facilitator is unavailable for this morning's group." | BH-D016 L9 | administrative notice, unsigned |
| 2026-01-16 | reason for contact | "This was a collateral discussion with Casey only, focused on observations at home and ways to support the treatment plan." | BH-D012 L11 | clinical note, signed |
| 2026-01-19 | reason for contact | "Rowan attended the opening check-in, accepted the exercise handout, and requested additional help from the individual clinician." | BH-D104 L18 | attendance record, signed (copy) |
| 2026-01-19 | reason for contact | "Group content is recorded separately." | BH-D104 L18 | attendance record, signed (copy) |
| 2026-01-19 | reason for contact | "This visit was added because Rowan became anxious during group and needed individual grounding and review of coping strategies." | BH-D105 L9 | clinical note, signed by Mira Patel, LCSW |
| 2026-01-21 | reason for contact | "Rowan reported drafting a short message about a possible gradual return to work but stopping before sending it." | BH-D106 L9 | clinical note, signed |
| 2026-01-23 | reason for contact | "The outside social worker reported that Rowan had asked for help understanding whom to contact about a gradual return schedule." | BH-D109 L9 | clinical note, signed |
| 2026-01-26 | reason for contact | "Rowan described sending a short message to the supervisor and receiving a request to discuss possible next steps." | BH-D110 L9 | clinical note, signed by Mira Patel, LCSW |
| 2026-01-26 | reason for contact | "Rowan discussed difficulty moving from a supervisor's reply to arranging the next conversation." | BH-D111 L10 | clinical note, signed |
| 2026-01-30 | reason for contact | "Casey described uncertainty about when reminders helped and when they seemed to increase Rowan's sense of pressure." | BH-D113 L9 | clinical note, signed by Mira Patel, LCSW |
| 2026-01-30 | reason for contact | "The service consisted of medication evaluation and management, including symptom review and medication counseling. No separately documented psychotherapy was provided." | BH-D114 L12 | clinical note, signed by Elena Ortiz, PMHNP |
| 2026-01-30 | reason for contact | "Rowan completed the questionnaire before the afternoon appointment." | BH-D115 L8 | clinical note, signed by Mira Patel, LCSW |



## How this answer was produced

| Item | Value |
|---|---|
| Model | us.anthropic.claude-sonnet-5 |
| Model calls | 3 |
| Tokens | 20821 input, 1742 output |
| Calculations run | find_patient, session_counts, observations |
| Time | 23751 ms |
| Abstraction version | d0c9c62d81aa43e7-b02d950f |


