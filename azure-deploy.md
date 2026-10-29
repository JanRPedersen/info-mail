# Azure InfoMail operations

The service runs independently of a Windows PC in Azure Container Apps.

## Current status: forwarding activated, 20 October 2026

The user explicitly approved scoped send/write permissions and production
activation, and chose to skip controlled delivery tests and operational email
alarms. The user confirmed only reviewed unambiguous topics, new mail from a new
activation timestamp, and moving originals after Graph acceptance.

- Production start: **2026-10-20T12:33:02.6100886+00:00**
  (14:33:02 Danish time). Earlier inbox mail is excluded.
- `Processing__Enabled=true`, `Processing__SendEnabled=true`,
  `Processing__MoveEnabled=true`. Persisted Boolean controls also verified true.
- Image `infomail:pilot-20261020-08`, ACR build `cg8`; revision
  `infomail-pilot--0000009` is ready.
- Exchange assignments `InfoMail-Azure-Production-Send` and
  `InfoMail-Azure-Production-ReadWrite` grant Application Mail.Send and
  Application Mail.ReadWrite only in scope `InfoMail-Pilot-Mailbox`.
  Its verified filter is `PrimarySmtpAddress -eq 'info@blind.dk'`.
- Authorization tests returned True for Read/Send/ReadWrite on info@blind.dk
  and False for all three on jap@blind.dk. Entra app-role inspection showed only
  Sites.Selected, not additional organization-wide Graph mail grants.
- Destination is **Inbox / Videresendt via regler**, as confirmed by the user.
  The original root-folder assumption failed preflight safely; lookup was
  corrected to Inbox childFolders, all 209 tests passed again, and the existing
  folder was verified using the managed identity before activation.
- The first production scan succeeded at approximately 12:33:28 UTC:
  `Processing scan completed. Read=0 SendConfigured=True MoveConfigured=True.`
  This verifies active scanning, not actual forwarding/delivery/moving.
- SharePoint restored the same rule hash and next-check deadline. Existing
  read-only RoutingPreview still includes older mail and reports
  SendingEnabled=false **for that preview only**. It does not describe the
  production engine. Look at Processing logs and Messages state for processing.
- Automatic topics remain inheritance, donors, DOT card, DH companion card,
  FSC finance, magazine submissions and missing magazine. Ambiguous matches,
  hold instructions, automatic/loop-marked messages and other topics remain
  manual in the inbox. Word-table updates do not expand the reviewed scope.
- No real test delivery was performed, no operational email alarms configured,
  and no budget alarm exists. An empty first scan is not an end-to-end delivery
  test; real forwarding and moving remain unverified until qualifying mail arrives.

Administration commands run in the existing Azure container using its managed
identity (no new local Table role was granted):

```powershell
az containerapp exec --subscription "e2cbaacf-b7d9-461e-9384-6f00d63cceba" `
    --resource-group rg-infomail-pilot --name infomail-pilot `
    --command "dotnet InfoMail.dll --azure-read-only-pilot --processing-preflight"
```

`--processing-preflight` restores/refreshes rules, verifies the existing destination
and reads controls without modifying mail. `--processing-enable-controls` does the
same preflight and explicitly persists both controls true; this is an administrative
activation command, not a monitoring command. Both are exclusive Azure-mode
one-shot commands; neither starts the hosted processing loop. App flags remain
separate gates. Exec's CLI exit code alone is not proof: inspect command output.

The earlier read-only and disabled-deployment sections below document the rollout
history and safe defaults, not the current activation state.

## AI shadow setup, 22 October 2026

**Activated:** `2026-10-22T12:43:21.4696290+00:00` (14:43:21 Danish time).
User explicitly approved real mail subject/body and routing catalog processing
on the existing GlobalStandard deployment, including its geography caveat,
SharePoint logs containing subjects, `AllNew` coverage and 100 requests/day.
The observer does not replace the processing classifier or control mail actions.

- Current image `infomail:ai-candidate-routing-20261027-03`, ACR build `cgm`.
- Current ready revision `infomail-pilot--0000024`, healthy (27 October).
- `AiShadow__Enabled=true`, `AiShadow__DataProcessingApproved=true`,
  `AiShadow__Coverage=AllNew`, `AiShadow__DailyRequestLimit=100`.
- Expanded source context is enabled with
  `AiShadow__IncludeSharePointKnowledge=true` and
  `AiShadow__MaximumInputCharacters=100000`.
- `AiShadow__IncludeExplanation=true`,
  `AiShadow__ComparisonListEnabled=true`.
- Original `Processing__*`, `SharePointRules__*`, identity and sampler settings
  were preserved, including the production start time and send/move gates.
- First active scan read five messages, all before the shadow start; each was
  explicitly skipped without inference. At 13:28:40-13:28:50 UTC the comparison
  revision completed four real new-mail evaluations and four SharePoint exports.
  One route disagreed with the rule robot. Human outcomes were then unrecorded;
  neither synthetic success nor route agreement measures human accuracy.

### Review-only rule expansion, 26 October 2026

The owner chose explicit review/approval before new automatic routing. The
worker now reads actual employee feedback from AI Mail Sammenligning and
publishes `logfiler/regelforslag-yyyyMMdd.txt` after shadow exports on startup,
then at most every 24 hours per process. Failures are logged and retried after
30 minutes; actual mail processing continues. No additional model calls or
automatic rule edits are made.

Only explicitly nonsynthetic rows with `ActualActor=Medarbejder` and a known
actual action count as human evidence. Forwarding requires valid internal
recipients. Different versions of an observation are conservatively grouped by
full subject and received timestamp; identical messages can be undercounted.
Conflicting outcomes or topics are excluded/flagged, never hidden by majority.
Unmatched recipient groups need explicit topic boundaries and matchers.
Explicit hold overrides require separate review. Every proposed expansion
needs owner approval and positive/negative routing tests.

After the owner confirmed that all currently filled actual actions described
employee handling, a one-shot command attributed **28** rows with blank actor
to `Medarbejder`. Each PATCH contained only `ActualActor`, protected by the
item ETag (`If-Match`); actions, recipients and comments were not changed.
The command refuses more than 28 eligible rows or missing ETags and does not
blindly retry conflicts. It is not part of recurring processing and must not
be used to infer the actor of future entries.

Force a report without changing any comparison fields:

```powershell
az containerapp exec --subscription "e2cbaacf-b7d9-461e-9384-6f00d63cceba" `
    --resource-group rg-infomail-pilot --name infomail-pilot `
    --command "dotnet InfoMail.dll --azure-read-only-pilot --routing-feedback-report"
```

The separately approved attribution used the additional
`--routing-confirm-existing-human-feedback` flag, which is rejected outside
report administration mode. Neither command starts the mailbox worker.
Inspect output, not just the exec CLI exit code.

Verified report at `2026-10-26T08:07:58Z`:
- 62 real observation rows, conservatively 60 distinct messages.
- 28 populated actual actions and attributed employees; 26 usable outcomes,
  32 incomplete outcomes and 2 invalid/missing-recipient outcomes.
- Four review groups: unmatched to indsamling (4 examples), unmatched to
  hunde (1), guide-dogs to hunde (1, plus a contrary outcome), and donors to
  Indsamling (2, no contrary outcome in the current evidence).
- These are recipient/topic evidence groups, not approved executable rules.
- Historical processing states since production start: Completed=3,
  ManualReview=176; reasons NoCatalogMatch=95, CatalogReviewRequired=64,
  LoopOrAutomaticMessage=13, ExplicitHold=4 and MoveConfirmed=3.
  This is not a five-day received-mail count or verified delivery count.
  Mail removed before scanning may not be observed.

All **314 tests** passed with warnings as errors. Managed-identity execution
confirmed the attribution and successful SharePoint report upload. The latest
ready revision is healthy with one replica. Existing send/move gates, production
start time, shadow start, 100-call daily limit and all-source context remain
unchanged. **No new automatic sending rule was activated.**

### AI-candidate forwarding, 27 October 2026

The owner separately approved forwarding every validated AI recipient candidate,
including candidates where the AI route is `ManualReview` because of a
confidence threshold or source conflict. The worker now also gives the model
bounded examples from `AI Mail Sammenligning`: only nonsynthetic rows with
`ActualActor=Medarbejder` and a known actual action; forwarded examples must
have valid internal `blind.dk` recipients. The model receives subject/action/
recipient examples, not employee comments. The three Word documents and all
MailRouting rows remain in the same context.

Candidate addresses are constrained to active rule recipients or recorded
internal employee recipients; model-invented addresses are rejected. External
candidates are possible only from an active MailRouting row with
`TilladEkstern=true`. Empty candidates do not trigger AI forwarding, and the
existing deterministic classifier remains the fallback for new mail. A mail
already in `ManualReview` is reopened only when a new AI evaluation provides a
candidate. `NeedsReview` and `Completed` states are never retried.

Non-overridable stops remain: explicit “do not forward” instructions,
automatic or self-originated messages, loop markers, invalid/self recipients,
and missing validated context. The AI may flag `ManualReview` or source conflict
and still provide candidates; the approved policy sends those candidates. This
is more permissive than the 26 October review-only mode. No deterministic
SharePoint rule was edited or generated.

The 100 model requests per UTC day limit is unchanged. The new evaluation
version causes currently visible messages, including prior manual-review cases,
to be reconsidered; the cap means catch-up may take multiple days. Messages
removed before a scan are not retrievable by this worker. Existing send/move
kill switches and persistent compare-and-swap action states still apply.

The feature is controlled by `Processing__UseAiRecipientCandidates=true` in
Azure. It remains false by default locally and is validated to require AllNew
shadow evaluation, SharePoint knowledge and the comparison list.

### Sygemeldinger flyttes uden videresendelse, 28 October 2026

Regardless of deterministic or AI routing, subject/body containing a
`sygemeld*` or `sygefravær*` term is now a move-only case. It is never forwarded
to `loenadministration@blind.dk` or another AI-selected recipient. The worker
requires the existing direct Inbox child folder `@Dagens melding` and the
persistent Move gate; otherwise it records `ManualReview` and sends nothing.
This exception does not automatically include raskmeldinger or other payroll
topics. It takes precedence over an AI candidate and also reopens an older
ManualReview state for the matching message.

The move is persisted as `Moving` before the Graph request. On an uncertain
result, it is not retried automatically; reconciliation is required. The
processing preflight now verifies both `Videresendt via regler` and
`@Dagens melding` exist exactly once directly under Inbox, without changing
mail. This behavior was covered by tests and deployed successfully:
- Image `infomail:sickleave-move-only-20261028-01`, ACR build `cgn`.
- Ready revision `infomail-pilot--0000025`, healthy with one replica.
- Live preflight verified both destination folders and `SendControl=True`,
  `MoveControl=True`; it made no mailbox changes.
- All **328 tests** passed with warnings treated as errors.

The exception applies to future scans and matching messages still present in
the inbox. It cannot undo a sick-leave mail already forwarded or removed from
the inbox before this deployment.

Existing comparison-list AI suggestions are also eligible for the exact same
mail when its full subject and UTC received timestamp match. Versioned rows are
reconciled only if every matching candidate set agrees; conflicting sets are
ignored. A previous recipient must still be authorized by a current active
rule or a confirmed employee outcome. This permits processing an already
visible mail without trusting an unrelated row or guessing by subject alone.
External historical suggestions are not reused; new external candidates still
require an active MailRouting row with `TilladEkstern=true`.

Validation and live rollout:
- All **322 tests** passed with compiler warnings treated as errors.
- ACR build `cgm` succeeded; the latest ready revision is healthy with one replica.
- Production settings were verified: AI candidate forwarding, send and move
  gates are enabled; the 100-request daily limit and original activation times
  are unchanged.
- In the initial live processing after activation, the persistent totals moved
  from 3 to **5 Completed / MoveConfirmed**. Thus two additional mails were
  accepted for forwarding and moved to the configured folder. This is not proof
  of final delivery.
- The latest report saw 115 AI observation rows / 104 conservatively distinct
  messages and 219 historical `ManualReview` states. Those state totals are
  since production start, not current inbox size. A scan found five currently
  visible messages; messages deleted or moved by staff before scanning cannot
  be recovered by this worker.

### Internal replies are not forwarded, 28 October 2026

Messages sent by any `@blind.dk` address are treated as internal and kept in
manual review; neither deterministic rules nor AI recipient candidates can
forward them. AI shadow results also record these messages as `ManualReview`
with no recipient candidates. This includes replies from internal staff, not
only messages sent by `info@blind.dk`.

Validation and live rollout:
- All **330 tests** passed with `--no-restore`.
- ACR build `cgq` succeeded for image
  `infomail:internal-replies-no-forward-20261028-02`.
- Revision `infomail-pilot--0000027` is active, healthy and running.
- Read-only processing preflight on that revision verified both destination
  folders and `SendControl=True`, `MoveControl=True`; it changed no mail.

The rule applies to messages scanned after rollout and cannot undo a message
already forwarded.

### Membership magazine routing is exclusive, 28 October 2026

The three existing magazine topics — submissions, letters to the editor and
missing delivery — are now exclusive to `medlemsbladet@blind.dk`. The engine
discards other deterministic recipients, CC addresses and AI recipient
candidates. Previously `ManualReview` items can be reopened when the routing
catalog identifies one of these topics. Explicit hold instructions, internal
sender protection and the sick-leave move-only exception retain precedence.
The scope is the three catalog topics, not any incidental mention of the word
"medlemsblad".

Validation and live rollout:
- All **334 tests** passed with `--no-restore`.
- ACR build `cgs` created image
  `infomail:magazine-exclusive-20261028-02`.
- Revision `infomail-pilot--0000029` is active, healthy and running.
- Read-only processing preflight on that revision verified both destination
  folders and `SendControl=True`, `MoveControl=True`; it changed no mail.

This affects future scans and cannot recall mail already forwarded.

### All absence messages move to Dagens melding, 29 October 2026

Every recognized absence message is now move-only: sick leave, vacation, leave,
maternity/parental leave, return-to-work notices, other absence, care days,
compensatory time and related terms. No rule or AI candidate may forward these
messages to payroll or any other recipient. They are moved only to the existing
Inbox folder `@Dagens melding`, and AI shadow records them as `ManualReview`
without recipient candidates. Previously manual messages can be reopened for
this move. If the destination or move gate is unavailable, the message remains
in manual review with no forwarding fallback.

The change extends the prior sick-leave-only move behavior; it does not undo
any forwarding already completed. Forwarding was temporarily disabled during
rollout and restored only after the new revision was healthy. The final
configuration has processing, sending and moving enabled. Read-only preflight
on revision `infomail-pilot--0000032` verified both destination folders and
persistent `SendControl=True`, `MoveControl=True`; it changed no mail.

Validation and live rollout:
- All **341 tests** passed with `--no-restore`.
- ACR build `cgu` created image
  `infomail:all-absence-move-only-20261029-01`.
- Revision `infomail-pilot--0000035` is active, healthy and running.
- Processing, send and move configuration gates are enabled; the persistent
  preflight confirmed `SendControl=True` and `MoveControl=True`.
- Read-only preflight on revision 35 verified both destination folders and
  made no mailbox changes.

Verified existing resource:
- Subscription `DBS AI`, `20f6024d-9e2d-40f7-a9bd-08e5f1129116`.
- Resource group `DBS_CHAT_LLM`; AIServices account `chat-dbs-resource`.
- Deployment `gpt-5.4-mini`, model version `2026-03-17`, provisioned successfully.
- SKU `GlobalStandard`, resource location West Europe. Location is not an
  EU-only processing guarantee.
- The OpenAI endpoint is exposed by this account; the implementation uses
  `https://chat-dbs-resource.openai.azure.com/openai/v1/chat/completions`.
  Synthetic inference with the actual InfoMail managed identity is verified.

The InfoMail user-assigned identity (`id-infomail-pilot`, principal
`c88bc432-c133-4fcc-8bd3-09e988354687`) has been granted and verified with
`Cognitive Services OpenAI User`, scoped to this account only. This inference
role applies to the account's deployments, not exclusively the mini deployment.
No chatbot keys were copied and no chatbot settings or mail-processing controls
were changed. The implementation uses a cognitive-services Entra token with
the existing InfoMail managed identity.

The earlier administrator CLI user-token test returned HTTP 401 PermissionDenied.
The deployed `--ai-shadow-preflight` subsequently succeeded with the managed
identity, the actual GPT-5.4 mini deployment and strict response schema. It
created a synthetic example in `Delte dokumenter/logfiler`, without reading
or changing any mailbox message:
`A217A6231EB078E22B19F782471DC64EE74122C8ED1AD5279D3239CF3B667318`.
Model completion, durable result and successful Graph file upload were confirmed
in the command output. The CLI user's Graph file-list request remains denied;
do not confuse that separate principal with the working managed identity.

The user completed an interactive delegated SharePoint administration login.
The existing Sites.Selected grant for InfoMail was changed from `read` to
`write` on the Infomail site only and reread to verify it. No tenant-wide
application SharePoint permissions were added. Site write is broader than
the logfiler folder: application code confines its writes to that folder.
No rule document was edited. The temporary Graph administration session was
disconnected. All 268 local tests passed with warnings treated as errors.

### Three documents and MailRouting expansion

The user explicitly requested all three library documents and the MailRouting
list together, choosing ManualReview when applicable sources disagree.
Runtime inventory confirmed exactly these three Word files in the library root:
- `2.1 Info-mailen - stikordsliste_version maj2026.docx`
- `Liste med noegleord til sortering i infomailen.docx` (actual name uses Danish oe)
- `regeltabel.docx`

The selected drive item IDs are pinned in `SharePointShadowKnowledge`.
Renaming/replacing a selected item requires reviewing the configuration; do not
automatically ingest arbitrary new files, especially the logfiler folder.
MailRouting list ID: `4bb4cf29-5e84-4332-bae1-d4c18861cd76`.
Selected list fields: Title, Modtager, Aktiv, MinConfidence, TilladEkstern,
Kommentar. Every list page is read, bounded to 1,000 rows/ten pages. Inactive rows
are included as reference but cannot authorize an AI rule ID.

Full source snapshots are assembled in memory and cached for at most five
minutes. All three Word bodies (including text in tables) and every list row
are supplied together alongside the existing catalog; no source is selectively
summarized or truncated. Visual highlighting, images and Word annotations are
not interpreted; unresolved tracked changes cause explicit source failure.
Downloads are bounded, redirects are restricted to the approved SharePoint
host and Graph tokens never follow preauthorized download redirects.

Content hashes for all five sources are included in the composite rule version
and SharePoint log metadata. An applicable source conflict reported by the model
forces its AI route to ManualReview in code. The separately approved candidate
policy described above can still forward a valid recipient despite that route.
The model is instructed not to rank sources.
As with all semantic classification, this is not proof that every disagreement
will be detected. Missing or invalid sources are logged as
SourcesUnavailableOrInvalid and never treated as complete success.

List recipient candidates must be explicit valid email addresses. Self-routing
is rejected; external list recipients require TilladEkstern=true. Unknown names
are not converted to guessed addresses. Positive MinConfidence values, whether
fractions or percentages, are retained unchanged and require manual verification;
the model cannot claim a calibrated confidence threshold. ManualAction and
unresolved-recipient restrictions force the AI route to ManualReview; under the
separately approved policy a supported candidate can nevertheless be forwarded.
Documents may provide context without a selectable rule ID: those cases require
manual review rather than an invented recipient/rule.

The initial expanded-source preflight stopped on percentage-style confidence
values instead of sending partial context. Validation now accepts finite
thresholds from 0 to 100 without interpreting units or authorizing forwarding.
The corrected synthetic preflight succeeded with SourceCount=4 and
InputCharacters=54002, and uploaded a versioned log with reference
`09FB5EDE83FEF29D85459CE77E8C24BDE25D38DBDE9B927889105EEF00E7BF4E`.
All 285 local tests passed with warnings treated as errors, including paginated
source loading, blocked external recipients, incomplete source failures and
enforced conflict/manual restrictions. Production configuration retains the
original shadow start timestamp and 100-request daily budget; no historical
backfill or production-rule expansion was authorized.

### Employee comparison and readable summaries

The user confirmed: keep production rules active; employees send as info and
probably delete originals; start with manually recorded human outcomes; only
new mail; short AI explanations may be stored. No mailbox search for human
Sent Items or inference from deletion was authorized or implemented.

The list is
`https://blindesamfund.sharepoint.com/sites/Infomail/Lists/AI%20Mail%20Sammenligning`.
The managed identity's site-write grant could not create a list (HTTP 403).
The user completed a one-shot delegated administrator login to provision the
list from the application's schema; the session was disconnected afterwards.
No service permissions were expanded. The existing site-write grant then
successfully created the synthetic comparison item and real-mail items.

The synthetic comparison preflight reference is
`505A6E78A0B6C2705FFAC09B50E4809FA1EF3934975CD39434452D4837BF7109`,
explicitly marked `IsSynthetic=true`. AI inference, short summary, source
context, text-file upload and comparison item creation succeeded.
All 294 tests passed with warnings treated as errors, including preservation
of human fields and reconciliation after a committed-but-uncertain create.

An indexed unique ShadowReference identifies each version-specific row.
Machine export uses PATCH for only machine-owned fields. It never writes
ActualAction, ActualTo, ActualActor, HumanVerdict or HumanComment. Employees
fill these from actual treatment, choosing employee/robot/unknown explicitly.
Blank fields are unknown; the app does not claim a comparison with humans
until they supply evidence. RuleSuggestion is only a classifier proposal,
not a verified send/move event. Synthetic rows are excluded from real-mail
evaluation by filtering Testdata=false.

The short summary is a user-approved model-generated decision summary in Danish,
not a reasoning trace. It is validated to 1-600 characters without control
characters. The prompt prohibits mail quotes and specific personal details;
this cannot guarantee a model never leaks such details, so keep access
restricted. Summaries are stored in table results/outbox, files and comparison
rows, never runtime logs. Files now include a readable Danish overview before
the detailed JSON and explicitly distinguish AI proposals from actual actions.
The summary flag changes the observation version to allow re-evaluating still
present eligible mails once with the approved richer output, within the same
100-attempt daily budget. The original activation timestamp was not changed.

Activation prerequisites:
1. Obtain explicit approval to send mail subject/body and the validated routing
   catalog to this GlobalStandard deployment, including its processing geography.
2. Verify synthetic inference with the managed identity before sending real mail.
3. Build/test and deploy a new application image, preserving the live
   `Processing__*`, `SharePointRules__*`, identity and read-only sampler settings.
   Do not redeploy the default app template over the live processing settings.
4. Set `AiShadow__Enabled=true`, `AiShadow__DataProcessingApproved=true` and a
   newly approved ISO-8601 round-trip `AiShadow__StartTimeUtc` (for example the
   output of `[DateTimeOffset]::UtcNow.ToString("O")`).
5. Keep `SharePointRules__Enabled=true`. `AllNew` also requires
   `Processing__Enabled=true`; it observes the existing paginated inbox scan.
   `Sample` instead requires `AzurePilot__ReadingEnabled=true` and uses the
   existing sampler start filter in addition to the shadow start.
   The observer uses the existing `PilotState` TableClient and validated current
   rule snapshot. It is not wired to local JSON or one-shot probe/example modes.
6. Inspect `AI shadow` logs, the `AiShadowV1` result partition and the
   `AiShadowLogV1` outbox partition. Verify errors,
   claims and results; a role grant or successful build is not a live inference test.

Default budget: 20 attempted requests per UTC day, shared across deployments
and rule versions; configurable from 1 to 100 using
`AiShadow__DailyRequestLimit`. Request slots and hashed message claims are
atomic and persistent. Interrupted/failed requests consume budget and are not
retried automatically. The same message/model/rule-version combination is not
called again after a restart. Results include the model, rule version, both
routes/rule-ID sets and agreement flags, without mail text. With explicit
approval, short decision summaries are now stored too.
The outbox additionally stores approved subjects and log metadata, not bodies.
No automatic retention cleanup exists for these records or SharePoint files.
Protect access to both the state table and the SharePoint library.

`SharePointShadowLog` creates/verifies the `logfiler` folder in the default
Infomail site drive and uploads UTF-8 `.txt` files named by received UTC time
and hashed reference. Subjects never appear in file names. Files use JSON
escaping inside plain text, not executable HTML or spreadsheet formulas.
Candidate recipients are selected from the current catalog by AI rule ID;
the model cannot invent a logged recipient. Candidates are not authorization.
Each file explicitly says that actions are hypothetical. `SkippedDailyBudget`,
`SkippedInputTooLarge`, `ClaimedOrInterrupted`, `SourcesUnavailableOrInvalid`
and `FailedOrUncertain` are
visible statuses, not successful classifications.

Uploads are retried from the persistent outbox without calling the model again.
Successful updates are acknowledged using ETags. The filename is deterministic,
so a crash after upload but before acknowledgement overwrites the same owned
log file on retry rather than creating duplicate files. A later successful
evaluation after a budget skip updates that same file. Flushes run after
processing, are limited to 100 files and 90 seconds per scan, and preserve
queued records after failures.

Inputs exceeding `AiShadow__MaximumInputCharacters` (default 32,000, maximum
200,000 including the catalog and supplementary sources; active Azure limit
100,000) are skipped rather than truncated. Completion
tokens are capped at 1,024, responses at 64 KiB, observations at 25 seconds
(plus up to five seconds to persist a failure);
redirects and automatic HTTP inference retries are disabled. These bounds are
not a currency-denominated budget or guarantee of correct classification.

The active `AllNew` coverage observes all distinct eligible messages found in
the processing snapshot, including previously manual messages, before that
message's processing-state checks or mailbox writes. Historical messages are
skipped using the separate shadow activation timestamp. The default `Sample`
mode still uses the at-most-five-message sampler and can miss moved messages.
Neither mode sees messages removed by another system before scanning. Size
and daily-budget limits still apply: all found new messages get an evaluation
attempt or an explicit skip/error status, not unlimited model calls.
Observations run sequentially and can extend the scan cycle; a failure
is logged without changing the rule decision. No AI recipients/route enter
the sending/moving decision, which continues to use the original classifier.

Disable with `AiShadow__Enabled=false`; leave processing settings unchanged.
To revoke model access, an authorized administrator can remove only the
OpenAI User assignment for the InfoMail principal at this model account.

## Resources

- Subscription: Azure Subscription DBS, `e2cbaacf-b7d9-461e-9384-6f00d63cceba`.
- Resource group: `rg-infomail-pilot`, North Europe.
- Consumption Container Apps environment and one app, `infomail-pilot`.
- One replica, 0.25 vCPU and 0.5 GiB, no public ingress.
- User-assigned managed identity, `id-infomail-pilot`.
- Basic container registry with admin-password access disabled; identity has AcrPull only.
- Log Analytics with 30-day retention and a 0.1 GB/day ingestion cap.
- LRS Storage with shared-key access and public blob access disabled.
  The `PilotState` table stores validated SharePoint rule snapshots, the active
  version and the next daily check time. The identity has Storage Table Data
  Contributor scoped only to this table, not the entire account. The prepared
  processing engine also uses this table for durable mail state and controls,
  but is disabled until separately activated.
- Resource-group monthly cost budget of 40 billing-currency units, alerts at
  80% and 100% to `japadmin@blind.dk`. Check the returned budget currency in
  Azure; it is not necessarily EUR. This is an alert, not a spending limit.

During initial provisioning Azure Cost Management rejected the budget request
with HTTP 401. The user explicitly approved continuing without the budget alarm
while billing-data access is resolved. The deployed foundation therefore uses
`enableBudget=false`. No cost warning emails are currently configured. Restore
the default `enableBudget=true` only after billing access and currency are verified.

The approved estimate is EUR 30-40/month before tax, not a fixed quote or cap.
Budget data is delayed, and a log cap may suppress monitoring after it is reached.
No automatic email operational alarm is configured by this deployment.

## Application behavior

The container entrypoint is `dotnet InfoMail.dll --azure-read-only-pilot`.
It uses managed identity exclusively, never the local Windows certificate.
`AzurePilot__ReadingEnabled=false` is the deployment default. In this state
the app emits a heartbeat every five minutes and makes no mailbox requests.

After separate authorization and explicit activation, it samples at most five
of the newest inbox messages received on or after `AzurePilot__StartTimeUtc`
every five minutes. Set the timestamp as an explicit round-trip ISO 8601
value, for example `2026-10-20T12:00:00.0000000+00:00`, only after agreeing
the actual activation time. This is NOT a reliable complete inbox processor:
samples may overlap and messages outside the five-message sample may be missed.
Each sample logs aggregate counts and one structured `RoutingPreview` per mail:
SHA-256 reference, received UTC time, rule IDs, candidate To/Cc addresses,
unresolved names and a fixed review reason. No raw Graph IDs, senders, subjects,
mail bodies or tokens are logged. References are pseudonymous, not anonymous.
Repeated rows with the same reference are repeated samples, not additional actions.
Graph errors are reported without response content.

Azure uses `CatalogMailClassifier` with the draft
[routing catalog](Data/routing-catalog.json) for read-only suggestions.
Every overview topic has explicit word/phrase candidate matchers. Only the
allowlisted simple topics and keyword rules can yield recipient suggestions.
Unresolved recipients, non-allowlisted topics, manual actions and multiple
topic matches require manual review. Candidate addresses may still be shown
for review; do not treat these as a complete approved recipient set.
Generic invoices remain manual until their type and exceptions are determined.
Explicit hold instructions suppress recipient suggestions altogether.
There is no prototype fallback forwarding recipient in this Azure mode.
The local JSON prototype and local probes keep their existing behavior.
Word documents are deliberately excluded from the image and from cloud matching:
appending a reference document to every email is not a production routing model.
No mail data or local certificates are uploaded in the build context.
Only the allowlisted application source, settings and routing catalog in
[.dockerignore](.dockerignore) are sent to the organization's Azure build service.

The prepared processing engine is registered alongside the sampler when the
SharePoint source is enabled, but its three configuration gates default false.
Do not grant Mail.Send or Mail.ReadWrite for this stage. Neither the local
forwarding worker nor the JSON inbox is registered in Azure pilot mode.

## Prepared processing engine - disabled deployment

The approved scope is implementation, automated tests and updating the existing
Azure app with sending and moving disabled. It does not authorize new services,
new roles, actual test deliveries or production activation.

Verified deployment on 20 October 2026:

- All **209** automated test cases passed; app Bicep compilation passed.
- ACR build `cg6` succeeded for `infomail:pilot-20261020-06`.
- Revision `infomail-pilot--0000006` is the latest ready revision, Healthy,
  with exactly one active running replica; previous revisions have stopped.
- Live environment inspection confirmed all three Processing flags below false,
  the original read-only start time unchanged and no public ingress.
- Startup logs at approximately 12:12 UTC confirmed
  `Processing engine disabled. Sending=false. Moving=false.`
- SharePoint restored the existing validated hash and unchanged next-check
  deadline (21 October, approximately 11:49:45 UTC). The Graph read-only sample
  succeeded with zero messages, not an error or a sending/moving test.
- No new services or roles were created; no real mail was sent or moved.

- `Processing__Enabled=false`, `Processing__SendEnabled=false`,
  `Processing__MoveEnabled=false`.
- Activation additionally requires its own explicit round-trip ISO 8601
  `Processing__StartTimeUtc`; the sampler's old start is not reused.
- The engine requires the validated SharePoint classifier and existing table.
  All inbox pages are collected before any mail mutation, preventing moves
  from skipping later pages. Each scan has a captured end time.
- More than 10,000 unique messages or 32 Mi characters of subject/body aborts
  the complete scan before any claims or mailbox writes. This is an explicit
  operational failure, not successful processing of just a partial page.
- Engine enabled with SendEnabled false is a full read-only dry run, without
  claiming persistent mail state. It cannot send or move.
- Only unambiguous Forward decisions with valid non-self recipients may send.
  Automatic messages, own messages and InfoMail markers require manual review.
  To/Cc are deduplicated; the Word table's addresses remain To recipients.
- When move is configured, the exact existing folder directly under Inbox
  `Videresendt via regler` must be found before forwarding. No folder is created.
- Reads may retry throttling/server errors; forward and move POSTs never retry.
  Immutable Graph IDs, not folder-dependent ordinary IDs, identify messages.

### Persistent controls, states and uncertainty

The `Messages` partition uses the SHA-256 immutable ID as RowKey. It stores the
raw immutable ID, status, recipient plan and destination ID, but not subject,
body or sender. Treat this as restricted operational metadata. Compare-and-swap
ETags claim each external action; conflicting claims must not act.

The `Control` partition, RowKey `Forwarding`, has **Boolean** `SendEnabled`
and `MoveEnabled` properties. Missing control/entity/properties mean disabled.
Both the app flag and the corresponding table flag must allow an action.
Controls are reread immediately before each external write. Closing the gate
does not recall a request already in flight. Do not enable these controls yet.

Stages:

| Stage | Meaning / operator action |
|---|---|
| Pending | No forwarding attempt claimed; may be evaluated when enabled. |
| ManualReview | No automatic action; inspect the mail and matching rules manually. |
| Sending | A forward attempt was claimed; result may be uncertain. Never reset blindly. |
| Accepted | Graph returned 202 and this was persisted. Resume a separately enabled move, never resend. |
| Moving | A move was claimed; its result may be uncertain. Never repeat blindly. |
| Completed | Graph confirmed the move with 201 and state was persisted. |
| NeedsReview | Interrupted external action observed again; reconcile manually. |

**202 is acceptance, not delivery.** There is no exactly-once delivery guarantee
or automatic delivery confirmation. A timeout, failed status save or overlapping
replicas may leave a conservative unresolved state even if the external action
succeeded. Automatic recovery prefers not sending over accidentally sending twice.

Use the Table browser to inspect `Messages`, including `Sending`, `Moving`,
`NeedsReview` and stranded `Accepted` rows, alongside logs and Exchange trace.
Do not rely only on the current inbox: a successful move followed by a lost
response can leave a Moving row for mail no longer in the inbox. Such rows are
not automatically reconciled. Stop both controls before manual work, inspect
Sent Items/message trace and the destination, and resolve each case deliberately.
Do not delete state or reset Sending to Pending without establishing whether
the forward was accepted. ManualReview/NeedsReview are not automatically
reclassified on rule changes.

Logs show hashed `Reference`, stages and fixed reasons. `Processing scan completed`
means the scan finished, not that every mail was forwarded or delivered.
Neither these logs nor read-only RoutingPreview are delivery receipts.

For urgent stop, set both persisted Boolean controls false and verify them.
Also disable the app configuration, allowing for revision rollout:

```powershell
az containerapp update --subscription "e2cbaacf-b7d9-461e-9384-6f00d63cceba" `
    --resource-group "rg-infomail-pilot" --name "infomail-pilot" `
    --set-env-vars Processing__Enabled=false Processing__SendEnabled=false Processing__MoveEnabled=false
```

Old revisions can run during rollout. Keep controls false; verify old replicas
have stopped. Identity permission revocation may be delayed by authorization
caches. Never roll back to an older state snapshot to retry uncertain sends.

Before activation, separately approve scoped Exchange Application Mail.Send
and Mail.ReadWrite only for info@blind.dk, positive/negative scope tests,
controlled real delivery tests to `japadmin@blind.dk`, destination verification,
operational email alarms to `jap@blind.dk` and the final start time. No automatic
operational email alarms or cost alarms are currently configured. Current tests
simulate Graph/storage; they are not evidence of real sending or moving.

## Deployment

The templates are [deploy/main.bicep](deploy/main.bicep) and
[deploy/app.bicep](deploy/app.bicep). Use an explicit subscription and
resource group for all Azure commands.

```powershell
$subscription = "e2cbaacf-b7d9-461e-9384-6f00d63cceba"
$group = "rg-infomail-pilot"
az group create --subscription $subscription --name $group --location northeurope
az deployment group create --subscription $subscription --resource-group $group `
    --name infomail-foundation --template-file .\deploy\main.bicep `
    --parameters alertEmail=japadmin@blind.dk
```

Read the foundation deployment's non-secret outputs for the registry server,
identity IDs and environment ID. Build with a unique image tag; Docker is
not required on the local PC:

```powershell
az acr build --subscription $subscription --registry "<registryName>" `
    --image "infomail:<unique-tag>" --file .\Dockerfile .
```

Then deploy the app:

```powershell
az deployment group create --subscription $subscription --resource-group $group `
    --name infomail-app --template-file .\deploy\app.bicep `
    --parameters identityId="<identityId>" identityClientId="<identityClientId>" `
      registryServer="<registryServer>" environmentId="<environmentId>" imageTag="<unique-tag>"
```

Reapplying the app template disables reads again by design.
Verify provisioning, running replica and heartbeat logs before calling
deployment successful. An active revision alone does not prove Graph access.

## Exchange permission and activation

### Daily SharePoint rule source, 20 October 2026

The administrator successfully granted the identity site-scoped `read` after
the initial CLI permission attempt failed. Azure subsequently downloaded
`regeltabel.docx`, validated 46 rows, and atomically saved the current catalog
and a version snapshot in the existing PilotState table.

- `SharePointRules__Enabled=true`.
- `SharePointRules__TableEndpoint=https://stinfomaile7hyeaaikl73s.table.core.windows.net`.
- First validated table hash:
  `1AF5C4769239BEB670770DE92D398D6D1717E0E4BDFC1EAA27C8CF2DC677730C`.
- First check completed at approximately 11:49:45 UTC on 20 October.
- Previous verified image: `infomail:pilot-20261020-05`, ACR build `cg5`;
  revision `infomail-pilot--0000005`. All 151 automated test cases passed.
- The new revision restored the same validated hash from Azure Storage at
  approximately 11:53:40 UTC, with the original next-check deadline unchanged.
  This verified persistence across replacement of the container.
- Next check: approximately 11:49:45 UTC on 21 October (13:49 Danish time).
- The deadline survives restarts; the five-minute worker loop performs the check
  at or after the deadline. Downtime may delay it.
- Failed checks log an explicit error and retry in five minutes, retaining the
  last valid catalog. Before any successful validation, all mail requires manual
  review instead of silently using stale bundled recipients.
- Only the two-column `Emne` / `Mailadresse` table is read. Exactly one such
  table is required. Tracked changes within it must be accepted/rejected.
- Email addresses can be separated by `og`, semicolon, comma or paragraph breaks.
  Hyperlink display text must show the actual email address. There is no separate
  cc column; table addresses are all To candidates, and old catalog cc is cleared.
- The table is the sole recipient source. Missing rows suppress their old
  addresses, and legacy keyword-only recipient rules are removed in synchronized
  catalogs. Known subjects retain their reviewed matching definitions/aliases.
  New/renamed subjects have only a literal-phrase manual candidate until their
  match definition is reviewed; no guessed synonyms.
- Existing ambiguity/manual exceptions remain conservative even after personal
  addresses are supplied. Resolving addresses alone does not approve a complex
  topic for automatic routing.
- Entire invalid tables are rejected (bad/self recipients, blank cells, duplicate
  subjects, merged cells, ambiguous table selection, size limits).
- Table rows are hashed separately from surrounding prose, so changing document
  notes alone does not change the rule version.
- Version snapshots use the table hash as a row key and `Rules/Current` as the
  active pointer. A rollback requires an authorized operation to restore a
  validated version as Current and restart the app; no rollback UI exists yet.
- Snapshots contain business routing metadata, not email bodies.

Daily updating does not grant sending or moving permissions. The five-message
read-only sampling limit and approved mail start time remain unchanged.

### Initial SharePoint permission preparation (historical)

The user selected `regeltabel.docx` in the default document library on the
Infomail site as the future daily source. Its table has `Emne` and
`Mailadresse` columns. Existing tested subject match definitions must be retained;
new/renamed subjects require manual review until matching is defined.
Only table content is a rule source, not free-text document notes.

The user explicitly approved Graph Application `Sites.Selected` for the managed
identity and site-scoped `read` for Infomail (including technical read access to
other documents on that site). The Sites.Selected role assignment succeeded:
`MsSLyDPBzE-L0wnpiDVGhxfL46dFAUtAk1g4XDcqLrc`.
Creating the site-scoped read grant was rejected with Graph 403 accessDenied.
No site permission grant was confirmed. Sites.Selected alone authorizes no sites.

Site ID:
`blindesamfund.sharepoint.com,0224fb1e-b25c-41f7-93d5-22e84b601907,aae5b9ae-72ee-4acd-85f3-d3ed176084b3`.
The current CLI session can resolve the site but cannot read the file or manage
its permissions. An appropriately authorized SharePoint administration session
is required. Do not broaden the pilot identity to Sites.Read.All or
Sites.FullControl.All to resolve this administrative-session limitation.
This initial failed attempt was subsequently resolved by the administrator as
recorded above; the daily source is now enabled.

### Catalog preview update, 20 October 2026

- Image `acrinfomaile7hyeaaikl73s.azurecr.io/infomail:pilot-20261020-02`,
  built in ACR run `cg2`; revision `infomail-pilot--0000002`.
- 126 automated test cases passed before deployment.
- Existing read activation and original start filter were preserved.
- First real Graph sample with catalog routing: three messages, one recipient
  suggestion and two requiring manual review. Individual RoutingPreview rows
  included recipients/rule IDs and unresolved consultant roles as expected.
- No message subjects, senders, bodies or raw IDs were logged.
- Sending and moving remain disabled; no new Exchange permissions were requested.

To inspect the preview, open **Azure Portal > Resource groups >
rg-infomail-pilot > infomail-pilot > Log stream > Console**. Filter for
`RoutingPreview`. CandidateTo/CandidateCc are recipient candidates; ManualReview
means unresolved/ambiguous routing, even if some candidates are present.
Rule IDs refer directly to the routing catalog. ReceivedUtc is UTC; convert
to the mailbox user's time zone to correlate with Outlook.

For historical rows, use the Log Analytics workspace's Logs view:

```kusto
ContainerAppConsoleLogs_CL
| where ContainerAppName_s == "infomail-pilot"
| where Log_s contains "RoutingPreview"
| project TimeGenerated, Log_s
| order by TimeGenerated desc
```

Log ingestion can be delayed or capped. Access to logs exposes pseudonymous
mail references and routing candidates; grant it only to authorized reviewers.

### Verified read activation, 20 October 2026

The user confirmed the new identity's Exchange scope tests (info@blind.dk in
scope, jap@blind.dk out of scope) and explicitly approved read activation.

- Start filter: `2026-10-20T08:40:58.6880785+00:00` (10:40:58 Danish time).
- Active revision: `infomail-pilot--0000001`, Healthy, one running replica.
- Previous waiting revision `infomail-pilot--j7cbt1b` is inactive, zero replicas.
- `AzurePilot__ReadingEnabled=true`; no public ingress.
- First actual managed-identity Graph read succeeded at approximately 08:41:29 UTC:
  `Read=0 ForwardSuggestions=0 ManualReview=0`.
- An empty first sample confirms successful access and parsing, not classification
  of a real new message. Sending and moving remain disabled.
- Reads repeat every five minutes while Azure is running, regardless of the local PC.
- Samples may overlap and do not provide complete inbox processing or durable deduplication.
- Budget warnings remain unavailable pending Cost Management access.

### Verified initial deployment, 20 October 2026

- App revision: `infomail-pilot--j7cbt1b`, Healthy, one running replica.
- Image: `acrinfomaile7hyeaaikl73s.azurecr.io/infomail:pilot-20261020-01`.
- Identity client ID: `16a0a254-42db-4a73-a68d-b98cf44330e6`.
- Identity principal/object ID: `c88bc432-c133-4fcc-8bd3-09e988354687`.
- Identity has AcrPull scoped to this registry, no Storage data roles.
- No ingress, no mailbox requests; waiting heartbeat observed in container logs.
- No budget alarm: Cost Management access is still pending.
- 93 automated test cases passed before deployment.

The following Exchange commands require separate approval and an authenticated
Exchange Online administrator session. They have not been executed by the Azure
deployment:

```powershell
New-ServicePrincipal `
    -AppId "16a0a254-42db-4a73-a68d-b98cf44330e6" `
    -ObjectId "c88bc432-c133-4fcc-8bd3-09e988354687" `
    -DisplayName "InfoMail-Azure-Pilot" -ErrorAction Stop

Get-ManagementScope -Identity "InfoMail-Pilot-Mailbox" -ErrorAction Stop |
    Format-List Name, RecipientFilter
```

Confirm the filter is only `PrimarySmtpAddress -eq 'info@blind.dk'` before
assigning the role:

```powershell
New-ManagementRoleAssignment `
    -Name "InfoMail-Azure-Pilot-Read" `
    -Role "Application Mail.Read" `
    -App "c88bc432-c133-4fcc-8bd3-09e988354687" `
    -CustomResourceScope "InfoMail-Pilot-Mailbox" -ErrorAction Stop

Test-ServicePrincipalAuthorization `
    -Identity "c88bc432-c133-4fcc-8bd3-09e988354687" -Resource "info@blind.dk"
Test-ServicePrincipalAuthorization `
    -Identity "c88bc432-c133-4fcc-8bd3-09e988354687" -Resource "jap@blind.dk"
```

Expected: Mail.Read is in scope for info@blind.dk, out of scope for jap@blind.dk.

The new managed identity is NOT the existing InfoMail-Pilot app registration.
An Exchange administrator must separately register its service principal in
Exchange and assign only `Application Mail.Read` with
`CustomResourceScope=InfoMail-Pilot-Mailbox`. Confirm that scope still matches
only info@blind.dk. Test info@blind.dk as in scope and another real mailbox
as out of scope. Check for additional unscoped Entra application grants.

Only after separate user approval, set `AzurePilot__ReadingEnabled=true`
and an agreed `AzurePilot__StartTimeUtc`. Inspect the new revision and logs.
Do not remove the working local application's permissions until the Azure
identity has passed a real read test and the local test is no longer needed.

## Stop and rollback

To stop reads, set `AzurePilot__ReadingEnabled=false`, inspect revision
rollout, and confirm the waiting heartbeat. An old revision may run until
replacement completes. For urgent access revocation, have the administrator
remove the identity's Exchange role assignment; authorization caches may delay
the effect. Disabling reads does not stop Azure charges.

Before production forwarding, approve the automated topic scope and resolve any
remaining draft catalog ambiguities. Validate the prepared deduplication,
uncertain-outcome procedures, loop checks, Graph acceptance-then-move ordering
and kill switch in separately approved real tests. Operational email alerts
remain unconfigured. Review .NET support before production: the current target
is .NET 8.
