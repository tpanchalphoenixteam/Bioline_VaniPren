namespace PH.SapB1.Vaniperen.Auth;

public sealed class ApiUserValidationResult
{
    public bool Succeeded { get; private set; }

    public string Username { get; private set; } = "";

    public string NormalCardCode { get; private set; } = "";

    public string SpecialCardCode { get; private set; } = "";

    public string PlaceholderItemCode { get; private set; } = "";

    public List<string> AllowedCardCodes { get; private set; } = new();

    public string ErrorMessage { get; private set; } = "";

    public static ApiUserValidationResult Success(
        string username,
        string normalCardCode,
        string specialCardCode,
        string placeholderItemCode)
    {
        var allowedCardCodes = new List<string>();

        if (!string.IsNullOrWhiteSpace(normalCardCode))
        {
            allowedCardCodes.Add(normalCardCode.Trim());
        }

        if (!string.IsNullOrWhiteSpace(specialCardCode))
        {
            allowedCardCodes.Add(specialCardCode.Trim());
        }

        allowedCardCodes = allowedCardCodes
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ApiUserValidationResult
        {
            Succeeded = true,
            Username = username.Trim(),
            NormalCardCode = normalCardCode.Trim(),
            SpecialCardCode = specialCardCode.Trim(),
            PlaceholderItemCode = placeholderItemCode.Trim(),
            AllowedCardCodes = allowedCardCodes
        };
    }

    public static ApiUserValidationResult Failed(string errorMessage)
    {
        return new ApiUserValidationResult
        {
            Succeeded = false,
            ErrorMessage = errorMessage
        };
    }
}