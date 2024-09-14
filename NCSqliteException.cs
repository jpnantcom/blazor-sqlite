using System.Text.Json;

namespace NC.BlazorSQLite;

public record NcSqliteErrorDetail(string Message, JsonElement Data = default);

public class NCSqliteException : Exception
{
    public NcSqliteErrorDetail ErrorDetail { get; init; }

    public NCSqliteException(NcSqliteErrorDetail detail) : base(detail.Message)
    {
        this.ErrorDetail = detail;
    }

    public NCSqliteException(string message) : base(message)
    {
        this.ErrorDetail = new NcSqliteErrorDetail(message);
    }
}
