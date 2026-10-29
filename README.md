# InfoMail

Dette projekt er en første version af en mail-robot til at overvåge postkassen info@blind.dk og klassificere indkomne mails.

Azure-motoren er aktiveret i indkøringsdrift fra **20. oktober 2026 kl. 14:33:02
dansk tid**. Kun nye mails fra dette tidspunkt omfattes. Lokal JSON-kørsel er
fortsat en prototype. Se den aktuelle [driftsstatus](azure-deploy.md).

## Funktioner
- Læser mails fra en JSON-kilde som eksempel
- Vurderer om en mail skal videresendes eller sendes til manuel gennemgang
- Logfører beslutninger og grundlaget for hver beslutning

## Regelforslag fra medarbejdernes behandling

Azure-driften læser nu også feedback fra
[AI Mail Sammenligning](https://blindesamfund.sharepoint.com/sites/Infomail/Lists/AI%20Mail%20Sammenligning).
Udfyld **Faktisk handling**, **Faktiske modtagere** ved videresendelse og
**ActualActor = Medarbejder** for menneskelig behandling. Regelrobotten skal
markeres som `Regelrobot`; AI-forslag er ikke dokumentation for faktisk handling.

En gennemgangsrapport gemmes i
[logfiler](https://blindesamfund.sharepoint.com/sites/Infomail/Delte%20dokumenter/logfiler)
som `regelforslag-yyyyMMdd.txt` efter logeksport ved opstart og derefter højst
én gang pr. 24 timer i samme proces. Fejl logges og genforsøges efter 30 minutter,
uden at stoppe mailbehandlingen. Rapporten bruger ingen nye AI-kald.
Den grupperer menneskelige udfald, viser modstridende behandling og peger på
rækkernes ID'er. Manglende emnematch kræver manuel emneinddeling.

Rapporten ændrer ingen deterministiske MailRouting-regler. Ejeren har dog
27. oktober 2026 særskilt godkendt, at validerede AI-modtagerkandidater kan
udløse videresendelse og flytning. Modellen får også et begrænset sæt eksempler
fra medarbejdernes faktiske behandling (emne, handling og gyldige interne
modtagere; ikke kommentarer). Modtagere må kun vælges fra aktive regler eller
disse faktiske medarbejderudfald; modellen kan ikke opfinde adresser.

Når der er en gyldig kandidat, kan systemet sende selv om AI-ruten er
`ManualReview`, f.eks. ved lav `MinConfidence` eller kildeuenighed. Eksplicitte
instruktioner om ikke at videresende, automatiske mails, mails fra info@blind.dk
eller andre afsendere på `@blind.dk`, loop-markører og ugyldige/selv-modtagere
forbliver stopklodser. Interne svar bliver derfor ikke sendt videre igen. Eksterne
kandidater fra MailRouting kræver fortsat `TilladEkstern=true`. Uden AI-kandidat
bruges den eksisterende klassifikation; tidligere manuelle sager genåbnes kun
med en ny AI-kandidat. Højst 100 AI-kald foretages pr. UTC-dag, så køen af
manuelle mails kan tage flere dage.

Alle fraværsmeldinger — herunder sygemelding, ferie, orlov, barsel, raskmelding,
fravær, omsorgsdage og afspadsering — videresendes ikke. De flyttes alene til
den eksisterende indbakkemappe `@Dagens melding`, uanset hvilken modtager
reglerne eller AI foreslår. AI-loggen registrerer dem uden videresendelseskandidat.
Hvis mappen eller flyttetilladelsen mangler, bliver sagen manuel; der forsøges
ikke fallback til videresendelse. Dette gælder også tidligere manuelle sager.

For de tre eksplicitte medlemsbladsemner i routingkataloget — indlæg,
læserbreve og manglende levering — går mailen kun til
`medlemsbladet@blind.dk`. Det tilsidesætter andre regelmodtagere og AI-kandidater
og fjerner CC-modtagere. Stopinstruktioner, interne afsendere og
sygemeldingsundtagelsen har stadig forrang. Dette gælder de tre klassificerede
emner, ikke blot ethvert tilfældigt tekstligt nævn af medlemsbladet.

Den 26. oktober bekræftede ejeren, at de 28 eksisterende udfyldte handlinger
var medarbejderbehandling. Kun deres manglende aktør blev udfyldt med
ETag-beskyttelse; handlinger, modtagere og kommentarer blev bevaret.
Se kommandoer og driftskontrol i [azure-deploy.md](azure-deploy.md).

## Struktur
- Services: klassificering og indlæsning af mails
- Models: domænemodeller og beslutninger
- Data: eksempeldata for test

## Kørsel
```bash
dotnet run
```

## Lokal, skrivebeskyttet Microsoft 365-test

Azure-klargøring og forskellen mellem sky-pilot og produktionsdrift beskrives i
[azure-deploy.md](azure-deploy.md). Azure-piloten læser ingen mails som standard
og bruger administreret identitet, ikke certifikatet på den lokale pc.

Dette er en separat engangstest, ikke en pilot i kontinuerlig drift.
Startes kun med `--read-only-probe`; uden dette flag bruges stadig JSON-prototypen.
Testen foretager ét Graph GET-kald til indbakken i info@blind.dk og henter
højst de fem nyeste mails. Den følger ikke næste side og stopper efter kørslen.
Den læser emne og tekst i hukommelsen til klassificering, men udskriver kun
mail-id, modtagelsestidspunkt og forslag. Den skriver ingen videresendelseslog
og sender, flytter, sletter eller ændrer ingen mails, heller ikke læst-status.
Der gemmes ikke mail-id'er mellem disse engangstests.

Forudsætninger:
- Windows med .NET SDK og .NET 8 runtime.
- En single-tenant Entra-app med Exchange Application RBAC-rollen
  `Application Mail.Read`, afgrænset til info@blind.dk.
- Ingen ekstra organisationsdækkende Graph Application-mailrettigheder i Entra.
- En positiv scope-test for info@blind.dk og en negativ for en anden postkasse.

Opret én gang et kortlivet certifikat i PowerShell som den Windows-bruger,
der skal køre testen. Den private nøgle kan ikke eksporteres. Kun den
offentlige del gemmes til upload:

```powershell
$cert = New-SelfSignedCertificate `
    -Type Custom `
    -Subject "CN=InfoMail-LocalProbe" `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -Provider "Microsoft Software Key Storage Provider" `
    -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 `
    -KeyExportPolicy NonExportable -KeyUsage DigitalSignature `
    -NotAfter (Get-Date).AddDays(30)

$folder = Join-Path $env:LOCALAPPDATA "InfoMail-Pilot"
New-Item -ItemType Directory -Path $folder -Force | Out-Null
$publicCertificate = Join-Path $folder "$($cert.Thumbprint).cer"
Export-Certificate -Cert $cert -FilePath $publicCertificate -ErrorAction Stop
$cert | Select-Object Thumbprint, NotAfter
$publicCertificate
```

Upload `.cer`-filen i Entra under **App registrations > InfoMail-Pilot >
Certificates & secrets > Certificates > Upload certificate**.
Upload aldrig en privat nøgle eller PFX-fil. Der skal ikke tilføjes Mail.Send,
Mail.ReadWrite eller organisationsdækkende Mail.Read i API permissions.

Kør fra projektmappen i samme Windows-brugerkonto:

```powershell
$env:GraphProbe__TenantId = "<Directory-tenant-ID>"
$env:GraphProbe__ClientId = "<Application-client-ID>"
$env:GraphProbe__CertificateThumbprint = "<Certifikatets-thumbprint>"
dotnet run --project .\InfoMail.csproj -- --read-only-probe
```

Testen slutter med en fejlstatus ved loginfejl, Graph-fejl eller ugyldigt svar;
fejl er ikke en tom indbakke eller en bestået test. Graph 403 kan skyldes manglende
rettighed eller forsinket udbredelse af en ny tildeling. HTTP 429 kræver en senere
prøvekørsel. Svardata og tokens udskrives ikke som fejl.
Lokale dokumentreferencer indgår som i prototypens normale klassificering.
Faste confidence-værdier er ikke målte sandsynligheder.

### Eksempler fra indbakke og Slettet post

Efter særskilt godkendelse kan `--read-only-examples` bruges i stedet for
`--read-only-probe`. Det læser én side med højst 50 af de nyeste mails i hver af
mapperne `inbox` og `deleteditems`, uden sideskift, undermapper eller
gendannelse af permanent slettede mails. Sorteringen er efter modtagelsestid,
ikke sletningstid. Kun samlede antal og forslag pr. nøgleordskategori vises;
hverken mail-id'er, afsendere, emner, tekst eller regelbegrundelser udskrives.
Kategorier kan overlappe. Dette er kandidatfund og afprøvning af reglerne,
ikke en menneskelig vurdering af den korrekte behandling.

På pc'er hvor EXE-start afvises, kan den byggede DLL køres via .NET-runtime
uden at ændre sikkerhedsindstillinger:

```powershell
dotnet build .\InfoMail.csproj
dotnet .\bin\Debug\net8.0\InfoMail.dll --read-only-examples
```

Brug de samme tre `GraphProbe__...`-miljøvariabler som ovenfor.
Der udføres ingen ændringer i postkassen og ingen vedvarende mail-logning.

Efter forsøget fjernes miljøvariablerne fra vinduet:

```powershell
Remove-Item Env:\GraphProbe__TenantId, Env:\GraphProbe__ClientId, Env:\GraphProbe__CertificateThumbprint
```

Når den lokale test ikke længere skal bruges, fjernes certifikatregistreringen
i Entra og det tilsvarende certifikat i **Current User > Personal > Certificates**
på pc'en. Identificér det på thumbprint, ikke kun navn.

## AI evaluation and candidate forwarding (disabled by default locally)

The optional observer compares rule decisions with the existing `gpt-5.4-mini`
deployment. Locally the feature remains disabled by default. In Azure, shadow
mode was activated on 22 October 2026 at 14:43:21 Danish time. As of
27 October, `Processing__UseAiRecipientCandidates=true` additionally allows
validated AI recipient candidates to authorize forwarding and moving.

`AllNew` observes every distinct eligible message found by the paginated
processing scan, before processing that message (including messages already
marked for manual review). This prevents the robot's own forwarding/moving
from hiding messages from shadow evaluation. Messages received before the
separate shadow start time are excluded. A message removed by a human or
another system before scanning cannot be observed. The default `Sample`
coverage remains the at-most-five-message read-only preview.

It sends subject, body and validated SharePoint routing context, but not sender,
mail ID, attachments or the baseline decision. Mail text can still contain
personal/confidential data. Live processing was explicitly approved.
GlobalStandard model deployments do not guarantee EU-only processing.

The active Azure configuration also sets `AiShadow__IncludeSharePointKnowledge=true`.
Every evaluation includes the combined text of all three explicitly selected
Word documents in the Infomail library (the overview/stikordsliste, the
keyword document and regeltabel), all MailRouting rows, and eligible employee
examples from AI Mail Sammenligning. Examples include only nonsynthetic rows
with `ActualActor=Medarbejder` and a known action; forwarding examples must have
a valid internal recipient. Human comments, sender, body and attachments are
excluded. Source snapshots refresh at most every five minutes; all list pages
must succeed. The log records source names and hashes, including
`SourceConflict`.
The documents are text-extracted including tables; images, annotations and
visual highlighting are not interpreted. Sources are reference data, not
permission to follow links or execute instructions.

No source has priority. The model must report conflicting applicable guidance
and can return `ManualReview`; under the owner's separate approval, a supported
candidate can still be forwarded despite that route. Inactive MailRouting rows
and unresolved recipients cannot authorize a candidate. Recipient addresses
must be explicit, valid and non-self. External routing requires
`TilladEkstern=true`. Positive `MinConfidence` is not a measured probability,
but no longer blocks a candidate under the approved policy. Explicit holds,
automatic/loop mail, self-routing and invalid recipients remain manual.
Text-only document guidance never becomes an invented address. Missing/invalid
sources produce `SourcesUnavailableOrInvalid`, not partial-context success or
silent stale-source fallback.

Defaults: disabled; no data-processing approval; maximum 20 attempted model
calls per UTC day, 32,000 input characters including the catalog, 1,024 maximum
completion tokens per call, and a 25-second observation timeout. Oversized inputs
are skipped without truncation. The character limit is not an exact token/cost
limit. Request attempts consume budget even if inference fails.
Azure currently permits 100,000 input characters to accommodate the combined
sources (the verified synthetic request used 54,002 characters). The code
accepts limits up to 200,000; a complete source snapshot itself is bounded to
160,000 JSON characters. No document or list page is silently truncated.

The existing `PilotState` table stores hashed references, claim status,
AI route/rule IDs and agreement flags. A separate `AiShadowLogV1` outbox stores
the approved mail subject and log metadata, but never the body, sender,
or attachments. With explicit approval, Azure now also stores a short
model-generated Danish decision summary (`AiShadow__IncludeExplanation=true`).
Summaries are limited to 600 characters and must describe category, sources/rules
and uncertainty, without mail quotations, personal details or addresses.
They are not model reasoning traces or verified facts; generated text can still
reveal sensitive topics and requires the same access controls as subjects.
Neither subjects nor summaries are printed in runtime logs.
Claims and the daily budget are updated atomically. A message is evaluated at
most once per deployment/rule version, even after restart or uncertain failure.
Changing either allows a new evaluation within the same shared daily budget.
Failed/claimed entries are not automatically retried or automatically deleted.
Comparison is route/rule agreement, not recipient validation or measured accuracy.

### Comparing with the employees' actual treatment

Open [AI Mail Sammenligning](https://blindesamfund.sharepoint.com/sites/Infomail/Lists/AI%20Mail%20Sammenligning).
`AiShadow__ComparisonListEnabled=true` exports each version-specific observation
to this list, in addition to the text log. Open a row to see the full subject,
received time, AI action, recipient candidates, short summary, source versions
and the rule robot's proposed action. `Testdata=true` identifies synthetic
examples; these must not count as real-mail evaluation results.

After handling the matching mail, the employee edits these fields:
- **Faktisk handling**: forwarded, answered, manual treatment, deleted without
  forwarding, other, or unknown.
- **Faktiske modtagere**: the addresses actually used, if applicable.
- **Behandlet af**: employee, rule robot, or unknown.
- **Vurdering**: agree, partly agree, disagree, or cannot assess.
- **Medarbejderens kommentar**: an optional short explanation of the difference.

These fields remain blank until a human records evidence. The app never
populates or overwrites them, including after retries or refreshed machine
fields. The same unique reference reconciles uncertain row creation without
duplicates. Compare each version separately if sources have changed.
Employees currently send as info and may delete originals; deletion alone
is not proof of forwarding. Personal/shared Sent Items are not scanned by
this feature. A blank actual-treatment field is unknown, not agreement or
a classifier failure. The rule proposal is not proof of a completed robot action.

The existing rule robot remains enabled. New AI evaluation applies only after
the shadow start time; a new version can reconsider older messages still visible
in the inbox, including a prior `ManualReview`, but no mail already removed
from the inbox is backfilled. The scan runs roughly every five minutes plus
processing time, so messages removed by staff before scanning can be missed.
This workflow is not a guaranteed capture of every received message or
automatic measurement of human accuracy.

Logs are uploaded as plain-text files to
[Infomail / Delte dokumenter / logfiler](https://blindesamfund.sharepoint.com/sites/Infomail/Delte%20dokumenter/logfiler).
Each file contains the subject, received time, hashed reference, model/rule
version, both route/rule suggestions, candidates from active rules or employee
precedents, suggested destination, short decision summary and agreement flags. A readable
Danish header explains the proposed action; detailed JSON follows. These are suggestions, not evidence
of performed mail actions. Budget/size skips and failed/uncertain evaluations
are explicitly marked without inventing an AI decision. Upload failures retain
the outbox for a later scan; deterministic file names make retries idempotent
without repeating inference. No automatic retention cleanup is configured.
Restrict library access to authorized reviewers because subjects may be sensitive.
The content hash includes all five sources: changing a source can produce a
new version-specific observation/file for a mail still present in the inbox.
The original shadow activation time remains unchanged; this is not historical
backfill of already moved mail.

`--azure-read-only-pilot --ai-shadow-preflight` performs a one-shot synthetic
model call and SharePoint upload using the managed identity, without reading
or changing the mailbox. It temporarily supplies approval/start time only for
its synthetic message, enables all five sources for that test and consumes
one request slot. It also enables short summaries and the comparison-list export
for this test. The row is explicitly marked synthetic; actual employee fields
remain blank. It does not activate
the background worker or change production controls.

List creation requires more than the runtime's existing site-write grant.
Provision the list once with an authorized SharePoint administrator; do not
broaden the robot's permissions just to create it. A local
`dotnet .\bin\Debug\net8.0\InfoMail.dll --ai-comparison-schema` command exports
the shared list definition as JSON without login or network calls.
An existing list is schema-checked, not silently modified.

`--azure-read-only-pilot --ai-shadow-source-inventory` lists source document
names and MailRouting field definitions using the runtime managed identity.
It prints no mail content, document text or list row values and does not start
the worker or modify SharePoint.

See [Azure AI setup and activation](azure-deploy.md#ai-shadow-setup-22-october-2026).

## Tests

Kør tests fra projektmappen:

```powershell
dotnet test .\Tests\InfoMail.Tests\InfoMail.Tests.csproj
```

Testene dækker:
- Regelmatch i emne og tekst, store/små bogstaver, regelrækkefølge samt alle/et af nøgleordene.
- Deaktiverede regler, tomme nøgleord og ukendte henvendelser.
- At eksplicitte stopord som "ikke videresend" og "holdes tilbage" har forrang over videresendelse, også ved fallback.
- Modtager, målmappe og manuel gennemgang.
- JSON-indlæsning, ugyldige data og frasortering af behandlede mails ved næste kontrol.
- Logning, bevarelse af eksisterende logposter og fejl uden overskrivning af en ugyldig log.
- Workerens rækkefølge: log før markering som behandlet, og ingen markering hvis logning fejler.
- Projektets egne eksempel-mails og regler.
- Den lokale Graph-tests grænse på fem mails, GET-metode, manglende sideskift,
  fejlstatus og fravær af mailtekst og tokens i output (med simuleret Graph).

Filtestene bruger separate midlertidige mapper, som ryddes op efter hver test.
Ingen tests forbinder til en postkasse eller sender mails.

## Begrænsninger i prototypen

### Behandlingsmotor i Azure

**Aktuel drift:** De tre `Processing__...`-flag og de vedvarende kontroller er
aktiveret efter særskilt godkendelse. De deaktiverede værdier nedenfor er sikre
standardværdier, ikke den aktuelle Azure-konfiguration. Kun gennemgåede,
entydige emner kan sendes automatisk; resten forbliver manuelle.
Brugeren har valgt at springe testafsendelse og fejlalarmer over.

Den nye motor kan hente alle sider af indbakken efter et **separat aftalt
starttidspunkt** og gemme behandlingsstatus i den eksisterende Azure-tabel.
Den gamle fem-mails-visning er fortsat kun en stikprøve, ikke en kvittering
for fuld behandling.

Motor, afsendelse og flytning er som standard deaktiveret med
`Processing__Enabled=false`, `Processing__SendEnabled=false` og
`Processing__MoveEnabled=false`. Derudover skal en særskilt, vedvarende
kontrolpost tillade hver handling. En manglende kontrolpost betyder nej.
Sende- og skriverettigheder er nu givet via Exchange RBAC kun til info@blind.dk.

Motoren gemmer et krav om behandlingen **før** et afsendelsesforsøg.
Microsoft Graph HTTP 202 betyder accepteret til behandling, ikke dokumenteret
levering. Ved et uklart udfald genafsendes der aldrig automatisk.
Flytning til den eksisterende mappe **Videresendt via regler** kan først
forsøges efter gemt accept fra Graph. Mappen findes direkte under Indbakke.
Hvis flytning er konfigureret,
kontrolleres mappen inden afsendelse; motoren opretter den ikke.
Automatiske mails, egne mails og mails med InfoMail-markør bliver manuelle.
Det er konservativ beskyttelse, ikke en garanti mod alle eksterne mailloops.

Statusserne `Sending`, `Moving` og `NeedsReview` kræver manuel afklaring ved
afbrydelse. `Accepted` er accepteret af Graph, og `Completed` betyder bekræftet
flytning, **ikke** bekræftet levering. Ingen garanti om exactly-once-levering.
Manuelle og uklare sager bliver ikke automatisk genvurderet ved regelændringer.

Tests bruger simuleret Graph og lagring; ingen rigtige testmails er sendt.
Brugeren har fravalgt afsendelsestest til `japadmin@blind.dk` og alarmer til
`jap@blind.dk`. Fejl kræver derfor aktiv overvågning af logs og behandlingsstatus.
Se [azure-deploy.md](azure-deploy.md) for stop og manuel afklaring.

### Regelkatalog til gennemgang før produktionspilot

#### Daglig opdatering fra SharePoint

Azure-piloten bruger nu tabellen **Emne / Mailadresse** i `regeltabel.docx`,
placeret direkte i standardbiblioteket på sitet Infomail. Dokumentet hentes
med administreret identitet og læseadgang kun til det godkendte SharePoint-site.
Regeltabellen kontrolleres hver 24. time, og den næste kontroltid gemmes i Azure,
så en genstart ikke starter en ny døgnperiode.

Kun regeltabellen bruges, ikke dokumentets frie noter. Kendte emner beholder
de testede matchregler fra kataloget; adresserne erstattes af Word-tabellens
adresser. Flere adresser adskilt med "og", semikolon, komma eller afsnit læses
som samtidige Til-modtagere. Mailto-links skal vise selve mailadressen.
Gamle adresser til emner, der ikke længere står i tabellen, bruges ikke.
De tidligere selvstændige nøgleordsregler kan heller ikke beholde modtagere,
som ikke findes i tabellen.

Nye eller omdøbte emner kræver manuel vurdering af matchreglen. De kan vises
som manuelle kandidater ved et præcist frasematch, men får ikke automatisk
gættede søgeord. Tvetydige kendte emner og særlige undtagelser forbliver manuelle.

Gyldige ændringer gemmes som versioner og tages automatisk i brug. En ugyldig
tabel afvises samlet, fejlen logges, og seneste gyldige tabel beholdes.
Ved fejl forsøges igen efter fem minutter; uden en tidligere gyldig version
går alt til manuel behandling. Der er ingen e-mailalarm for disse fejl endnu.
Der skal være præcis én tabel med de to kolonneoverskrifter og uden flettede
celler eller uafklarede registrerede ændringer i tabellen.

I Azure Log stream vises `SharePointRules validated` med antal rækker,
versionshash, om indholdet er ændret og næste kontroltid. Ved genstart vises
`SharePointRules restored`. Afsendelse og flytning er stadig slået fra.
Se [azure-deploy.md](azure-deploy.md) for drift og versionsopbevaring.

[Data/routing-catalog.json](Data/routing-catalog.json) omsætter begge Word-kilder
til emner, dokumenterede mailadresser, uafklarede modtagere og undtagelser.
Kataloget er et udkast og bruges nu af Azure-pilotens særskilte
`CatalogMailClassifier` til skrivebeskyttede forslag. Den lokale JSON-prototype
bruger fortsat de gamle regler. `To` er dokumenterede kandidater, ikke godkendelse
til afsendelse. Afsnitsreferencer
tæller ikke-tomme Word-afsnit i dokumentrækkefølge; gul markering og
tabellayout skal også gennemgås visuelt før godkendelse.

Før afsendelse implementeres, skal forretningen afklare person- og
gruppeadresser, DH/EBU/WBU- og Selveje-noter, geografisk fordeling, eksterne
modtagere og regler for følsomme oplysninger. Fakturatyper og særlige
undtagelser må ikke reduceres til et enkelt generelt nøgleord.

Den aftalte produktionspilot skal kun behandle nye mails i indbakken fra et
senere godkendt aktiveringstidspunkt og flytte originalen til
`Videresendt via regler` efter bekræftet afsendelse. Azure afklares med IT.
Ingen aktivering, afsendelsesrettigheder eller mailflytning er implementeret
eller godkendt gennem dette katalog.

Azure viser én `RoutingPreview`-række pr. mail i **Container App > Log stream >
Console** og i de indsamlede konsollogs. Rækken indeholder:
- `Reference`: SHA-256 af Graph-id, så gentagne stikprøver kan genkendes.
- `ReceivedUtc`: modtagelsestidspunkt, som kan sammenholdes med Outlook.
- `Rules`: matchende regel-id'er i kataloget.
- `CandidateTo` / `CandidateCc`: dokumenterede modtagerkandidater, ikke faktisk afsendelse.
- `Unresolved`: navne eller grupper uden bekræftet adresse.
- `Status`: `RecipientSuggestion` eller `ManualReview`, samt en fast `ReviewReason`.

Emne, afsender, mailtekst og rå Graph-id udskrives ikke. Referencen er
pseudonymiseret, ikke anonym; logadgang skal fortsat begrænses til relevante personer.
Katalogets `TopicMatchers` bruger hele ord/fraser uden sproglig forståelse.
Kun de særskilt angivne `PreviewReadyTopicIds` og nøgleordsregler kan give et
modtagerforslag; andre emner, flere samtidige emnematch, manuelle handlinger og
uafklarede modtagere giver manuel behandling. Kandidatadresser vises også ved
manuel behandling, så mangler kan afklares, men udgør ikke en komplet godkendt
modtagerliste. Et generelt ord som faktura afgør ikke fakturatype.
Stopord fjerner alle modtagerforslag. Ukendte emner, herunder en generel
bestilling uden en dokumenteret regel, har ingen fallback til info@blind.dk.
Ord i signaturer og citeret tekst kan stadig give match; alle forslag skal vurderes.

- Videresendelse simuleres og logføres; der sendes ikke rigtige mails.
- JSON-kilden husker behandlede mail-id'er i hukommelsen under kørslen. Ved genstart læses eksempel-mails igen; permanent dubletbeskyttelse er endnu ikke implementeret.
- Den lokale prototypes stopord og øvrige nøgleord matches som delstrenge, ikke som sproglig forståelse.
- Lokale dokumentreferencer indgår i prototypens regelmatch, men ikke i Azure-katalogets match. Azure bruger det eksplicitte JSON-katalog i stedet for at tilføje alle dokumenttekster til hver mail.
