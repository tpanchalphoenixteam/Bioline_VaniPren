using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using PH.SapB1.Vaniperen.Sap;

namespace PH.SapB1.Vaniperen.Setup;

public sealed class SapMetadataInstaller
{
    private readonly SapServiceLayerRawClient _client;
    private readonly IOptionsMonitor<SapMetadataOptions> _metadataOptions;
    private readonly ILogger<SapMetadataInstaller> _logger;

    public SapMetadataInstaller(
        SapServiceLayerRawClient client,
        IOptionsMonitor<SapMetadataOptions> metadataOptions,
        ILogger<SapMetadataInstaller> logger)
    {
        _client = client;
        _metadataOptions = metadataOptions;
        _logger = logger;
    }

    public async Task<SetupInstallResult> InstallAsync(CancellationToken cancellationToken)
    {
        var result = new SetupInstallResult();

        var metadata = _metadataOptions.CurrentValue;

        if (metadata.UserTables.Count == 0 && metadata.UserFields.Count == 0)
        {
            result.Actions.Add(
                SetupActionResult.Failed(
                    "Config",
                    "sap-metadata.json",
                    "No UserTables or UserFields found in sap-metadata.json."));

            return result;
        }

        foreach (var table in metadata.UserTables)
        {
            result.Actions.Add(await EnsureUserTableAsync(table, cancellationToken));
        }

        foreach (var field in metadata.UserFields)
        {
            result.Actions.Add(await EnsureUserFieldAsync(field, cancellationToken));
        }

        return result;
    }

    private async Task<SetupActionResult> EnsureUserTableAsync(
        SapUserTableDefinition table,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(table.TableName))
        {
            return SetupActionResult.Failed(
                "UDT",
                "Unknown",
                "TableName is required.");
        }

        var cleanTableName = table.TableName.Trim().TrimStart('@');

        var getUrl = $"UserTablesMD('{EscapeODataKey(cleanTableName)}')";

        var existing = await _client.SendAsync(
            HttpMethod.Get,
            getUrl,
            null,
            cancellationToken);

        if (existing.IsSuccessStatusCode)
        {
            return SetupActionResult.Exists("UDT", "@" + cleanTableName);
        }

        if (!IsNotFound(existing))
        {
            return SetupActionResult.Failed(
                "UDT",
                "@" + cleanTableName,
                existing.Body);
        }

        var payload = new JsonObject
        {
            ["TableName"] = cleanTableName,
            ["TableDescription"] = string.IsNullOrWhiteSpace(table.TableDescription)
                ? cleanTableName
                : table.TableDescription,
            ["TableType"] = string.IsNullOrWhiteSpace(table.TableType)
                ? "bott_NoObject"
                : table.TableType
        };

        var created = await _client.SendAsync(
            HttpMethod.Post,
            "UserTablesMD",
            payload,
            cancellationToken);

        if (created.IsSuccessStatusCode)
        {
            return SetupActionResult.Created("UDT", "@" + cleanTableName);
        }

        if (LooksLikeAlreadyExists(created.Body))
        {
            return SetupActionResult.Exists("UDT", "@" + cleanTableName);
        }

        return SetupActionResult.Failed(
            "UDT",
            "@" + cleanTableName,
            created.Body);
    }

    private async Task<SetupActionResult> EnsureUserFieldAsync(
        SapUserFieldDefinition field,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(field.TableName))
        {
            return SetupActionResult.Failed(
                "UDF",
                field.Name,
                "TableName is required.");
        }

        if (string.IsNullOrWhiteSpace(field.Name))
        {
            return SetupActionResult.Failed(
                "UDF",
                field.TableName,
                "Name is required.");
        }

        var cleanName = field.Name.Trim();

        if (cleanName.StartsWith("U_", StringComparison.OrdinalIgnoreCase))
        {
            return SetupActionResult.Failed(
                "UDF",
                $"{field.TableName}.{cleanName}",
                "Field Name should be without U_. Example: APIUser, not U_APIUser.");
        }

        var cleanTableName = field.TableName.Trim();

        var filter =
            $"TableName eq '{EscapeODataString(cleanTableName)}' and Name eq '{EscapeODataString(cleanName)}'";

        var getUrl =
            "UserFieldsMD" +
            "?$select=TableName,FieldID,Name,Description,Type" +
            "&$filter=" + Uri.EscapeDataString(filter);

        var existing = await _client.SendAsync(
            HttpMethod.Get,
            getUrl,
            null,
            cancellationToken);

        if (!existing.IsSuccessStatusCode)
        {
            return SetupActionResult.Failed(
                "UDF",
                $"{cleanTableName}.U_{cleanName}",
                existing.Body);
        }

        var root = JsonNode.Parse(existing.Body);
        var values = root?["value"]?.AsArray();

        if (values is not null && values.Count > 0)
        {
            return SetupActionResult.Exists(
                "UDF",
                $"{cleanTableName}.U_{cleanName}");
        }

        var payload = new JsonObject
        {
            ["TableName"] = cleanTableName,
            ["Name"] = cleanName,
            ["Description"] = string.IsNullOrWhiteSpace(field.Description)
                ? cleanName
                : field.Description,
            ["Type"] = string.IsNullOrWhiteSpace(field.Type)
                ? "db_Alpha"
                : field.Type,
            ["SubType"] = string.IsNullOrWhiteSpace(field.SubType)
                ? "st_None"
                : field.SubType
        };
        payload["Mandatory"] = string.IsNullOrWhiteSpace(field.Mandatory)
    ? "tNO"
    : field.Mandatory;

        if (field.DefaultValue is not null)
        {
            payload["DefaultValue"] = field.DefaultValue;
        }

        if (field.ValidValues.Count > 0)
        {
            var validValues = new JsonArray();

            foreach (var validValue in field.ValidValues)
            {
                validValues.Add(new JsonObject
                {
                    ["Value"] = validValue.Value,
                    ["Description"] = string.IsNullOrWhiteSpace(
                        validValue.Description)
                        ? validValue.Value
                        : validValue.Description
                });
            }

            payload["ValidValuesMD"] = validValues;
        }

        if (field.EditSize.HasValue)
        {
            payload["EditSize"] = field.EditSize.Value;
        }

        var created = await _client.SendAsync(
            HttpMethod.Post,
            "UserFieldsMD",
            payload,
            cancellationToken);

        if (created.IsSuccessStatusCode)
        {
            return SetupActionResult.Created(
                "UDF",
                $"{cleanTableName}.U_{cleanName}");
        }

        if (LooksLikeAlreadyExists(created.Body))
        {
            return SetupActionResult.Exists(
                "UDF",
                $"{cleanTableName}.U_{cleanName}");
        }

        return SetupActionResult.Failed(
            "UDF",
            $"{cleanTableName}.U_{cleanName}",
            created.Body);
    }

    private static bool IsNotFound(ServiceLayerResult result)
    {
        if (result.StatusCode == HttpStatusCode.NotFound)
        {
            return true;
        }

        return result.Body.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
               result.Body.Contains("No matching records", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeAlreadyExists(string body)
    {
        return body.Contains("already exists", StringComparison.OrdinalIgnoreCase) ||
               body.Contains("already defined", StringComparison.OrdinalIgnoreCase) ||
               body.Contains("duplicate", StringComparison.OrdinalIgnoreCase);
    }

    private static string EscapeODataString(string value)
    {
        return value.Replace("'", "''");
    }

    private static string EscapeODataKey(string value)
    {
        return value.Replace("'", "''");
    }
}