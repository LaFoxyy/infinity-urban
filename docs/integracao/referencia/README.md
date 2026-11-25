# Stayline

A small property management application for exploring a Channex staging integration. The backend uses C# / ASP.NET Core, Entity Framework Core and SQLite; the frontend uses plain JavaScript, HTML and CSS. A hotel room type has a quantity greater than one; a vacation rental can use a room type with a quantity of one.

This is an early product foundation, **not a certified integration**. The calendar, reservations, database and synchronization workers are intended to become part of the real product. Channex decides whether the application meets its certification requirements during its review.

## Run locally

Install the .NET 10 SDK. From this directory:

```powershell
dotnet run --project src/Stayline.Api
```

Open <http://127.0.0.1:5080>. The application creates `src/Stayline.Api/App_Data/stayline.db` on first start. No Node.js build step is required. By default, Channex synchronization is disabled, so the application works locally without credentials. Saving real calendar or reservation changes still records the pending synchronization work.

On Windows, double-click `Start-Stayline.cmd` to build the app and run it in the background. It opens the site after the local health check succeeds. Closing the browser or launcher window does not stop the background server. Use `Stop-Stayline.cmd` when you want to stop that server; it checks the saved process ID, executable path and start time before stopping only this project's instance. Runtime state and logs stay in the ignored `src/Stayline.Api/.local` directory. After code changes, use `Restart-Stayline.cmd` to stop and rebuild this background instance, then open the site again.

The normal `dotnet run` command still runs in the foreground: its terminal must stay open, and Ctrl+C stops it. If a foreground instance is already using port 5080, stop it in its own terminal before switching to the background launcher. The helpers do not change firewall rules, install a Windows service or enable startup at login.

Stop the application, then run the operational checks without contacting Channex:

```powershell
dotnet run --project tests/Stayline.Checks
```

The checks use temporary SQLite databases, a test-only HTTP handler and an in-process ASP.NET Core test server. They exercise real API routes and middleware alongside calendar rollback, reservation date changes and cancellation, overbooking rejection, 500-day batching, decimal formatting, durable rate limiting, warnings and retries, booking revision ordering, duplicate handling, missed-webhook feed recovery and persistence before acknowledgment. Onboarding checks also cover read-only discovery, complete staging imports, repeated import handling, rollback for incomplete or invalid remote data, currency mismatches and incomplete mapping rejection. They open no network sockets and do not replace testing against staging.

## Connect Channex staging

Obtain a Channex staging account and API key. Create a staging property with the room types and rate plans that you will map to local records. With PowerShell 7, run the hidden-input setup script, then start or restart the backend:

```powershell
pwsh -NoProfile -File ./Configure-Staging.ps1
dotnet run --project src/Stayline.Api
```

The script reads the key without showing it or putting it in shell history or process arguments. It prefers .NET User Secrets, stored outside the project. If Windows refuses access to that profile store, it writes `src/Stayline.Api/.local/secrets.json`, which is ignored by Git, excluded from source downloads, and outside `wwwroot`. Both are local development configuration files, not encrypted vaults. Credentials are not included when sharing this project; configure them separately on each machine.

The `local` launch profile uses the Development environment, which loads these local credentials. Use the normal `dotnet run` command above; a published application or `--no-launch-profile` in Production must receive its own server configuration. Restart a running instance after configuring credentials. Environment variables such as `Channex__ApiKey`, `Channex__Enabled` and `Channex__WebhookSecret` override local settings. Do not include real values in shared terminal commands.

The API hostname is fixed to `https://staging.channex.io/api/v1/`. Keep credentials on the server. The browser receives connection flags and mapping records, never the API key. Outgoing header logs and saved ARI response details redact configured credentials.

The **Properties** page lists records saved in this application's database. Channex properties appear separately in staging discovery until you import one or connect it to an existing local property.

For a property created in Stayline, add its rooms and rate plans, then use **Create in Channex staging**. Review the actual property, currency and inventory in the confirmation. The server creates the property, room types and per-room rate plans through Channex's APIs, saves their returned UUIDs as mappings, and queues the initial 500-day calendar publication. This works independently of the imported demo property. Select the new property in Channex's property switcher to inspect its rooms and rates; a different property's Rooms & Rates page will not list them.

Successful setup steps are saved individually, so retrying does not recreate already mapped entities. An interrupted create request with an uncertain outcome blocks automatic repetition: inspect Channex and complete the matching mappings before retrying. Setup requests and actual returned entity IDs appear in sync history. Entity IDs from property/room/rate creation are different from the task IDs returned by ARI updates; use the ARI task IDs for the corresponding certification scenarios.

Each local property has its own calendar, whether it is connected to Channex or not. Use **Open calendar** on its property card or the **Property calendar** selector to change properties. Adding a room or rate plan selects its parent property.

To start from the staging demo, choose **Import into PMS**, inspect the property preview and explicitly confirm the import. The backend creates a separate local draft with the staging property's currency, timezone, rooms, room quantities, rate plans and 500-day availability, price and restriction snapshot. If Channex returns zero prices, the preview identifies those dates and requires a positive local price for each affected rate plan. It suggests an existing positive daily price when available; review or change that amount before importing. Your confirmed amount initializes only the unpriced dates and that plan's local base price; existing positive daily prices are preserved. Discovery, preview and import only read Channex; importing does not activate mappings or queue outgoing updates. Existing local properties are preserved, including properties in another currency. Repeating the import returns the existing draft instead of duplicating it or replacing saved changes. Unsupported pricing models or missing inventory/restriction fields must be resolved before import.

Review the imported calendar before connecting it. Imported availability is saved as local open capacity; importing does not fetch existing reservations. Reconcile future bookings and inventory with Channex before publishing, so later booking imports do not subtract the same occupied inventory twice. An imported snapshot is an onboarding starting point, not an ongoing mirror of remote changes.

Alternatively, create the local property, room types and rate plans yourself, then open its connection settings. Use the remote inventory lookup to select the matching Channex property, room types and rate plans. Save the correct IDs against their corresponding local records; do not reuse another property's IDs. Both properties must use the same currency, and each local room needs a matching room and rate plan. A complete mapping activates delivery and queues 500 days of the currently saved local state. Confirm the calendar is correct before saving that mapping.

Use the calendar to save a price, availability or restriction change. The backend writes the application state and outbox records in one database transaction. The worker batches pending changes and builds API payloads from the current saved state. The synchronization history records responses and returned task IDs; a queued update is not proof that Channex accepted it.

An initial full sync queues a 500-day horizon. With a complete mapping, the worker combines it into one availability request and one rate/restrictions request per property. The application rejects a full sync when rooms, rates or mappings are incomplete and shows the missing setup steps. Ordinary edits produce delta updates; the worker does not repeatedly send full inventory on a timer. A manual full sync is also available for recovery. A missing booking webhook secret does not block outgoing prices, availability or restrictions; configure it separately before accepting incoming booking notifications.

## Incoming reservations

Booking notifications wake the regular booking feed consumer. Complete revisions come only from `booking_revisions/feed`: the backend persists a minimal operational snapshot, saves the booking and resulting availability changes, then acknowledges that revision. It never retrieves incoming bookings through booking/revision list endpoints or by-ID GETs. New, modified and cancelled reservations all affect inventory. Occupied dates include arrival and exclude departure. Repeated notifications are deduplicated; feed polling recovers missed notifications. **Check incoming bookings** runs that same consumer immediately. Booking receipts show booking ID, revision ID, status, source and acknowledgment, so no separate diagnostic API request is needed to find certification IDs. Replay payloads are hidden from the UI/API and cleared after acknowledgment; cards and guest contact fields are excluded from them.

For a live end-to-end staging test, use a host that supports .NET 10, persistent storage and public HTTPS. Before hosting, add authentication or restrict the operator UI/API with your reverse proxy or private network. Expose the webhook route to Channex:

```text
POST https://your-staging-host.example/api/channex/webhook
X-Stayline-Webhook-Secret: the-server-Channex__WebhookSecret-value
```

Configure a Channex booking webhook with `event_mask: "booking"`, `send_data: true`, and `headers: {"X-Stayline-Webhook-Secret": "<same server secret>"}`. Use a random secret of at least 16 characters. The application accepts `payload.revision_id` on ordinary booking events and `payload.booking_revision_id` on alternate events, stores the notification and requests an immediate feed check. Posted guest data is not trusted or imported. A localhost URL cannot receive Channex notifications. Sites hosting cannot execute this C# backend; the backend needs its own .NET host. This project does not provision hosting, HTTPS, reverse-proxy access controls or a Channex webhook automatically.

## Before requesting certification

- Agree on the client's real workflows and confirm the product supports the required room, rate and inventory model.
- Complete and verify mappings against a realistic Channex staging property.
- Save varied prices, inventory and restrictions through the application's normal UI, then inspect the resulting requests and task IDs.
- Exercise new, modified and cancelled bookings through Channex, including duplicated notifications, missed webhook recovery and acknowledgment after persistence.
- Verify batching, rate limiting, transient failures, retries and recovery full sync with staging credentials.
- Run every applicable [Channex certification test](https://docs.channex.io/api-v.1-documentation/pms-certification-tests), record the task IDs and disclose unsupported or skipped scenarios.
- Be ready to demonstrate ad-hoc changes in the live review and open the application code that performs them. Passing local checks does not imply certification.

Use the [Channex integration guide](https://channex.io/connect-first-property) and [API documentation](https://docs.channex.io/api-v.1-documentation) alongside the current certification checklist supplied by Channex.

## Scope and limitations

This build is for local development by one trusted operator. It has no user authentication, authorization, customer tenant isolation or production security review. Multiple property records do not provide isolation between separate businesses. Do not expose this version directly to the public or use it for real guest data.

SQLite and the in-process mutation lock assume a single application instance with a persistent local disk. Scaling to several instances requires a shared database, distributed work claiming and durable coordination. Keep the database on persistent storage and back it up. Set the server `DataDirectory` configuration value to place the database on a persistent volume when deploying. Stopping the application preserves queued work in SQLite; deleting the database deletes local application records and synchronization history.

The initial schema is created with Entity Framework's `EnsureCreated`. `AppSchema.EnsureAsync` applies the reviewed additive upgrades for field-specific outbox changes and booking receipt metadata to existing databases. Before upgrading an existing file database it makes a consistent SQLite backup under `App_Data/.schema-backups`; saved properties, calendars, bookings and pending work are preserved. Old pending restrictions without a field mask remain full-state recovery records; do not treat them as evidence of a new field-only delta. Future schema changes need reviewed upgrades and migration tests.

The first version uses room-based prices and integer room quantities. It does not implement occupancy-based pricing, derived-rate inheritance, credit-card handling, payments, accounting, guest messaging, housekeeping, channel onboarding or a complete commercial PMS. Reconcile the exact supported fields with Channex before promising a client that their channel configuration is covered.

Future product work must retain the actual database-to-outbox-to-Channex paths used here. A separate certification-only UI or scripted submission will not satisfy the live review described by Channex.
