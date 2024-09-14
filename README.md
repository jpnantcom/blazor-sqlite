# NC-BlazorSQLite
Uses SQLite Database which runs entirely on Client Browser via SQLite wasm. Simple CRUD ORM which uses NewtonSoft JSON is provided for extra convinience.

## Why you need this?
Nowadays, Browsers are more powerful than ever and you will find that most of the logic can be done on the browser itself.

What if you can also store some data to be used with that logic as well?

**Usecases:**
- Local Cache with support for SQL queries.
- Offload Complex Report Generation to Client Side.
- Staging area for the data before sending it to the server.

## Where the data is stored?
By default, the database is stored in memory and will be lost once the tab is closed.

Note that this library was never intended to be used to store data permanently on the client browser and the SQLite WASM is loaded inside the worker thread which means `:localStorage:` and `:sessionStorage:` is not available and will fail.

SQLite WASM provides options to persist the data, see: [Persistent storage options](https://sqlite.org/wasm/doc/trunk/persistence.md) for more details.

## How to use

1. Install Nuget Package
2. In your component, add

```csharp
    [Inject]
    public IJSRuntime JSRuntime { get; set; }
    
    private NCSqlite nCSqlite;

    protected override Task OnInitializedAsync()
    {
        // initialize ncSQLite
        nCSqlite = new NCSqlite(JSRuntime, "test.db");

        return base.OnInitializedAsync();
    }
```

3. Add `using Newtonsoft.Json.Linq` 

### Insert Data
The library can support most POCO and no additional mapping is required. Just use `.Upsert()` to insert data. Table with the same name as POCO type will be automatically created. 

For attributes which are not String, Integer, Float, Boolean, Date - the value of that attribute is stored as JSON and NC-BlazorSQLite will automatically deserializes the JSON back for you.

from this POCO:
```csharp
    public class MyData
    {
        public int Id { get; set; }

        public string Value { get; set; }

        public MyData Nested { get; set; }
    }
```

Use:
```csharp
    await nCSqlite.Upsert(new MyData()
        {
            Id = 1,
            Value = "Outer",
            Nested = new MyData()
            {
                Id = 2,
                Value = "Inner"
            }
        });
```

### Query Data
To Query data, NC-BlazorSQLite uses SQL Statement directly - there is no need to fear SQL injection attacks or anything, remember: this is a temporary database in Browser's memory!!

```csharp
    await nCSqlite.Execute("SELECT * FROM MyData", (item) =>
    {
        Console.WriteLine(item); // item is a JObject
    });
```

Your handler function is called on each row returned by the statement. You can directly access each column by using `item["Value"]` or convert the JObject into your own type using `item.ToObject<MyData>()`. This allows for aggregate or join queries which may not maps to any POCO you have.

For example:
```csharp
    await nCSqlite.Execute("SELECT 1", (item) =>
    {
        Console.WriteLine(item["1"]); // print 1
    });
```

### Run SQL Statement
Just use Execute without the second argument.

```csharp
    await nCSqlite.Execute("DELETE FROM MyData");
```