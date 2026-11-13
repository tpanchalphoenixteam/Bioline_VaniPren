namespace PH.SapB1.Vaniperen.Auth;

public sealed class BasicAuthOptions
{
    public List<BasicAuthUser> Users { get; set; } = new();
}

public sealed class BasicAuthUser
{
    public string Username { get; set; } = "";

    public string Password { get; set; } = "";

    public string NormalCardCode { get; set; } = "";

    public string SpecialCardCode { get; set; } = "";

    public string PlaceholderItemCode { get; set; } = "";

    public bool Active { get; set; } = true;
}