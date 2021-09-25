# MSBuild Labels
Here's a brief explanation on the labels most often used by the MSBuild team excluding hopefully self-evident ones such as `bug`.

| Label             | Applied When | Notes |
|-------------------|--------------|-------|
| `needs-triage`    | Team has yet to determine what area/prioritization applies to the issue. | This is the primary label queried during a regular bug triage meeting. Automatically removed when `needs-more-info` is applied. |
| `needs-attention` | An issue requires the team look at it during bug triage. | Automatically applied when a stale issue receives a comment. |
| `needs-more-info` | Team asked for info needed to continue an investigation. | If no response is given within 7 days, the `stale` label is applied. |
| `initial-investigation` | A member of the team does a "first pass" investigation. | `needs-triage` is applied and team member and unassigns themselves after the initial investigation is complete. |
| `stale` | An issue marked with `needs-more-info` is inactive for 7 days. | The issue will be closed after 30 days of inactivity while the `stale` label is applied. |
| `For consideration` | An issue should get higher prioritization when planning the next set of features. | |
| `up-for-grabs` | Anyone can take ownership over this issue. | If a contributor wants to take the issue on, they should ask that it be assigned to them BEFORE doing development work.  |
