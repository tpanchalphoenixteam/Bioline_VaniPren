using System.Net;

namespace PH.SapB1.Vaniperen.Sap;

public sealed class ServiceLayerResult
{
    public HttpStatusCode StatusCode { get; set; }

    public string Body { get; set; } = "";

    public bool IsSuccessStatusCode =>
        (int)StatusCode >= 200 && (int)StatusCode <= 299;
}