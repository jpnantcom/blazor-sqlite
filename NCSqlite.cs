using Microsoft.JSInterop;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace NC.BlazorSQLite;

public class NCSqlite : IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private IJSObjectReference _jsModule;
    private IJSObjectReference _ncsqliteJs;
    private readonly DotNetObjectReference<NCSqlite> _ref;

    private Action<JObject>? _currentRowHandler;
    private NcSqliteErrorDetail? _errorDetail;
    private string _latestSql = string.Empty;   

	private HashSet<string> _createdTables = new();

    public string DbFileName { get; private set; }

    public NCSqlite(IJSRuntime jsRuntime, string dbFileName)
    {
        _js = jsRuntime;
        _ref = DotNetObjectReference.Create(this);

        this.DbFileName = dbFileName;
    }

    [JSInvokable]
    public async Task OnError(string message, JsonElement data)
    {
        _errorDetail = new NcSqliteErrorDetail($"Latest Query was: {_latestSql}\r\n\r\n{message}", data);
    }

    [JSInvokable]
    public async void OnRow(JsonElement row)
    {
        JsonElement rowNumber;
        if (row.TryGetProperty("rowNumber", out rowNumber) == false ||
            rowNumber.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (_currentRowHandler == null)
        {
            throw new NCSqliteException("No Read Operation Pending");
        }

        JsonElement rowValue;
        if (row.TryGetProperty("row", out rowValue) == false)
        {
            return;
        }

        var jObject = JObject.Parse(rowValue.GetRawText());
        foreach (var jp in jObject.Properties().ToList())
        {
            if (jp.Name.EndsWith("_json"))
            {
                jObject[jp.Name.Replace("_json", "")] = JObject.Parse(jp.Value.ToString());
                jp.Remove();
            }
        }

        _currentRowHandler?.Invoke(jObject);
    }

    /// <summary>
    /// Executes SQL Statement
    /// </summary>
    /// <param name="sql"></param>
    /// <param name="reader"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public async Task Execute(string sql, Action<JObject>? reader = null, object bind = null)
    {
        _errorDetail = null;
        _latestSql = sql;

        if (_currentRowHandler != null)
        {
            throw new InvalidOperationException("Other pending read operation");
        }

        _currentRowHandler = reader;

        var ncSqlite = await GetNcSqliteInstance();

        await ncSqlite.InvokeVoidAsync("exec", sql, bind);

        _currentRowHandler = null;

        if (_errorDetail != null)
        {
            throw new NCSqliteException(_errorDetail);
        }
    }

    /// <summary>
    /// Create table for a given JSON object
    /// </summary>
    /// <param name="tableName"></param>
    /// <param name="keyPropertyName"></param>
    /// <param name="input"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    public async Task CreateTable(string tableName, JObject input)
    {
        if (string.IsNullOrEmpty(tableName))
        {
            throw new ArgumentException("Table name cannot be null or empty.");
        }

        StringBuilder sb = new StringBuilder();
        sb.Append($"CREATE TABLE IF NOT EXISTS {tableName} (");

        var properties = input.Properties();
        foreach (var property in properties)
        {
            var (columnName, sqlLiteType) = GetColumnNameAndType(property);
            sb.Append($"[{columnName}] {sqlLiteType}, ");
        }
        sb.Remove(sb.Length - 2, 2); // Remove the last comma and space
        sb.Append(")");

        string createTableStatement = sb.ToString();

        await Execute(createTableStatement, null);
    }

    /// <summary>
    /// Upsert provided object
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="data"></param>
    /// <returns></returns>
    public Task Upsert<T>(T data)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        return Upsert(data.GetType().Name, JObject.FromObject(data));
    }

    /// <summary>
    /// Upsert the given json object
    /// </summary>
    /// <param name="tableName"></param>
    /// <param name="data"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    /// <exception cref="ArgumentNullException"></exception>
    public async Task Upsert(string tableName, JObject data)
    {
        if (string.IsNullOrEmpty(tableName))
        {
            throw new ArgumentException("Table name cannot be null or empty.");
        }

        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        tableName = tableName.ToLower();

        if (_createdTables.Contains(tableName) == false)
        {
            await CreateTable(tableName, data);
            _createdTables.Add(tableName);
        }

        StringBuilder sb = new StringBuilder();
        sb.Append($"INSERT OR REPLACE INTO {tableName} (");

        var properties = data.Properties();
        foreach (var property in properties)
        {
            var (columnName, _) = GetColumnNameAndType(property, skipNull: true);

            if (string.IsNullOrEmpty(columnName))
            {
                continue;
            }

            sb.Append($"[{columnName}], ");
        }
        sb.Remove(sb.Length - 2, 2); // Remove the last comma and space
        sb.Append(") VALUES (");

        foreach (var property in properties)
        {
            JToken columnValue = property.Value;

            if (columnValue.Type == JTokenType.Null)
            {
                continue;
            }

            sb.Append($"{GetSqlValue(columnValue)}, ");
        }
        sb.Remove(sb.Length - 2, 2); // Remove the last comma and space
        sb.Append(")");

        string upsertStatement = sb.ToString();

        await Execute(upsertStatement, null);
    }

    /// <summary>
    /// Deletes a row from the table
    /// </summary>
    /// <param name="tableName"></param>
    /// <param name="keyPropertyName"></param>
    /// <param name="keyValue"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    /// <exception cref="ArgumentNullException"></exception>
    public async Task Delete(string tableName, string keyPropertyName, JToken keyValue)
    {
        if (string.IsNullOrEmpty(tableName))
        {
            throw new ArgumentException("Table name cannot be null or empty.");
        }

        if (string.IsNullOrEmpty(keyPropertyName))
        {
            throw new ArgumentException("Key property name cannot be null or empty.");
        }

        if (keyValue == null)
        {
            throw new ArgumentNullException(nameof(keyValue));
        }

        string deleteStatement = $"DELETE FROM {tableName} WHERE {keyPropertyName} = {GetSqlValue(keyValue)}";

        await Execute(deleteStatement, null);
    }

    private async Task<IJSObjectReference> GetNcSqliteInstance()
    {
        if (_ncsqliteJs == null)
        {
            _errorDetail = null;

            _jsModule = await _js.InvokeAsync<IJSObjectReference>(
                        "import", "/_content/NC.BlazorSQLite/ncsqlite.js");
            _ncsqliteJs = await _jsModule.InvokeAsync<IJSObjectReference>("getInstance", _ref, this.DbFileName);

            if (_errorDetail != null)
            {
                throw new NCSqliteException(_errorDetail);
            }
        }

        return _ncsqliteJs;
    }

    public async ValueTask DisposeAsync()
    {
        if (_jsModule != null)
        {
            await _jsModule.DisposeAsync();
        }

        if (_ncsqliteJs != null)
        {
            await _ncsqliteJs.DisposeAsync();
        }

        _ref?.Dispose();
    }

    public static string EscapeSqlString(string value)
    {
	    if (string.IsNullOrEmpty(value))
		    return value;

	    // Escape single quotes by doubling them
	    value = value.Replace("'", "''");

	    // Escape backslashes (important for some databases)
	    value = value.Replace("\\", "\\\\");

	    // Escape newlines
	    value = value.Replace("\n", "\\n").Replace("\r", "\\r");

	    return value;
    }

    private string GetSqlValue(JToken value)
    {
        switch (value.Type)
        {
            case JTokenType.Integer:
            case JTokenType.Float:
            case JTokenType.Boolean:
                return value.ToString();
			case JTokenType.String:
			case JTokenType.Date:
			default:
				return $"'{EscapeSqlString(value.ToString())}'";
		}
    }


    private (string columnName, string sqlLiteType) GetColumnNameAndType(JProperty property, bool skipNull = false)
    {
        if (property.Value.Type == JTokenType.Null)
        {
            if (skipNull)
            {
                return (string.Empty, string.Empty);
            }

            throw new InvalidOperationException("Column value cannot be null");
        }

        string columnName = property.Name;
        string sqlLiteType = "TEXT";

        switch (property.Value.Type)
        {
            case JTokenType.String:
                sqlLiteType = "TEXT";
                break;
            case JTokenType.Integer:
                sqlLiteType = "INTEGER";
                break;
            case JTokenType.Float:
                sqlLiteType = "REAL";
                break;
            case JTokenType.Boolean:
                sqlLiteType = "INTEGER";
                break;
            case JTokenType.Date:
                sqlLiteType = "TEXT";
                break;
            default:
                columnName = $"{columnName}_json";
                break;
        }

        return (columnName, sqlLiteType);
    }

    /// <summary>
    /// Runs the given action in a transaction
    /// </summary>
    /// <param name="toRun"></param>
    /// <returns></returns>
    public async Task Transaction(Func<Task> toRun)
    {
        await Execute("BEGIN TRANSACTION;", null);

        try
        {
            await toRun();
        }
        catch (Exception)
        {
            await Execute("ROLLBACK;", null);

            throw;
        }

        await Execute("END TRANSACTION;", null);
    }
}
