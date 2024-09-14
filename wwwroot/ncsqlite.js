export async function getInstance(dotnetRef, dbFileName)
{
    const loadScript = async function (src)
    {
        return new Promise((resolve, reject) =>
        {
            const script = document.createElement('script');
            script.src = src;
            script.onload = () => resolve(script);
            script.onerror = () => reject(new Error(`Failed to load script: ${src}`));
            document.head.appendChild(script);
        });
    };

    await loadScript('./_content/NC.BlazorSQLite/sqlite3-worker1-promiser.js');

    const promiserFactory = globalThis.sqlite3Worker1Promiser.v2;
    delete globalThis.sqlite3Worker1Promiser;

    let instance = {};
    instance.dotnetRef = dotnetRef;

    instance.promiserConfig = {
        debug: 1 ? undefined : (...args) => console.debug('worker debug', ...args),
        onunhandled: function (ev)
        {
            dotnetRef.invokeMethodAsync("OnError", "Unhandled worker message:", ev.data);
        },
        onerror: function (ev)
        {
            dotnetRef.invokeMethodAsync("OnError", "worker1 error:", ev.data);
        }
    };

    instance.sqlite = await promiserFactory(instance.promiserConfig);

    instance.exec = async function (sql)
    {
        let columnNames = [];

        try
        {
            await instance.sqlite('exec', {
                sql,
                columnNames,
                callback: function (item)
                {
                    instance.dotnetRef.invokeMethodAsync("OnRow", item);
                },
                rowMode: 'object',
            });
        }
        catch (ex)
        {
            dotnetRef.invokeMethodAsync("OnError", ex.result.message, ex.result);
        }
    }

    try
    {
        await instance.sqlite('open', { filename: dbFileName });
    }
    catch (ex)
    {
        dotnetRef.invokeMethodAsync("OnError", ex.result.message, ex.result);
    }

    return instance;
}