namespace PH.SapB1.Vaniperen.Auth
{
    public interface IApiUserValidator
    {
        Task<ApiUserValidationResult> ValidateAsync(
            string username,
            string password,
            CancellationToken cancellationToken);
    }
}
