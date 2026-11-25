# Stayline: questionnaire answers and certification evidence

Prepared for the current C# / JavaScript staging build. These answers describe implemented behavior. They do not mean Channex has certified the product or that every certification scenario has been run against staging.

**Review correction, 29 October 2026:** Channex passed the submitted full sync but rejected several deltas and booking-receipt evidence. The revised implementation sends only explicitly selected restriction fields, merges contiguous equal dates, and consumes full booking feed revisions without booking/revision list or by-ID retrieval. Legacy IDs are not new evidence. Follow [the retest guide](Certification-retest-guide.md) and collect fresh task/booking revision IDs through the normal PMS UI. The historical smoke-check results below are not certification passes.

**Retest evidence, 29 October 2026:** the revised build passes 52 local regression checks. Cases 2–10 were repeated through the normal calendar UI and produced nine distinct accepted staging task IDs. A new Booking CRS reservation completed create/modify/cancel through the PMS feed consumer, with persistence before each ACK. All 59 remote ARI readback checks matched the final expected state, including restored inventory after cancellation. See [the new results and IDs](Certification-retest-results.md). Cases 9–10 retain the disclosed single-unit adaptation accepted in the review email. These are live technical checks; formal certification approval remains with Channex.

## Answers for the supplied first page

| Form question | Answer for this build | Evidence and boundary |
| --- | --- | --- |
| Multiple Room Types per Property? | **Yes** | A property owns multiple room types. Hotel room types have independent quantities; rental units use quantity one. |
| Multiple Rate Plans per Room Type? | **Yes** | Each room type can own several independent rate plans, with separate prices and restrictions. |
| Supported restrictions | Check **Availability, Rate, Min Stay Through, Min Stay Arrival, Max Stay, Closed To Arrival, Closed To Departure, Stop Sell** | All eight have operational calendar controls, persist in SQLite, and feed the normal outgoing ARI path. Automated checks verify saving and retrieving each field and its API payload. |
| Need credit card details with bookings? | **No** | The application does not use card details, card tokens or payment processing. Do not request card access for this build. |
| Are you PCI Certified? | The current software has **no PCI certification or PCI service integration**. | Do not select a certified-company or PCI-provider option unless your company can substantiate it independently. The application cannot attest to the company's certification status. If the company has none, choose **No**. |

Min Stay Arrival applies to a stay's arrival date. Min Stay Through applies across occupied nights. Maximum stay uses zero for no maximum. CTA and CTD independently close arrival and departure; Stop Sell closes sales for the selected rate plan and dates. Availability is room inventory after active reservations are subtracted, not a rate-plan restriction.

The current price model is independent **per-room** nightly pricing. Occupancy-based prices, inherited/derived rates, payments, and credit-card handling are outside this build. Multiple properties currently share one trusted operator workspace; this is not yet separate customer tenant isolation.

## Why the two sites can show different properties

Creating a property locally saves it in Stayline. Publishing its property, room types and rate plans to Channex requires the explicit **Create in Channex staging** action. Calendar synchronization alone cannot create those metadata records. Importing a Channex demo property creates a separate local draft instead.

Select a local property in the workspace/calendar selector to view its rooms and rates. Its calendar works before it is mapped. A BRL property and a GBP demo are separate records; they must not be mapped to one another as a currency workaround.

The publish action uses the saved local names, currency, timezone, quantities, occupancy and base prices. Returned Channex IDs are saved as mappings. Once every relationship is available, the application queues 500 days from the saved calendar for the normal ARI worker. This creates one availability request and one restrictions request for that property. Property/room/rate creation responses supply entity IDs; ARI responses supply task IDs. These are different evidence types.

Successful creation steps are retained if a later step is rejected. A lost or malformed creation response has an uncertain outcome: automatic duplicate creation is blocked until the operator reconciles the existing Channex IDs. A completed repeat does not create the same entities or queue another initial sync.

## API coverage in the normal application

| Function | Channex API path | Implementation / evidence |
| --- | --- | --- |
| Discover existing metadata and verify mappings | GET `properties`, `properties/{id}`, `room_types`, `room_types/{id}`, `rate_plans`, `rate_plans/{id}` | Server-side lookup, import and publish validation. Actual paths depend on the chosen workflow. |
| Create local metadata in staging | POST `properties`, `room_types`, `rate_plans` | `StagingProvisionService.cs`; durable creation intent, saved returned IDs, relationship checks and resumable definite failures. |
| Import a remote calendar as a local draft | GET `availability`, `restrictions` | `StagingImportService.cs`; validates the 500-day snapshot, preserves existing properties and does not activate synchronization. |
| Send availability | POST `availability` | `ChannexSyncWorker.cs`; payload uses current saved inventory minus active reservations. |
| Send prices and restrictions | POST `restrictions` | `ChannexSyncWorker.cs`; all seven rate/restriction fields above, batched by property. |
| Receive bookings and acknowledge revisions | GET `booking_revisions/feed`, POST `booking_revisions/{id}/ack` | `BookingInboxWorker.cs`, `ChannexClient.cs`; persist the complete sanitized feed revision, commit reservation/inventory changes before acknowledgment, deduplicate revisions, recover notifications through the feed. No booking/revision list or by-ID GETs. |
| Accept booking notifications | Local POST `/api/channex/webhook` | `ChannexEndpoints.cs`; requires a configured shared secret. Public HTTPS and Channex webhook registration still require deployment setup. |

The API key remains server-side. Browser responses and synchronization history must not expose it. Local secrets, databases and build artifacts are excluded from the source archive.

## What has been checked versus what still needs staging evidence

Automated checks use the actual application entry point, HTTP routes and SQLite transactions with an in-process test server and a fake Channex transport. They include metadata creation from persisted state, independent local calendars, all supported restriction fields, multi-room/multi-rate batching, duplicate protection, resume after a definite rejection, blocking uncertain creation retries, mapping ancestry, credential redaction, reservation lifecycle, booking ordering/acknowledgment, retry handling and persistent rate limiting. They make no real Channex calls and produce no certification task IDs.

Previously verified staging discovery/import proves access to that existing demo and the returned snapshot. It does not prove newly created metadata, an outgoing ARI update or a booking round trip was accepted. Use the actual Channex task IDs and screenshots collected during the current live session for those claims. A local pending-change count means queued work; a successful ARI response and a readback in Channex provide stronger evidence.

### Live smoke check completed on 22 October 2026

Using **Create in Channex staging** in the normal PMS UI created the saved BRL property **Teste**, **Room1** (one unit, two adults) and **TestRate**. All three creation requests returned HTTP 201, and their returned UUIDs were saved locally. The ARI worker then sent one availability request and one restrictions request; both returned HTTP 200. A direct readback from Channex confirmed 500 dates of inventory 1 and the actual saved price BRL 500.00, including the first and last date. Pending changes reached zero.

| Evidence | Actual staging ID |
| --- | --- |
| Property | `e001a56b-7671-4723-a8cf-687d54dbe423` |
| Room type | `2ac7ce64-7152-4ab0-bedb-48a1f63c42ef` |
| Rate plan | `4b37d5db-ea65-4b7f-8bc3-98d5f87952fc` |
| Availability task | `06da0013-b2f0-4b9a-8b5e-036a36837f40` |
| Restrictions task | `9a393ac2-26e9-4e46-89f4-1a7af5db9f67` |

A normal calendar edit also passed after the background restart: changing TestRate for 23 October 2026 from BRL 500 to BRL 501 produced one restrictions update (HTTP 200, task `697771d9-bede-4b1a-bef3-fb7b0cec3d05`), and Channex readback confirmed 501.00. Restoring the original BRL 500 price produced another one-change update (HTTP 200, task `ce6c6d05-84c0-443a-a897-a400a91e55b4`); readback confirmed 500.00 and zero pending changes. The temporary test price was fully restored.

This confirms the onboarding and saved-calendar delivery path. It is **not** the formal certification full-sync scenario: the existing local test property's prices and inventory are uniform, and the broader scenario checklist still needs realistic varied UI changes and its own evidence. Incoming booking webhook authentication and the live booking lifecycle have not been configured or verified in this session.

An old queue block from the unmapped setup was released during this smoke check. Provisioning now clears that permanent mapping block automatically while preserving timed retry/backoff gates; the regression suite passes 38 checks. The readback summary is saved beside the project as `Stayline-staging-verification.json`.

Use the current [Channex certification checklist](https://docs.channex.io/api-v.1-documentation/pms-certification-tests) as the source of required scenarios. Its setup recommends a separate USD test property with two room types and four rate plans. Keep existing BRL and GBP properties intact. The official process requires realistic UI-driven changes and a live review; submitting a form alone does not establish certification.

## Manual evidence worksheet

Run these through Stayline's normal operational UI. Use realistic, varied business data and inspect the resulting history. Add rows for every applicable scenario on later form pages; this worksheet is not a replacement for the full checklist.

| Evidence to collect | Operator action and expected result | Real evidence / task IDs |
| --- | --- | --- |
| Property API | Publish a saved local property; confirm its name, currency and timezone in the Channex property selector. | Entity ID: __________ |
| Room Type API | Confirm each published room, quantity and occupancy in Channex Rooms & Rates. | Entity IDs: __________ |
| Rate Plan API | Confirm every room's independent plans and currency in Channex. | Entity IDs: __________ |
| Initial ARI | Inspect one batched availability request and one batched restrictions request for the full horizon; verify accepted values remotely. | Availability task ID: __________ / Restrictions task ID: __________ |
| Availability delta | Edit inventory for a selected date/range; confirm only changed dates are sent and available rooms match saved capacity minus bookings. | Task ID: __________ |
| Price and restriction deltas | Edit varied prices, both minimum stays, maximum stay, CTA, CTD and Stop Sell; verify Channex reflects the saved values. | Task IDs: __________ |
| Multiple rooms/rates | Change several rooms or plans in a single saved operation; verify the requested batching and all mapped IDs. | Task IDs: __________ |
| Direct reservation lifecycle | Create, modify and cancel a reservation; verify occupied nights and checkout-exclusive availability deltas. | Task IDs: __________ |
| Incoming booking lifecycle | Trigger new, modified and cancelled bookings from Channex; verify durable reservation state before acknowledgment and correct inventory. | Booking/revision IDs and ARI task IDs: __________ |
| Recovery and limits | Demonstrate retries, missed-notification feed recovery, duplicate revisions, rate limiting and a manual recovery sync as required. | Task IDs / observations: __________ |
| Live ad-hoc review | Change arbitrary values requested by the reviewer; show saved DB state, ordinary application handlers, queue, requests and response history. | Review result: __________ |

Before submission, disclose unrun or unsupported scenarios and the reason. Do not copy automated-test UUIDs into the form. Keep credentials and guest/card data out of evidence shared with reviewers.
