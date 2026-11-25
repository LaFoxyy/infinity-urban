using Stayline.Checks;

var checks = new (string Name, Func<Task> Run)[]
{
    ("Production changes only the host of approved Channex requests", ProductionEnvironmentChecks.EnvironmentChangesOnlyHostAndPreservesApprovedRequests),
    ("Unsafe environments and path escapes cannot send requests", ProductionEnvironmentChecks.UnknownEnvironmentsAndPathEscapesCannotSendRequests),
    ("Production databases are bound to their environment", ProductionEnvironmentChecks.FreshProductionDatabaseBindsAndCannotSwitchEnvironment),
    ("Staging mappings and queues cannot be reused in production", ProductionEnvironmentChecks.LegacyMappingsAndQueuesCannotBeReusedInProduction),
    ("Production HTTP logs redact API credentials", ProductionEnvironmentChecks.ProductionHttpTraceLogsStillRedactCredentialHeaders),
    ("Calendar state and outbox commit together", DomainChecks.CalendarWritesSavedStateAndOutboxTogether),
    ("Invalid calendar changes roll back the whole save", DomainChecks.InvalidSecondChangeRollsBackWholeCalendarSave),
    ("Overlapping calendar edits preserve the final saved state", DomainChecks.OverlappingCalendarEditsKeepOneCurrentRow),
    ("Reservation changes and cancellations use checkout-exclusive dates", DomainChecks.ReservationLifecycleUsesCheckoutExclusiveDates),
    ("Overbooking and invalid capacity changes cannot alter saved state", DomainChecks.OverbookingAndCapacityReductionAreRejectedAtomically),
    ("Rental inventory remains one per unit", DomainChecks.RentalUnitsHaveSingleInventory),
    ("500-day synchronization batches current state into two requests", IntegrationChecks.FullHorizonBatchesCurrentSavedState),
    ("HTTP 200 warnings preserve pending work", IntegrationChecks.WarningResponseDoesNotLosePendingChanges),
    ("Echoed API credentials are redacted before sync history is saved", IntegrationChecks.EchoedCredentialsNeverReachSyncHistory),
    ("HTTP trace logs redact the Channex API key header", IntegrationChecks.HttpClientLoggingRedactsApiHeader),
    ("HTTP 429 Retry-After delays retries without losing work", IntegrationChecks.RetryAfterBlocksImmediateResend),
    ("Missing mappings block delivery without losing work", IntegrationChecks.UnmappedRecordsStayPending),
    ("Rate limits persist across worker recreation", IntegrationChecks.DurableRateLimitSurvivesWorkerRecreation),
    ("Calendar edits saved during delivery cannot be lost", IntegrationChecks.EditsDuringDeliveryRemainQueued),
    ("Booking persistence precedes acknowledgment and ack retry is idempotent", BookingChecks.BookingIsCommittedBeforeAckAndAckRetriesAreIdempotent),
    ("Unmapped bookings cannot be acknowledged or partially imported", BookingChecks.InvalidBookingMappingPreventsAckAndAllBookingWrites),
    ("Channel booking revisions honor per-room dates and revision order", BookingChecks.PerRoomDatesAndOrderedRevisionsPreserveInventory),
    ("Feed polling recovers missed webhooks across all pages", BookingChecks.FeedRecoveryCollectsEveryPageWithoutDuplicatingReceipts),
    ("Price-only deltas preserve the selected rate and exact date", DeltaChecks.PriceOnlySaveTargetsExactlyTheChosenDateAndRate),
    ("Multi-rate saves preserve all selected mappings in one request", DeltaChecks.MultiRateSavePreservesEverySelectedMappingInOneRequest),
    ("Min-stay and false restriction changes send only selected fields", DeltaChecks.MinimumStayAndBooleanEditsContainOnlyRequestedRestrictions),
    ("Range compression respects gaps, entities, field masks and values", DeltaChecks.RangesMergeOnlyContiguousMatchingEntitiesMasksAndValues),
    ("Dirty field masks read current state and legacy queues retain full snapshots", DeltaChecks.DirtyMasksUnionLatestStateAndLegacyRowsRemainSafe),
    ("All rooms and plans retain500-day coverage in two merged full-sync requests", DeltaChecks.FullSyncCoversAllRoomsAndPlansUsingTwoMergedRequests),
    ("Explicit reapply repairs selected fields without manufacturing ordinary changes", DeltaChecks.ReapplyRepairsOnlyExplicitFieldsWithoutInventingStateChanges),
    ("Fields saved during delivery retain a separate later delta", DeltaChecks.NewFieldSavedDuringDeliveryKeepsItsOwnLaterDelta),
    ("Incoming new/modified/cancelled lifecycle uses only feed then acknowledgment", BookingChecks.CompleteLifecycleUsesOnlyFeedThenAckAndKeepsLabeledEvidence),
    ("Feed replay snapshots strip payment and guest-contact data before persistence", BookingChecks.FeedSnapshotsStripCardsAndContactDataBeforePersistence),
    ("Accidental booking list and by-ID reads are blocked before transport", BookingChecks.BookingListAndByIdReadsAreBlockedBeforeTransport),
    ("Normal incoming-booking recovery exposes safe labeled receipt evidence", IncomingHttpChecks.RecoveryEndpointImportsFeedAndExposesSafeLabeledReceipts),
    ("Authenticated webhook notifications pull authoritative feed snapshots", IncomingHttpChecks.AuthenticatedWebhookStoresOnlyNotificationAndThenUsesFeed),
    ("Additive schema upgrades preserve existing queues and booking history", SchemaChecks.AdditiveUpgradeKeepsLegacyPendingWorkAndBookingHistory),
    ("Real HTTP routes persist calendar changes and the reservation lifecycle", HttpChecks.RealEndpointsPersistCalendarAndReservationLifecycle),
    ("Real HTTP middleware rejects cross-origin writes and malformed requests", HttpChecks.MiddlewareRejectsCrossOriginAndMalformedRequests),
    ("Malformed mapping requests cannot change saved mappings", HttpChecks.MalformedMappingCannotAlterSavedMappings),
    ("Staging discovery and import preview are read-only", OnboardingChecks.DiscoveryAndPreviewNeverCreateOrPublishLocalData),
    ("Staging import copies 500 days into a separate unmapped draft", OnboardingChecks.ImportCopiesCompleteSavedStateAsAnUnmappedDraft),
    ("Repeated staging imports and existing mappings cannot duplicate properties", OnboardingChecks.RepeatedImportAndAlreadyMappedPropertiesDoNotDuplicateRecords),
    ("Incomplete or invalid staging snapshots leave no partial imported state", OnboardingChecks.InvalidOrIncompleteSnapshotsCannotLeavePartialImportedState),
    ("Unpriced staging preview reports exact dates and a real candidate", OnboardingChecks.PreviewReportsZeroPriceDatesWithoutInventingADefault),
    ("Explicit defaults initialize only zero prices in an unmapped draft", OnboardingChecks.ExplicitDefaultsFillOnlyZeroPricesAndPersistAsLocalBasePrices),
    ("Entirely unpriced plans require user input and have no guessed candidate", OnboardingChecks.EntirelyUnpricedPlansRequireUserDefaultsAndHaveNoSuggestedPrice),
    ("Invalid or foreign price defaults leave no partial imported state", OnboardingChecks.InvalidOrForeignPriceDefaultsCannotCreatePartialDrafts),
    ("Full sync and mapping reject incomplete setup without queueing work", OnboardingChecks.FullSyncAndMappingRejectIncompleteSetupWithoutQueuingWork),
    ("Complete mapping queues outgoing ARI without a webhook secret", OnboardingChecks.CompleteMappingQueuesSavedStateWithoutAWebhookSecret),
    ("Local unmapped calendars preserve independent properties and every restriction", ProvisioningChecks.LocalCalendarsWorkBeforeMappingAndKeepAllRestrictions),
    ("Provisioning creates actual saved property, rooms and rates before publishing ARI", ProvisioningChecks.ProvisionUsesSavedEntitiesAndPublishesSavedCalendar),
    ("Provisioning resolves missing-mapping blocks while preserving timed backoff", ProvisioningChecks.ProvisionUnblocksMissingMappingsWithoutResettingTimedBackoff),
    ("Repeated and concurrent provisioning cannot duplicate remote entities", ProvisioningChecks.RepeatedAndConcurrentProvisionDoesNotDuplicateRemoteEntities),
    ("Definite creation errors resume from committed mappings", ProvisioningChecks.KnownCreateFailureResumesFromPersistedMappings),
    ("An unknown create outcome blocks unsafe duplicate retries", ProvisioningChecks.UnknownCreateOutcomeBlocksBlindRetry),
    ("Provisioning verifies existing remote mapping ancestry", ProvisioningChecks.ForeignMappingsCannotBeUsedForProvision)
};

var failed = 0;
foreach (var (name, run) in checks)
{
    try { await run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {name}\n{ex}"); }
}
Console.WriteLine($"{checks.Length - failed}/{checks.Length} checks passed. No real Channex calls were made.");
return failed == 0 ? 0 : 1;
