namespace PH.SapB1.Vaniperen.Setup;

public sealed class SetupInstallResult
{
    public bool Success => Actions.All(x => x.Success);

    public List<SetupActionResult> Actions { get; set; } = new();
}

public sealed class SetupActionResult
{
    public string ObjectType { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public bool Success { get; set; }
    public string Message { get; set; } = "";

    public static SetupActionResult Exists(string objectType, string name)
    {
        return new SetupActionResult
        {
            ObjectType = objectType,
            Name = name,
            Status = "Exists",
            Success = true,
            Message = "Already exists."
        };
    }

    public static SetupActionResult Created(string objectType, string name)
    {
        return new SetupActionResult
        {
            ObjectType = objectType,
            Name = name,
            Status = "Created",
            Success = true,
            Message = "Created successfully."
        };
    }

    public static SetupActionResult Failed(string objectType, string name, string message)
    {
        return new SetupActionResult
        {
            ObjectType = objectType,
            Name = name,
            Status = "Failed",
            Success = false,
            Message = message
        };
    }
}