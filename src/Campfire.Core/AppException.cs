namespace Campfire.Core;

public sealed class AppException : Exception
{
    public AppException(int status, string message) : base(message) => Status = status;

    public int Status { get; }
}

public sealed class ViolationException : Exception
{
    public ViolationException() : base("host resolves to a private network")
    {
    }
}

public sealed class UnresolvableException : Exception
{
    public UnresolvableException() : base("host did not resolve")
    {
    }
}
