namespace PH.SapB1.Vaniperen.Sap;

public sealed class SapServiceLayerOptions
{
    public string BaseUrl { get; set; } = "";

    public string CompanyDB { get; set; } = "";

    public string UserName { get; set; } = "";

    public string Password { get; set; } = "";

    public int SessionMinutes { get; set; } = 20;

    public bool IgnoreSslCertificateErrors { get; set; }

    public int LoginRetryCount { get; set; } = 3;

    public int LoginRetryDelaySeconds { get; set; } = 2;

    /// <summary>
    /// Diagnostic option only. Example: .node6
    /// Leave blank for normal load-balanced routing.
    /// </summary>
    public string PreferredRouteId { get; set; } = "";
}