namespace ErpSync.Domain.Orders;

public enum SalesOrderStatus
{
    /// <summary>Accepted by the API and waiting to be sent to the ERP.</summary>
    Pending = 0,

    /// <summary>Created in the ERP; <see cref="SalesOrder.ErpOrderNumber"/> is set.</summary>
    Submitted = 1,

    /// <summary>Rejected by the ERP or out of retry attempts. Needs manual attention.</summary>
    Failed = 2,
}
