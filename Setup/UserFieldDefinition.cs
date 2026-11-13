namespace PH.SapB1.Vaniperen.Setup;

public sealed class UserFieldDefinition
{
    public string TableName { get; set; } = "";

    // Name should be without U_
    // Example: APIUser => SAP creates U_APIUser
    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    public string Type { get; set; } = "db_Alpha";

    public string SubType { get; set; } = "st_None";

    public int? EditSize { get; set; }

    public string FullFieldName => $"U_{Name}";
}