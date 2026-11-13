namespace PH.SapB1.Vaniperen.Setup;

public sealed class SapMetadataOptions
{
    public List<SapUserTableDefinition> UserTables { get; set; } = new();

    public List<SapUserFieldDefinition> UserFields { get; set; } = new();
}

public sealed class SapUserTableDefinition
{
    // Without @
    // Example: PH_API_USER
    public string TableName { get; set; } = "";

    public string TableDescription { get; set; } = "";

    // Example: bott_NoObject
    public string TableType { get; set; } = "bott_NoObject";
}

public sealed class SapUserFieldDefinition
{
    // For UDT: @PH_API_USER
    // For system table: ORDR, OCRD, OITM
    public string TableName { get; set; } = "";

    // Without U_
    // Example: APIUser => SAP creates U_APIUser
    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    // Example: db_Alpha, db_Numeric, db_Date, db_Float, db_Memo
    public string Type { get; set; } = "db_Alpha";

    // Example: st_None, st_Price, st_Quantity, st_Percentage, st_Sum
    public string SubType { get; set; } = "st_None";

    public int? EditSize { get; set; }

    public string FullFieldName => $"U_{Name}";
    public string Mandatory { get; set; } = "tNO";

    public string? DefaultValue { get; set; }

    public List<SapValidValueDefinition> ValidValues { get; set; } = new();
}
public sealed class SapValidValueDefinition
{
    public string Value { get; set; } = "";

    public string Description { get; set; } = "";
}