namespace ErpSync.Application.Abstractions.Erp;

/// <summary>The ERP understood the request and refused it. Sending it again will fail the same way.</summary>
public sealed class ErpRejectedException : Exception
{
    public ErpRejectedException()
    {
    }

    public ErpRejectedException(string message)
        : base(message)
    {
    }

    public ErpRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The ERP was unreachable, slow or failing. The operation may succeed later.</summary>
public sealed class ErpUnavailableException : Exception
{
    public ErpUnavailableException()
    {
    }

    public ErpUnavailableException(string message)
        : base(message)
    {
    }

    public ErpUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ErpUnavailableException(string message, bool outcomeUnknown, Exception? innerException = null)
        : base(message, innerException)
    {
        OutcomeUnknown = outcomeUnknown;
    }

    /// <summary>
    /// <c>true</c> when the request may have been processed by the ERP even though no success
    /// response arrived (for example a timeout after the request was sent).
    /// </summary>
    public bool OutcomeUnknown { get; }
}
