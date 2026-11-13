using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using PH.SapB1.Vaniperen.Sap;

namespace PH.SapB1.Vaniperen.Orders;

public sealed class SapOrderSubmitService
{
    private readonly SapServiceLayerRawClient _client;
    private readonly OrderPostingOptions _options;
    private readonly ILogger<SapOrderSubmitService> _logger;

    public SapOrderSubmitService(
        SapServiceLayerRawClient client,
        IOptions<OrderPostingOptions> options,
        ILogger<SapOrderSubmitService> logger)
    {
        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<SubmitOrderResponse> SubmitAsync(
    SubmitOrderRequest request,
    string username,
    List<string> allowedCardCodes,
    string placeholderItemCode,
    CancellationToken cancellationToken)
    {
        var receivedAt = DateTimeOffset.UtcNow;
        var warnings = new List<SubmitOrderMessage>();

        var parsed = ValidateBaseRequest(request);

        var billToParty = parsed.BillToParty;

        if (!allowedCardCodes.Contains(
                billToParty,
                StringComparer.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "ORDER SUBMIT FORBIDDEN BillToParty={BillToParty} Username={Username} AllowedCardCodes={AllowedCardCodes}",
                billToParty,
                username,
                string.Join(",", allowedCardCodes));

            throw new UnauthorizedAccessException(
                "The supplied billToParty is not allowed for this API user.");
        }

        _logger.LogInformation(
            "ORDER SUBMIT START CustomerOrderNumber={CustomerOrderNumber} BillToParty={BillToParty} ShipToGLN={ShipToGLN} Username={Username}",
            parsed.CustomerOrderNumber,
            billToParty,
            request.ShipToGLN,
            username);

        var businessPartner = await GetBusinessPartnerContextAsync(
            billToParty,
            cancellationToken);

        var deliveryDate = ResolveDeliveryDate(
            request.DeliveryDate,
            warnings);

        var shipToCode = ResolveShipToCode(
            request.ShipToGLN,
            businessPartner,
            warnings);

        var processedLines = await ProcessLinesAsync(
    request.Lines,
    placeholderItemCode,
    warnings,
    cancellationToken);
        _logger.LogInformation(
    "ORDER PLACEHOLDER CONFIG Username={Username} PlaceholderItemCode={PlaceholderItemCode}",
    username,
    placeholderItemCode);

        if (processedLines.Count == 0)
        {
            throw new OrderSubmitUnprocessableException(
                "ORDER_HAS_NO_VALID_LINES",
                "All order lines have a zero or blank quantity, or no line items were submitted. No SAP document was created.");
        }

        var existingDocument = await FindExistingDocumentAsync(
            parsed.CustomerOrderNumber,
            allowedCardCodes,
            cancellationToken);

        if (existingDocument is not null &&
            !string.Equals(
                existingDocument.CardCode,
                billToParty,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new OrderSubmitConflictException(
                $"Customer order number '{parsed.CustomerOrderNumber}' already exists against CardCode '{existingDocument.CardCode}', not '{billToParty}'.");
        }

        var orderStatus = ResolveOrderStatus(
            parsed.OrderDateTime,
            deliveryDate,
            businessPartner);

        var integrationId = GenerateIntegrationId(receivedAt);

        _logger.LogInformation(
            "ORDER SUBMIT DECISION CustomerOrderNumber={CustomerOrderNumber} ExistingObject={ExistingObject} ExistingDocEntry={ExistingDocEntry} DeliveryDate={DeliveryDate} ShipToCode={ShipToCode} OrderStatus={OrderStatus} ValidLineCount={ValidLineCount}",
            parsed.CustomerOrderNumber,
            existingDocument?.SapObject ?? "NONE",
            existingDocument?.DocEntry ?? 0,
            deliveryDate,
            shipToCode ?? "",
            orderStatus,
            processedLines.Count);

        if (existingDocument is null)
        {
            // Shipping date is calculated only when the draft is first created.
            var shippingDate = ResolveShippingDate(
                parsed.OrderDateTime,
                deliveryDate,
                businessPartner,
                warnings);

            var createPayload = BuildSapPayload(
                request,
                parsed,
                deliveryDate,
                shippingDate,
                processedLines,
                billToParty,
                shipToCode,
                username,
                integrationId,
                orderStatus,
                isCreateDraft: true);

            var createResult = await _client.SendAsync(
                HttpMethod.Post,
                "Drafts",
                createPayload,
                cancellationToken);

            if (!createResult.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    "SAP Service Layer failed while creating the sales order draft: " +
                    createResult.Body);
            }

            var createdObject = ParseObject(createResult.Body);

            var docEntry = GetInt(createdObject, "DocEntry");

            _logger.LogInformation(
                "ORDER SUBMIT CREATED CustomerOrderNumber={CustomerOrderNumber} IntegrationId={IntegrationId} SapObject=Drafts DocEntry={DocEntry}",
                parsed.CustomerOrderNumber,
                integrationId,
                docEntry);

            return BuildResponse(
                integrationId,
                operationType: "CREATE",
                parsed.CustomerOrderNumber,
                sapObject: "Drafts",
                docEntry,
                orderStatus,
                receivedAt,
                warnings);
        }

        // Updates never touch the shipping date (DocDueDate): it keeps the value
        // calculated at creation, or whatever the user has since set in SAP.
        var updatePayload = BuildSapPayload(
            request,
            parsed,
            deliveryDate,
            shippingDate: null,
            processedLines,
            billToParty,
            shipToCode,
            username,
            integrationId,
            orderStatus,
            isCreateDraft: false);

        // Skip the update when the request carries nothing new compared with SAP.
        var unchangedResponse = await TryBuildNoChangeResponseAsync(
            existingDocument,
            updatePayload,
            parsed.CustomerOrderNumber,
            orderStatus,
            integrationId,
            receivedAt,
            warnings,
            cancellationToken);

        if (unchangedResponse is not null)
        {
            return unchangedResponse;
        }

        var headers = new Dictionary<string, string>
        {
            ["B1S-ReplaceCollectionsOnPatch"] = "true"
        };

        var updateResult = await _client.SendAsync(
            HttpMethod.Patch,
            $"{existingDocument.SapObject}({existingDocument.DocEntry})",
            updatePayload,
            cancellationToken,
            headers);

        if (!updateResult.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"SAP Service Layer failed while updating {existingDocument.SapObject}: " +
                updateResult.Body);
        }

        var operationType =
            existingDocument.SapObject.Equals(
                "Orders",
                StringComparison.OrdinalIgnoreCase)
                ? "UPDATE_ORDER"
                : "UPDATE_DRAFT";

        // SAP (e.g. a transaction notification) can change the status or the
        // Confirmed tick after our PATCH. Make sure the update really leaves the
        // document unconfirmed with OOR/TBC, and report what SAP actually holds.
        orderStatus = await EnsureUpdatedStatusAsync(
            existingDocument,
            orderStatus,
            warnings,
            cancellationToken);

        _logger.LogInformation(
            "ORDER SUBMIT UPDATED CustomerOrderNumber={CustomerOrderNumber} IntegrationId={IntegrationId} OperationType={OperationType} SapObject={SapObject} DocEntry={DocEntry} OrderStatus={OrderStatus}",
            parsed.CustomerOrderNumber,
            integrationId,
            operationType,
            existingDocument.SapObject,
            existingDocument.DocEntry,
            orderStatus);

        return BuildResponse(
            integrationId,
            operationType,
            parsed.CustomerOrderNumber,
            existingDocument.SapObject,
            existingDocument.DocEntry,
            orderStatus,
            receivedAt,
            warnings);
    }

    private async Task<SubmitOrderResponse?> TryBuildNoChangeResponseAsync(
        ExistingSapDocument document,
        JsonObject updatePayload,
        string customerOrderNumber,
        string computedStatus,
        string newIntegrationId,
        DateTimeOffset receivedAt,
        List<SubmitOrderMessage> warnings,
        CancellationToken cancellationToken)
    {
        var ignoredHeaderKeys = GetNonBusinessHeaderKeys();

        var headerKeys = updatePayload
            .Select(x => x.Key)
            .Where(x => !ignoredHeaderKeys.Contains(x))
            .ToList();

        var selectFields = headerKeys
            .Append(_options.OrderStatusField)
            .Append(_options.IntegrationIdField)
            .Append("DocumentLines")
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var documentUrl = $"{document.SapObject}({document.DocEntry})";

        var response = await _client.SendAsync(
            HttpMethod.Get,
            $"{documentUrl}?$select={string.Join(",", selectFields)}",
            null,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // Can't compare: fall back to a normal update.
            _logger.LogWarning(
                "ORDER CHANGE CHECK SKIPPED Url={Url} Body={Body}",
                documentUrl,
                response.Body);

            return null;
        }

        var existing = ParseObject(response.Body);

        var differences = FindPayloadDifferences(
            updatePayload,
            existing,
            ignoredHeaderKeys);

        if (differences.Count > 0)
        {
            _logger.LogInformation(
                "ORDER CHANGE CHECK CHANGED CustomerOrderNumber={CustomerOrderNumber} SapObject={SapObject} DocEntry={DocEntry} Differences={Differences}",
                customerOrderNumber,
                document.SapObject,
                document.DocEntry,
                string.Join(" | ", differences.Take(20)));

            return null;
        }

        var existingStatus = GetString(existing, _options.OrderStatusField).Trim();
        var existingIntegrationId = GetString(existing, _options.IntegrationIdField).Trim();

        var status = existingStatus.Length > 0 ? existingStatus : computedStatus;
        var integrationId = existingIntegrationId.Length > 0 ? existingIntegrationId : newIntegrationId;

        _logger.LogInformation(
            "ORDER CHANGE CHECK UNCHANGED CustomerOrderNumber={CustomerOrderNumber} SapObject={SapObject} DocEntry={DocEntry} OrderStatus={OrderStatus}. Update skipped.",
            customerOrderNumber,
            document.SapObject,
            document.DocEntry,
            status);

        return BuildResponse(
            integrationId,
            operationType: "NO_CHANGE",
            customerOrderNumber,
            document.SapObject,
            document.DocEntry,
            status,
            receivedAt,
            warnings);
    }

    // Header fields we write for tracking/workflow, not business content.
    // A request that only differs in these counts as "no change".
    private HashSet<string> GetNonBusinessHeaderKeys()
    {
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Confirmed",
            "DocObjectCode",
            "DocumentLines",
            _options.OrderStatusField,
            _options.IntegrationIdField,
            _options.ApiUserField,
            _options.ProcessingStatusField
        };
    }

    // Compares every business field we would send with what SAP holds.
    // Returns a readable list of differences (empty = nothing to update).
    internal static List<string> FindPayloadDifferences(
        JsonObject payload,
        JsonObject existing,
        ISet<string> ignoredHeaderKeys)
    {
        var differences = new List<string>();

        foreach (var (key, value) in payload)
        {
            if (ignoredHeaderKeys.Contains(key))
            {
                continue;
            }

            var sent = NormalizeForCompare(value);
            var stored = NormalizeForCompare(existing[key]);

            if (!string.Equals(sent, stored, StringComparison.Ordinal))
            {
                differences.Add($"{key}: '{stored}' -> '{sent}'");
            }
        }

        var sentLines = payload["DocumentLines"] as JsonArray ?? new JsonArray();

        var storedLines = (existing["DocumentLines"] as JsonArray ?? new JsonArray())
            .OfType<JsonObject>()
            .OrderBy(x => NormalizeForCompare(x["LineNum"]) is var n &&
                          int.TryParse(n, out var lineNum) ? lineNum : int.MaxValue)
            .ToList();

        if (sentLines.Count != storedLines.Count)
        {
            differences.Add($"Line count: {storedLines.Count} -> {sentLines.Count}");
            return differences;
        }

        for (var i = 0; i < sentLines.Count; i++)
        {
            if (sentLines[i] is not JsonObject sentLine)
            {
                continue;
            }

            foreach (var (key, value) in sentLine)
            {
                var sent = NormalizeForCompare(value);
                var stored = NormalizeForCompare(storedLines[i][key]);

                if (!string.Equals(sent, stored, StringComparison.Ordinal))
                {
                    differences.Add($"Line {i + 1} {key}: '{stored}' -> '{sent}'");
                }
            }
        }

        return differences;
    }

    // Makes SAP and request values comparable:
    // null/"" are equal, numbers compare by value (10 = 10.0),
    // dates compare by day ("2026-11-10" = "2026-11-10T00:00:00Z").
    internal static string NormalizeForCompare(JsonNode? node)
    {
        if (node is null)
        {
            return "";
        }

        if (node is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<string>(out var text))
            {
                text = text.Trim();

                if (text.Length >= 10 &&
                    DateOnly.TryParseExact(
                        text[..10],
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out _) &&
                    (text.Length == 10 || text[10] == 'T' || text[10] == ' '))
                {
                    return text[..10];
                }

                return text;
            }

            if (decimal.TryParse(
                    jsonValue.ToJsonString(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var number))
            {
                return number.ToString("0.############", CultureInfo.InvariantCulture);
            }
        }

        return node.ToJsonString();
    }

    private async Task<string> EnsureUpdatedStatusAsync(
        ExistingSapDocument document,
        string expectedStatus,
        List<SubmitOrderMessage> warnings,
        CancellationToken cancellationToken)
    {
        var documentUrl = $"{document.SapObject}({document.DocEntry})";

        var (status, confirmed) = await ReadStatusAsync(
            documentUrl,
            cancellationToken);

        if (IsExpectedUpdateState(status, confirmed, expectedStatus))
        {
            return expectedStatus;
        }

        _logger.LogWarning(
            "ORDER STATUS OVERWRITTEN AFTER UPDATE SapObject={SapObject} DocEntry={DocEntry} ExpectedStatus={ExpectedStatus} ActualStatus={ActualStatus} Confirmed={Confirmed}. Re-applying.",
            document.SapObject,
            document.DocEntry,
            expectedStatus,
            status,
            confirmed);

        // Re-apply only the header status fields (no lines).
        var fixPayload = new JsonObject
        {
            ["Confirmed"] = "tNO",
            [_options.OrderStatusField] = expectedStatus
        };

        var fixResult = await _client.SendAsync(
            HttpMethod.Patch,
            documentUrl,
            fixPayload,
            cancellationToken);

        if (!fixResult.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "ORDER STATUS RE-APPLY FAILED SapObject={SapObject} DocEntry={DocEntry} Body={Body}",
                document.SapObject,
                document.DocEntry,
                fixResult.Body);
        }

        (status, confirmed) = await ReadStatusAsync(
            documentUrl,
            cancellationToken);

        if (IsExpectedUpdateState(status, confirmed, expectedStatus))
        {
            _logger.LogInformation(
                "ORDER STATUS RE-APPLIED SapObject={SapObject} DocEntry={DocEntry} Status={Status}",
                document.SapObject,
                document.DocEntry,
                expectedStatus);

            return expectedStatus;
        }

        _logger.LogError(
            "ORDER STATUS STILL OVERWRITTEN SapObject={SapObject} DocEntry={DocEntry} ExpectedStatus={ExpectedStatus} ActualStatus={ActualStatus} Confirmed={Confirmed}. Check SAP transaction notifications.",
            document.SapObject,
            document.DocEntry,
            expectedStatus,
            status,
            confirmed);

        warnings.Add(new SubmitOrderMessage
        {
            Type = "WARN",
            Code = "ORDER_STATUS_OVERWRITTEN_IN_SAP",
            Text =
                $"The order was updated with status '{expectedStatus}', but SAP changed it to '{status}' (Confirmed={confirmed})."
        });

        // Return what SAP really holds, so the caller is not misled.
        return string.IsNullOrWhiteSpace(status)
            ? expectedStatus
            : status;
    }

    private async Task<(string Status, string Confirmed)> ReadStatusAsync(
        string documentUrl,
        CancellationToken cancellationToken)
    {
        var response = await _client.SendAsync(
            HttpMethod.Get,
            $"{documentUrl}?$select=Confirmed,{_options.OrderStatusField}",
            null,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "ORDER STATUS READ-BACK FAILED Url={Url} Body={Body}",
                documentUrl,
                response.Body);

            // Can't verify; don't block the update.
            return ("", "");
        }

        var doc = ParseObject(response.Body);

        return (
            GetString(doc, _options.OrderStatusField).Trim(),
            GetString(doc, "Confirmed").Trim());
    }

    private static bool IsExpectedUpdateState(
        string status,
        string confirmed,
        string expectedStatus)
    {
        // Read-back failed: nothing to compare.
        if (status.Length == 0 && confirmed.Length == 0)
        {
            return true;
        }

        var unconfirmed =
            !confirmed.Equals("tYES", StringComparison.OrdinalIgnoreCase) &&
            !confirmed.Equals("Y", StringComparison.OrdinalIgnoreCase);

        return unconfirmed &&
               status.Equals(expectedStatus, StringComparison.OrdinalIgnoreCase);
    }

    private ParsedSubmitOrderRequest ValidateBaseRequest(
        SubmitOrderRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.CustomerOrderNumber))
        {
            throw new ArgumentException(
                "customerOrderNumber is required.");
        }

        if (string.IsNullOrWhiteSpace(request.OrderDateTime))
        {
            throw new ArgumentException(
                "orderDateTime is required.");
        }

        if (!DateTimeOffset.TryParse(
                request.OrderDateTime,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal |
                DateTimeStyles.AdjustToUniversal,
                out var orderDateTime))
        {
            throw new ArgumentException(
                "orderDateTime must be a valid ISO datetime, for example 2026-04-08T10:15:00Z.");
        }

        if (string.IsNullOrWhiteSpace(request.BillToParty))
        {
            throw new ArgumentException(
                "billToParty is required.");
        }

        if (request.Lines is null || request.Lines.Count == 0)
        {
            throw new OrderSubmitUnprocessableException(
                "ORDER_HAS_NO_LINES",
                "No line items were submitted. No SAP document was created.");
        }

        return new ParsedSubmitOrderRequest
        {
            CustomerOrderNumber = request.CustomerOrderNumber.Trim(),
            OrderDateTime = orderDateTime,
            BillToParty = request.BillToParty.Trim()
        };
    }

    private DateOnly ResolveDeliveryDate(
        string? submittedDeliveryDate,
        List<SubmitOrderMessage> warnings)
    {
        var businessTimeZone = ResolveTimeZone(
            _options.BusinessTimeZone);

        var localNow = TimeZoneInfo.ConvertTime(
            DateTimeOffset.UtcNow,
            businessTimeZone);

        var today = DateOnly.FromDateTime(localNow.DateTime);

        if (string.IsNullOrWhiteSpace(submittedDeliveryDate))
        {
            warnings.Add(new SubmitOrderMessage
            {
                Type = "WARN",
                Code = "DELIVERY_DATE_DEFAULTED",
                Text =
                    $"Delivery date was missing and has been defaulted to {today:yyyy-MM-dd}."
            });

            _logger.LogWarning(
                "ORDER VALIDATION Delivery date missing. DefaultDate={DefaultDate}",
                today);

            return today;
        }

        if (!DateOnly.TryParseExact(
                submittedDeliveryDate,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var deliveryDate))
        {
            throw new ArgumentException(
                "deliveryDate must be in YYYY-MM-DD format when supplied.");
        }

        if (deliveryDate < today)
        {
            warnings.Add(new SubmitOrderMessage
            {
                Type = "WARN",
                Code = "DELIVERY_DATE_IN_PAST",
                Text =
                    $"Delivery date {deliveryDate:yyyy-MM-dd} is in the past. The order was processed using the submitted date."
            });

            _logger.LogWarning(
                "ORDER VALIDATION Delivery date is in the past. DeliveryDate={DeliveryDate} Today={Today}",
                deliveryDate,
                today);
        }

        return deliveryDate;
    }

    private string? ResolveShipToCode(
        string? shipToGln,
        BusinessPartnerContext businessPartner,
        List<SubmitOrderMessage> warnings)
    {
        if (string.IsNullOrWhiteSpace(shipToGln))
        {
            warnings.Add(new SubmitOrderMessage
            {
                Type = "WARN",
                Code = "SHIP_TO_GLN_MISSING",
                Text =
                    "shipToGLN was not supplied. The order was processed without overriding the customer's default Ship-To address."
            });

            _logger.LogWarning(
                "ORDER VALIDATION Ship-to GLN missing. CardCode={CardCode}",
                businessPartner.CardCode);

            return null;
        }

        var cleanGln = shipToGln.Trim();

        var matchingAddress = businessPartner.Addresses
            .FirstOrDefault(x =>
                IsShipToAddressType(x.AddressType) &&
                string.Equals(
                    x.GlobalLocationNumber?.Trim(),
                    cleanGln,
                    StringComparison.OrdinalIgnoreCase));

        if (matchingAddress is null ||
            string.IsNullOrWhiteSpace(matchingAddress.AddressName))
        {
            warnings.Add(new SubmitOrderMessage
            {
                Type = "WARN",
                Code = "SHIP_TO_GLN_NOT_MATCHED",
                Text =
                    $"Ship-to GLN '{cleanGln}' was not matched against the customer's Ship-To addresses. The order was processed without overriding the default Ship-To address."
            });

            _logger.LogWarning(
                "ORDER VALIDATION Ship-to GLN not matched. CardCode={CardCode} ShipToGLN={ShipToGLN}",
                businessPartner.CardCode,
                cleanGln);

            return null;
        }

        _logger.LogInformation(
            "ORDER SHIP-TO MATCH CardCode={CardCode} ShipToGLN={ShipToGLN} ShipToCode={ShipToCode}",
            businessPartner.CardCode,
            cleanGln,
            matchingAddress.AddressName);

        return matchingAddress.AddressName;
    }

    private async Task<List<ProcessedOrderLine>> ProcessLinesAsync(
    List<SubmitOrderLineRequest> submittedLines,
    string placeholderItemCode,
    List<SubmitOrderMessage> warnings,
    CancellationToken cancellationToken)
    {
        var result = new List<ProcessedOrderLine>();

        var itemCache = new Dictionary<string, ItemValidationResult>(
            StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < submittedLines.Count; index++)
        {
            var line = submittedLines[index];

            var effectiveLineNumber =
                line.LineNumber.GetValueOrDefault(index + 1);

            if (!line.Quantity.HasValue || line.Quantity.Value == 0)
            {
                warnings.Add(new SubmitOrderMessage
                {
                    Type = "WARN",
                    Code = "LINE_SKIPPED_ZERO_QUANTITY",
                    Text =
                        $"Line {effectiveLineNumber} was skipped because its quantity was zero or blank."
                });

                _logger.LogWarning(
                    "ORDER LINE SKIPPED LineNumber={LineNumber} Reason=ZERO_OR_BLANK_QUANTITY ItemCode={ItemCode}",
                    effectiveLineNumber,
                    line.ItemCode);

                continue;
            }

            if (line.Quantity.Value < 0)
            {
                throw new OrderSubmitUnprocessableException(
                    "NEGATIVE_LINE_QUANTITY",
                    $"Line {effectiveLineNumber} has a negative quantity. Negative quantities are not allowed.");
            }

            if (string.IsNullOrWhiteSpace(line.ShipDate) ||
                !DateOnly.TryParseExact(
                    line.ShipDate,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var shipDate))
            {
                throw new OrderSubmitUnprocessableException(
                    "INVALID_LINE_SHIP_DATE",
                    $"Line {effectiveLineNumber} must contain shipDate in YYYY-MM-DD format.");
            }

            var submittedItemCode = line.ItemCode?.Trim() ?? "";

            var itemValidation = await GetItemValidationAsync(
                submittedItemCode,
                itemCache,
                cancellationToken);

            var finalItemCode = submittedItemCode;

            if (!itemValidation.IsActiveSalesItem)
            {
                finalItemCode = await GetValidPlaceholderItemAsync(
     placeholderItemCode,
     itemCache,
     cancellationToken);

                var reason = itemValidation.Exists
                    ? "inactive or not enabled as a sales item"
                    : "unknown";

                warnings.Add(new SubmitOrderMessage
                {
                    Type = "WARN",
                    Code = "PRODUCT_SUBSTITUTED",
                    Text =
                        $"Line {effectiveLineNumber}: product code '{submittedItemCode}' is {reason} and was replaced with placeholder item '{finalItemCode}'."
                });

                _logger.LogWarning(
                    "ORDER ITEM SUBSTITUTED LineNumber={LineNumber} OriginalItemCode={OriginalItemCode} PlaceholderItemCode={PlaceholderItemCode} Reason={Reason}",
                    effectiveLineNumber,
                    submittedItemCode,
                    finalItemCode,
                    reason);
            }

            // Cost centre comes from the final item (the placeholder when substituted).
            var finalItem = await GetItemValidationAsync(
                finalItemCode,
                itemCache,
                cancellationToken);

            result.Add(new ProcessedOrderLine
            {
                LineNumber = effectiveLineNumber,
                ItemCode = finalItemCode,
                ItemDescription = line.ItemDescription?.Trim(),
                Quantity = line.Quantity.Value,
                ShipDate = shipDate,
                CostCenter = finalItem.CostCenter
            });
        }

        return result;
    }

    private async Task<string> GetValidPlaceholderItemAsync(
     string placeholderItemCode,
     Dictionary<string, ItemValidationResult> itemCache,
     CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(placeholderItemCode))
        {
            throw new InvalidOperationException(
                "No placeholder item is configured for the authenticated API user.");
        }

        var cleanPlaceholderItemCode =
            placeholderItemCode.Trim();

        var validation = await GetItemValidationAsync(
            cleanPlaceholderItemCode,
            itemCache,
            cancellationToken);

        if (!validation.IsActiveSalesItem)
        {
            _logger.LogError(
                "ORDER PLACEHOLDER INVALID PlaceholderItemCode={PlaceholderItemCode} Exists={Exists} IsActiveSalesItem={IsActiveSalesItem}",
                cleanPlaceholderItemCode,
                validation.Exists,
                validation.IsActiveSalesItem);

            throw new InvalidOperationException(
                $"Configured placeholder item '{cleanPlaceholderItemCode}' for the authenticated API user does not exist, is inactive, frozen, or is not enabled as a sales item.");
        }

        return cleanPlaceholderItemCode;
    }

    private async Task<ItemValidationResult> GetItemValidationAsync(
        string itemCode,
        Dictionary<string, ItemValidationResult> cache,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(itemCode))
        {
            return ItemValidationResult.NotFound();
        }

        if (cache.TryGetValue(itemCode, out var cached))
        {
            return cached;
        }

        var relativeUrl =
            $"Items('{EscapeODataKey(itemCode)}')" +
            "?$select=ItemCode,Valid,Frozen,SalesItem" +
            (string.IsNullOrWhiteSpace(_options.ItemCostCenterField)
                ? ""
                : "," + _options.ItemCostCenterField);

        var response = await _client.SendAsync(
            HttpMethod.Get,
            relativeUrl,
            null,
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            var missing = ItemValidationResult.NotFound();
            cache[itemCode] = missing;
            return missing;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"SAP Service Layer failed while validating item '{itemCode}': {response.Body}");
        }

        var item = ParseObject(response.Body);

        var valid = GetString(item, "Valid");
        var frozen = GetString(item, "Frozen");
        var salesItem = GetString(item, "SalesItem");

        var result = new ItemValidationResult
        {
            Exists = true,
            IsActiveSalesItem =
                IsYes(valid) &&
                !IsYes(frozen) &&
                IsYes(salesItem),
            CostCenter =
                string.IsNullOrWhiteSpace(_options.ItemCostCenterField)
                    ? ""
                    : GetString(item, _options.ItemCostCenterField).Trim()
        };

        cache[itemCode] = result;

        return result;
    }

    private async Task<BusinessPartnerContext>
        GetBusinessPartnerContextAsync(
            string cardCode,
            CancellationToken cancellationToken)
    {
        var relativeUrl =
            $"BusinessPartners('{EscapeODataKey(cardCode)}')";

        var response = await _client.SendAsync(
            HttpMethod.Get,
            relativeUrl,
            null,
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new OrderSubmitUnprocessableException(
                "BILL_TO_PARTY_NOT_FOUND",
                $"billToParty '{cardCode}' does not exist in SAP Business One.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"SAP Service Layer failed while reading billToParty '{cardCode}': {response.Body}");
        }

        var obj = ParseObject(response.Body);

        var context = new BusinessPartnerContext
        {
            CardCode = GetString(obj, "CardCode"),
            //LimitDateRaw = GetString(obj, "U_LimitDateOrder"),
            LimitTimeRaw = GetString(obj, "U_LimitOrder")
        };

        // Shipping lead days UDF (case-insensitive lookup). Record whether SAP
        // returned the field at all, so a wrong field name or a Service Layer
        // that has not picked up the new UDF is reported instead of silently
        // treated as 0 days.
        var leadDaysField = _options.ShippingLeadDaysField?.Trim() ?? "";

        if (leadDaysField.Length > 0)
        {
            var match = obj.FirstOrDefault(x =>
                string.Equals(x.Key, leadDaysField, StringComparison.OrdinalIgnoreCase));

            context.ShippingLeadDaysFieldFound = match.Key is not null;

            if (match.Key is not null)
            {
                context.ShippingLeadDaysRaw = GetString(obj, match.Key);
            }
            else
            {
                context.CandidateShippingFields = obj
                    .Select(x => x.Key)
                    .Where(x => x.StartsWith("U_", StringComparison.OrdinalIgnoreCase) &&
                                (x.Contains("Ship", StringComparison.OrdinalIgnoreCase) ||
                                 x.Contains("Lead", StringComparison.OrdinalIgnoreCase) ||
                                 x.Contains("Day", StringComparison.OrdinalIgnoreCase)))
                    .ToList();
            }
        }

        if (obj["BPAddresses"] is JsonArray addresses)
        {
            foreach (var addressNode in addresses)
            {
                if (addressNode is not JsonObject address)
                {
                    continue;
                }

                context.Addresses.Add(new BusinessPartnerAddress
                {
                    AddressName = GetString(
                        address,
                        "AddressName"),

                    AddressType = GetString(
                        address,
                        "AddressType"),

                    GlobalLocationNumber = GetString(
                        address,
                        "GlobalLocationNumber")
                });
            }
        }

        return context;
    }

    private async Task<ExistingSapDocument?>
        FindExistingDocumentAsync(
            string customerOrderNumber,
            List<string> allowedCardCodes,
            CancellationToken cancellationToken)
    {
        var orders =
            await FindDocumentsByCustomerOrderNumberAsync(
                "Orders",
                customerOrderNumber,
                allowedCardCodes,
                cancellationToken);

        if (orders.Count > 1)
        {
            throw new OrderSubmitConflictException(
                $"Multiple Sales Orders were found for customerOrderNumber '{customerOrderNumber}'.");
        }

        if (orders.Count == 1)
        {
            return orders[0];
        }

        var drafts =
            await FindDocumentsByCustomerOrderNumberAsync(
                "Drafts",
                customerOrderNumber,
                allowedCardCodes,
                cancellationToken);

        if (drafts.Count > 1)
        {
            throw new OrderSubmitConflictException(
                $"Multiple Sales Order Drafts were found for customerOrderNumber '{customerOrderNumber}'.");
        }

        return drafts.FirstOrDefault();
    }

    private async Task<List<ExistingSapDocument>>
        FindDocumentsByCustomerOrderNumberAsync(
            string sapObject,
            string customerOrderNumber,
            List<string> allowedCardCodes,
            CancellationToken cancellationToken)
    {
        var filterParts = new List<string>
        {
            $"{_options.CustomerOrderNumberField} eq '{EscapeODataString(customerOrderNumber)}'",
            BuildCardCodeFilter(allowedCardCodes)
        };

        if (sapObject.Equals(
                "Drafts",
                StringComparison.OrdinalIgnoreCase))
        {
            filterParts.Add(
                "DocObjectCode eq 'oOrders'");
        }

        var filter = string.Join(
            " and ",
            filterParts);

        var relativeUrl =
            $"{sapObject}" +
            "?$select=DocEntry,CardCode" +
            "&$filter=" +
            Uri.EscapeDataString(filter) +
            "&$top=5";

        var response = await _client.SendAsync(
            HttpMethod.Get,
            relativeUrl,
            null,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"SAP Service Layer failed while searching {sapObject}: {response.Body}");
        }

        var root = JsonNode.Parse(response.Body);
        var values = root?["value"]?.AsArray();

        var result = new List<ExistingSapDocument>();

        if (values is null)
        {
            return result;
        }

        foreach (var node in values)
        {
            if (node is not JsonObject obj)
            {
                continue;
            }

            result.Add(new ExistingSapDocument
            {
                SapObject = sapObject,
                DocEntry = GetInt(obj, "DocEntry"),
                CardCode = GetString(obj, "CardCode")
            });
        }

        return result;
    }

    //Commented by pritesh
    //private string ResolveOrderStatus(
    //    DateTimeOffset orderDateTime,
    //    BusinessPartnerContext businessPartner)
    //{

    //    if (string.IsNullOrWhiteSpace(
    //            businessPartner.LimitDateRaw) ||
    //        string.IsNullOrWhiteSpace(
    //            businessPartner.LimitTimeRaw))
    //    {
    //        return _options.MissingLimitStatus;
    //    }



    //    if (!TryParseSapDate(
    //            businessPartner.LimitDateRaw,
    //            out var limitDate) ||
    //        !TryParseLimitTime(
    //            businessPartner.LimitTimeRaw,
    //            out var limitTime))
    //    {
    //        return _options.MissingLimitStatus;
    //    }


    //    var timeZone = ResolveTimeZone(
    //        _options.BusinessTimeZone);

    //    var localOrderDateTime =
    //        TimeZoneInfo.ConvertTime(
    //            orderDateTime,
    //            timeZone);

    //    var submittedDate =
    //        DateOnly.FromDateTime(
    //            localOrderDateTime.DateTime);

    //    var submittedTime =
    //        TimeOnly.FromDateTime(
    //            localOrderDateTime.DateTime);

    //    if (submittedDate < limitDate)
    //    {
    //        return "TBC";
    //    }

    //    if (submittedDate > limitDate)
    //    {
    //        return "OOR";
    //    }

    //    return submittedTime <= limitTime
    //        ? "TBC"
    //        : "OOR";
    //}

    private string ResolveOrderStatus(
        DateTimeOffset orderDateTime,
        DateOnly deliveryDate,
        BusinessPartnerContext businessPartner)
    {
        // Numeric limit (allowed number of days) maintained on the Business Partner.
        // Example: 1 or 2
        if (string.IsNullOrWhiteSpace(businessPartner.LimitTimeRaw))
        {
            return _options.MissingLimitStatus;
        }

        var rawLimit = businessPartner.LimitTimeRaw.Trim();

        // SAP may return the value as "2" or "2.0"
        if (!int.TryParse(rawLimit, NumberStyles.Integer, CultureInfo.InvariantCulture, out var limitDays))
        {
            if (decimal.TryParse(rawLimit, NumberStyles.Number, CultureInfo.InvariantCulture, out var limitDecimal))
            {
                limitDays = (int)Math.Floor(limitDecimal);
            }
            else
            {
                return _options.MissingLimitStatus;
            }
        }

        if (limitDays < 0)
        {
            return _options.MissingLimitStatus;
        }

        var timeZone = ResolveTimeZone(_options.BusinessTimeZone);

        // Order date (DocDate) in business time zone
        var orderDate = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(orderDateTime, timeZone).DateTime);

        // Notice given = days from order date to requested delivery date (TaxDate).
        var noticeDays = deliveryDate.DayNumber - orderDate.DayNumber;

        _logger.LogInformation(
            "ORDER STATUS CHECK CardCode={CardCode} OrderDate={OrderDate} DeliveryDate={DeliveryDate} NoticeDays={NoticeDays} LimitDays={LimitDays}",
            businessPartner.CardCode,
            orderDate,
            deliveryDate,
            noticeDays,
            limitDays);

        return IsShortNotice(noticeDays, limitDays)
            ? "OOR"
            : "TBC";
    }

    // Short notice = delivery requested sooner than the customer's limit allows.
    // Example: limit = 2, order placed 2026-11-06
    //   delivery 2026-11-06 -> 0 days -> OOR
    //   delivery 2026-11-07 -> 1 day  -> OOR
    //   delivery 2026-11-08 -> 2 days -> TBC
    //   delivery 2026-11-20 -> 14 days -> TBC
    internal static bool IsShortNotice(int noticeDays, int limitDays)
    {
        return noticeDays < limitDays;
    }

    // Shipping Date (DocDueDate) = Delivery Date (TaxDate) - BP lead days.
    // Called only when the draft is created.
    // Example: delivery 2026-10-20, U_ShipLeadDays = 10 -> shipping 2026-10-10.
    private DateOnly ResolveShippingDate(
        DateTimeOffset orderDateTime,
        DateOnly deliveryDate,
        BusinessPartnerContext businessPartner,
        List<SubmitOrderMessage> warnings)
    {
        var raw = businessPartner.ShippingLeadDaysRaw?.Trim() ?? "";

        if (!businessPartner.ShippingLeadDaysFieldFound &&
            !string.IsNullOrWhiteSpace(_options.ShippingLeadDaysField))
        {
            var candidates = businessPartner.CandidateShippingFields.Count > 0
                ? $" Similar fields found on the Business Partner: {string.Join(", ", businessPartner.CandidateShippingFields)}."
                : "";

            warnings.Add(new SubmitOrderMessage
            {
                Type = "WARN",
                Code = "SHIPPING_LEAD_DAYS_FIELD_NOT_FOUND",
                Text =
                    $"SAP did not return the Business Partner field '{_options.ShippingLeadDaysField}', so the shipping date was set to the delivery date {deliveryDate:yyyy-MM-dd}. Check the field name in OrderPosting:ShippingLeadDaysField, or restart the SAP Service Layer if the field was just created.{candidates}"
            });

            _logger.LogWarning(
                "ORDER SHIPPING DATE Field not returned by SAP. CardCode={CardCode} Field={Field} Candidates={Candidates}",
                businessPartner.CardCode,
                _options.ShippingLeadDaysField,
                string.Join(",", businessPartner.CandidateShippingFields));
        }

        if (!TryParseShippingLeadDays(raw, out var leadDays))
        {
            warnings.Add(new SubmitOrderMessage
            {
                Type = "WARN",
                Code = "SHIPPING_LEAD_DAYS_INVALID",
                Text =
                    $"The customer's shipping lead days value '{raw}' is not a valid whole number of days. The shipping date was set to the delivery date {deliveryDate:yyyy-MM-dd}."
            });

            _logger.LogWarning(
                "ORDER SHIPPING DATE Invalid lead days. CardCode={CardCode} Field={Field} RawValue={RawValue}",
                businessPartner.CardCode,
                _options.ShippingLeadDaysField,
                raw);

            leadDays = 0;
        }

        var timeZone = ResolveTimeZone(_options.BusinessTimeZone);

        var orderDate = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(orderDateTime, timeZone).DateTime);

        var shippingDate = CalculateShippingDate(
            deliveryDate,
            leadDays,
            orderDate);

        if (shippingDate != deliveryDate.AddDays(-leadDays))
        {
            warnings.Add(new SubmitOrderMessage
            {
                Type = "WARN",
                Code = "SHIPPING_DATE_ADJUSTED",
                Text =
                    $"Delivery date {deliveryDate:yyyy-MM-dd} minus {leadDays} shipping lead day(s) falls before the order date. The shipping date was set to the order date {shippingDate:yyyy-MM-dd}."
            });
        }

        // Always report the result, so the caller can see the calculation.
        warnings.Add(new SubmitOrderMessage
        {
            Type = "INFO",
            Code = "SHIPPING_DATE_CALCULATED",
            Text =
                $"Shipping date {shippingDate:yyyy-MM-dd} = delivery date {deliveryDate:yyyy-MM-dd} minus {leadDays} shipping lead day(s) (Business Partner field {_options.ShippingLeadDaysField} = '{raw}')."
        });

        _logger.LogInformation(
            "ORDER SHIPPING DATE CardCode={CardCode} DeliveryDate={DeliveryDate} LeadDays={LeadDays} OrderDate={OrderDate} ShippingDate={ShippingDate}",
            businessPartner.CardCode,
            deliveryDate,
            leadDays,
            orderDate,
            shippingDate);

        return shippingDate;
    }

    // Blank = 0 days. SAP may return "10" or "10.0". Negative values are invalid.
    internal static bool TryParseShippingLeadDays(string? raw, out int leadDays)
    {
        leadDays = 0;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (!decimal.TryParse(
                raw.Trim(),
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var value) ||
            value < 0 ||
            value != decimal.Truncate(value) ||
            value > 3650)
        {
            return false;
        }

        leadDays = (int)value;
        return true;
    }

    // Never earlier than the order date (DocDate): SAP does not accept a
    // document due date before the posting date.
    internal static DateOnly CalculateShippingDate(
        DateOnly deliveryDate,
        int leadDays,
        DateOnly orderDate)
    {
        var shippingDate = deliveryDate.AddDays(-leadDays);

        return shippingDate < orderDate
            ? orderDate
            : shippingDate;
    }

    private JsonObject BuildSapPayload(
        SubmitOrderRequest originalRequest,
        ParsedSubmitOrderRequest parsed,
        DateOnly deliveryDate,
        DateOnly? shippingDate,
        List<ProcessedOrderLine> processedLines,
        string billToParty,
        string? shipToCode,
        string username,
        string integrationId,
        string orderStatus,
        bool isCreateDraft)
    {
        var businessTimeZone = ResolveTimeZone(
            _options.BusinessTimeZone);

        var localOrderDateTime =
            TimeZoneInfo.ConvertTime(
                parsed.OrderDateTime,
                businessTimeZone);

        var docDate =
            DateOnly.FromDateTime(
                localOrderDateTime.DateTime);

        var payload = new JsonObject
        {
            ["CardCode"] = billToParty,

            ["DocDate"] =
                docDate.ToString(
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture),

            // Shown as "Delivery Date" on the client's customised form (ORDR.TaxDate).
            ["TaxDate"] =
                deliveryDate.ToString(
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture),

            ["NumAtCard"] =
                parsed.CustomerOrderNumber,

            ["Comments"] =
                originalRequest.Remarks ?? "",
            // New draft: Confirmed ticked.
            // Any update (draft or order): Confirmed unticked, so the order
            // has to be confirmed again after the change.
            ["Confirmed"] = isCreateDraft ? "tYES" : "tNO",

            [_options.CustomerOrderNumberField] =
                parsed.CustomerOrderNumber,

            [_options.OrderStatusField] =
                orderStatus,

            [_options.IntegrationIdField] =
                integrationId,

            [_options.ApiUserField] =
                username,

            [_options.ProcessingStatusField] =
                _options.AcceptedProcessingStatus
        };

        if (!string.IsNullOrWhiteSpace(shipToCode))
        {
            payload["ShipToCode"] = shipToCode;
        }

        // DocDueDate is shown as "Shipping Date" on the client's form and SAP
        // requires it when the document is created. Sent only on create
        // (delivery date minus the BP's U_ShipLeadDays); updates leave it alone.
        if (shippingDate.HasValue)
        {
            payload["DocDueDate"] =
                shippingDate.Value.ToString(
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture);
        }

        if (isCreateDraft)
        {
            payload["DocObjectCode"] = "oOrders";
        }

        var documentLines = new JsonArray();

        foreach (var line in processedLines
                     .OrderBy(x => x.LineNumber))
        {
            var lineObject = new JsonObject
            {
                ["ItemCode"] = line.ItemCode,
                ["Quantity"] =
                    JsonValue.Create(line.Quantity),

                ["ShipDate"] =
                    line.ShipDate.ToString(
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture)
            };

            if (!string.IsNullOrWhiteSpace(
                    line.ItemDescription))
            {
                lineObject["ItemDescription"] =
                    line.ItemDescription;
            }

            // Item cost centre from OITM (U_BIO_CostCenter) -> the line fields in
            // OrderPosting:LineCostCenterFields (currently COGSCostingCode2 = CogsOcrCo2).
            // Sent on both create and update so newly added lines also get it.
            if (!string.IsNullOrWhiteSpace(line.CostCenter))
            {
                foreach (var field in _options.LineCostCenterFields
                             .Where(x => !string.IsNullOrWhiteSpace(x))
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    lineObject[field.Trim()] = line.CostCenter;
                }
            }

            documentLines.Add(lineObject);
        }

        payload["DocumentLines"] = documentLines;

        return payload;
    }

    private SubmitOrderResponse BuildResponse(
        string integrationId,
        string operationType,
        string customerOrderNumber,
        string sapObject,
        int sapDocEntry,
        string orderStatus,
        DateTimeOffset receivedAt,
        List<SubmitOrderMessage> warnings)
    {
        var messages = BuildOperationMessages(
            operationType,
            orderStatus);

        messages.AddRange(warnings);

        return new SubmitOrderResponse
        {
            IntegrationId = integrationId,
            OperationType = operationType,
            CustomerOrderNumber =
                customerOrderNumber,

            ProcessingStatus =
                _options.AcceptedProcessingStatus,

            SapObject = sapObject,
            SapDocEntry = sapDocEntry,
            OrderStatus = orderStatus,
            ReceivedAt = receivedAt,
            Messages = messages
        };
    }

    private static List<SubmitOrderMessage>
        BuildOperationMessages(
            string operationType,
            string orderStatus)
    {
        if (operationType == "CREATE")
        {
            return
            [
                new SubmitOrderMessage
                {
                    Type =
                        orderStatus == "OOR"
                            ? "WARN"
                            : "INFO",

                    Code =
                        orderStatus == "OOR"
                            ? "ORDER_ACCEPTED_OUT_OF_RANGE"
                            : "ORDER_ACCEPTED",

                    Text =
                        orderStatus == "OOR"
                            ? "Sales order draft created and marked Out of Range for review."
                            : "Sales order draft created successfully."
                }
            ];
        }

        if (operationType == "NO_CHANGE")
        {
            return
            [
                new SubmitOrderMessage
                {
                    Type = "INFO",
                    Code = "ORDER_NOT_CHANGED",
                    Text =
                        "The submitted order is identical to the existing SAP document. No update was made."
                }
            ];
        }

        if (operationType == "UPDATE_ORDER")
        {
            return
            [
                new SubmitOrderMessage
                {
                    Type =
                        orderStatus == "OOR"
                            ? "WARN"
                            : "INFO",

                    Code =
                        orderStatus == "OOR"
                            ? "ORDER_UPDATED_OUT_OF_RANGE"
                            : "ORDER_UPDATED",

                    Text =
                        orderStatus == "OOR"
                            ? "Live order updated and marked Out of Range for review."
                            : "Live sales order updated successfully."
                }
            ];
        }

        return
        [
            new SubmitOrderMessage
            {
                Type =
                    orderStatus == "OOR"
                        ? "WARN"
                        : "INFO",

                Code =
                    orderStatus == "OOR"
                        ? "DRAFT_UPDATED_OUT_OF_RANGE"
                        : "DRAFT_UPDATED",

                Text =
                    orderStatus == "OOR"
                        ? "Sales order draft updated and marked Out of Range for review."
                        : "Sales order draft updated successfully."
            }
        ];
    }

    private static bool IsShipToAddressType(
        string? addressType)
    {
        if (string.IsNullOrWhiteSpace(addressType))
        {
            return false;
        }

        return addressType.Equals(
                   "bo_ShipTo",
                   StringComparison.OrdinalIgnoreCase) ||
               addressType.Equals(
                   "S",
                   StringComparison.OrdinalIgnoreCase) ||
               addressType.Contains(
                   "ShipTo",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsYes(string? value)
    {
        return value is not null &&
               (value.Equals(
                    "tYES",
                    StringComparison.OrdinalIgnoreCase) ||
                value.Equals(
                    "Y",
                    StringComparison.OrdinalIgnoreCase) ||
                value.Equals(
                    "true",
                    StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildCardCodeFilter(
        List<string> allowedCardCodes)
    {
        var cardCodes = allowedCardCodes
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (cardCodes.Count == 0)
        {
            throw new UnauthorizedAccessException(
                "No CardCodes are configured for this API user.");
        }

        if (cardCodes.Count == 1)
        {
            return
                $"CardCode eq '{EscapeODataString(cardCodes[0])}'";
        }

        return "(" +
               string.Join(
                   " or ",
                   cardCodes.Select(x =>
                       $"CardCode eq '{EscapeODataString(x)}'")) +
               ")";
    }

    private static bool TryParseSapDate(
        string value,
        out DateOnly date)
    {
        var datePart =
            value.Length >= 10
                ? value[..10]
                : value;

        return DateOnly.TryParseExact(
            datePart,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);
    }

    private static bool TryParseLimitTime(
        string value,
        out TimeOnly time)
    {
        string[] formats =
        [
            "HH:mm:ss",
            "HH:mm",
            "H:mm:ss",
            "H:mm"
        ];

        return TimeOnly.TryParseExact(
            value.Trim(),
            formats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out time);
    }

    private static TimeZoneInfo ResolveTimeZone(
        string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(
                timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            if (timeZoneId.Equals(
                    "Asia/Dubai",
                    StringComparison.OrdinalIgnoreCase))
            {
                return TimeZoneInfo.FindSystemTimeZoneById(
                    "Arabian Standard Time");
            }

            if (timeZoneId.Equals(
                    "Asia/Kolkata",
                    StringComparison.OrdinalIgnoreCase))
            {
                return TimeZoneInfo.FindSystemTimeZoneById(
                    "India Standard Time");
            }

            throw;
        }
    }

    private static JsonObject ParseObject(
        string body)
    {
        return JsonNode.Parse(body)?.AsObject()
               ?? throw new InvalidOperationException(
                   "SAP Service Layer returned an invalid JSON response.");
    }

    private static string GetString(
        JsonObject? obj,
        string propertyName)
    {
        if (obj is null ||
            !obj.TryGetPropertyValue(
                propertyName,
                out var node) ||
            node is null)
        {
            return "";
        }

        try
        {
            return node.GetValue<string>() ?? "";
        }
        catch
        {
            return node.ToString();
        }
    }

    private static int GetInt(
        JsonObject? obj,
        string propertyName)
    {
        if (obj is null ||
            !obj.TryGetPropertyValue(
                propertyName,
                out var node) ||
            node is null)
        {
            return 0;
        }

        try
        {
            return node.GetValue<int>();
        }
        catch
        {
            return int.TryParse(
                node.ToString(),
                out var result)
                ? result
                : 0;
        }
    }

    private static string GenerateIntegrationId(
        DateTimeOffset receivedAt)
    {
        var suffix =
            Math.Abs(
                DateTimeOffset.UtcNow.Ticks %
                1_000_000);

        return
            $"INT-{receivedAt:yyyyMMdd}-{suffix:D6}";
    }

    private static string EscapeODataString(
        string value)
    {
        return value.Replace("'", "''");
    }

    private static string EscapeODataKey(
        string value)
    {
        return value.Replace("'", "''");
    }

    private sealed class ParsedSubmitOrderRequest
    {
        public string CustomerOrderNumber { get; set; } = "";

        public DateTimeOffset OrderDateTime { get; set; }

        public string BillToParty { get; set; } = "";
    }

    private sealed class ProcessedOrderLine
    {
        public int LineNumber { get; set; }

        public string ItemCode { get; set; } = "";

        public string? ItemDescription { get; set; }

        public decimal Quantity { get; set; }

        public DateOnly ShipDate { get; set; }

        public string CostCenter { get; set; } = "";
    }

    private sealed class ExistingSapDocument
    {
        public string SapObject { get; set; } = "";

        public int DocEntry { get; set; }

        public string CardCode { get; set; } = "";
    }

    private sealed class BusinessPartnerContext
    {
        public string CardCode { get; set; } = "";

        //public string LimitDateRaw { get; set; } = "";

        public string LimitTimeRaw { get; set; } = "";

        // OCRD.U_ShipLeadDays: days to ship before the delivery date (blank = 0).
        public string ShippingLeadDaysRaw { get; set; } = "";

        public bool ShippingLeadDaysFieldFound { get; set; }

        public List<string> CandidateShippingFields { get; set; } = new();

        public List<BusinessPartnerAddress> Addresses { get; set; } = new();
    }

    private sealed class BusinessPartnerAddress
    {
        public string AddressName { get; set; } = "";

        public string AddressType { get; set; } = "";

        public string GlobalLocationNumber { get; set; } = "";
    }

    private sealed class ItemValidationResult
    {
        public bool Exists { get; set; }

        public bool IsActiveSalesItem { get; set; }

        public string CostCenter { get; set; } = "";

        public static ItemValidationResult NotFound()
        {
            return new ItemValidationResult
            {
                Exists = false,
                IsActiveSalesItem = false
            };
        }
    }
}