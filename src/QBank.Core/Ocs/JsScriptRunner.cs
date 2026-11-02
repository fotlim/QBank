using System.Text.Encodings.Web;
using System.Text.Json;
using Jint;

namespace QBank.Core.Ocs;

public sealed class JsRunOutcome
{
    public bool Ok { get; init; }
    public string? Error { get; init; }

    /// <summary>返回值的 JSON 文本；返回 undefined 时为 null。</summary>
    public string? Json { get; init; }

    public bool IsUndefined { get; init; }

    public List<string> Logs { get; init; } = new();

    public Dictionary<string, string> Storage { get; init; } = new();
}

/// <summary>
/// 用纯托管 JS 引擎（Jint）执行 OCS 题库配置里的 <c>handler</c> 与 data 字段解析器。
///
/// OCS 的做法是 <c>Function(handler)()</c>，即把 handler 字符串当成一个函数体来求值，
/// 再拿返回的函数去处理响应。这里等价地包一层 <c>(function(){ &lt;handler&gt; })()</c>，
/// 并给脚本提供 <c>localStorage</c> / <c>console</c> 垫片（OCS 文档里“魔改 ChatGPT”的示例会用到）。
/// 脚本运行有超时、语句数与内存上限，避免恶意题库配置把进程拖死。
/// </summary>
public static class JsScriptRunner
{
    private static readonly JsonSerializerOptions DtoOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed class ScriptResultDto
    {
        public bool Ok { get; set; }
        public string? Error { get; set; }
        public string? Json { get; set; }
        public bool IsUndefined { get; set; }
        public List<string>? Logs { get; set; }
        public Dictionary<string, string>? Storage { get; set; }
    }

    /// <summary>用 JSON 值作为参数运行 handler（模拟 OCS 收到 json 响应）。</summary>
    public static JsRunOutcome RunWithJsonArg(
        string handlerCode,
        string jsonText,
        Dictionary<string, string>? storage = null,
        TimeSpan? timeout = null)
        => Run(handlerCode, "JSON.parse(" + JsonSerializer.Serialize(jsonText ?? "null") + ")", storage, timeout);

    /// <summary>用字符串作为参数运行 handler（模拟 OCS 收到 text 响应）。</summary>
    public static JsRunOutcome RunWithTextArg(
        string handlerCode,
        string text,
        Dictionary<string, string>? storage = null,
        TimeSpan? timeout = null)
        => Run(handlerCode, JsonSerializer.Serialize(text ?? string.Empty), storage, timeout);

    /// <summary>
    /// 运行 OCS 的 data 字段解析器：参数是 env 对象 <c>{title, options, type}</c>。
    /// </summary>
    public static JsRunOutcome RunWithEnvArg(
        string handlerCode,
        IReadOnlyDictionary<string, string> env,
        Dictionary<string, string>? storage = null,
        TimeSpan? timeout = null)
        => Run(handlerCode, JsonSerializer.Serialize(env), storage, timeout);

    private static JsRunOutcome Run(
        string handlerCode,
        string argExpression,
        Dictionary<string, string>? storage,
        TimeSpan? timeout)
    {
        if (handlerCode is null) throw new ArgumentNullException(nameof(handlerCode));

        var storageJson = JsonSerializer.Serialize(storage ?? new Dictionary<string, string>(), StorageOptions);
        var script = BuildScript(handlerCode, argExpression, storageJson);

        try
        {
            var engine = new Engine(options =>
            {
                options.Strict(false);
                options.TimeoutInterval(timeout ?? TimeSpan.FromSeconds(10));
                options.MaxStatements(300_000);
                options.LimitMemory(32L * 1024 * 1024);
            });

            var value = engine.Evaluate(script);
            var text = value?.ToString() ?? string.Empty;
            var dto = JsonSerializer.Deserialize<ScriptResultDto>(text, DtoOptions);

            if (dto is null)
                return new JsRunOutcome { Ok = false, Error = "脚本没有返回可解析的结果" };

            return new JsRunOutcome
            {
                Ok = dto.Ok,
                Error = dto.Error,
                Json = dto.Json,
                IsUndefined = dto.IsUndefined,
                Logs = dto.Logs ?? new List<string>(),
                Storage = dto.Storage ?? new Dictionary<string, string>(),
            };
        }
        catch (Exception ex)
        {
            var message = ex.Message;
            if (ex.GetType().Name.Contains("Timeout", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
                message = "脚本执行超时（可能写成了死循环）";
            return new JsRunOutcome { Ok = false, Error = message };
        }
    }

    private static readonly JsonSerializerOptions StorageOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string BuildScript(string handlerCode, string argExpression, string storageJson)
    {
        // 注意：handlerCode 直接内联成函数体（等价于 OCS 的 Function(handler)），
        // 前后各留一个换行，避免代码末尾的行注释把收尾括号注释掉。
        var sb = new System.Text.StringBuilder();
        sb.Append("(function(){\n");
        sb.Append("  var __logs = [];\n");
        sb.Append("  var __store = ").Append(storageJson).Append(";\n");
        sb.Append("""
  var localStorage = {
    getItem: function(k){ k = String(k); return Object.prototype.hasOwnProperty.call(__store, k) ? __store[k] : null; },
    setItem: function(k, v){ __store[String(k)] = String(v); },
    removeItem: function(k){ delete __store[String(k)]; },
    clear: function(){ __store = {}; },
    key: function(i){ var ks = Object.keys(__store); return i >= 0 && i < ks.length ? ks[i] : null; }
  };
  Object.defineProperty(localStorage, 'length', { get: function(){ return Object.keys(__store).length; } });
  var __fmt = function(x){
    try {
      if (typeof x === 'string') return x;
      if (typeof x === 'undefined') return 'undefined';
      return JSON.stringify(x);
    } catch (e) { return String(x); }
  };
  var console = {
    log: function(){ __logs.push(Array.prototype.slice.call(arguments).map(__fmt).join(' ')); },
    info: function(){ __logs.push(Array.prototype.slice.call(arguments).map(__fmt).join(' ')); },
    warn: function(){ __logs.push('[warn] ' + Array.prototype.slice.call(arguments).map(__fmt).join(' ')); },
    error: function(){ __logs.push('[error] ' + Array.prototype.slice.call(arguments).map(__fmt).join(' ')); },
    debug: function(){ __logs.push(Array.prototype.slice.call(arguments).map(__fmt).join(' ')); }
  };
  try {
    var __fn = (function(){
""");
        sb.Append(handlerCode);
        sb.Append(@"
    })();
    if (typeof __fn !== 'function') throw new Error('handler 响应处理器必须返回一个函数');
    var __arg = ").Append(argExpression).Append(@";
    var __out = __fn(__arg);
    var __isUndef = (typeof __out === 'undefined');
    var __json = __isUndef ? null : JSON.stringify(__out);
    if (typeof __json === 'undefined') __json = null;
    return JSON.stringify({ ok: true, json: __json, isUndefined: __isUndef, logs: __logs, storage: __store });
  } catch (e) {
    var __msg = (e && e.message) ? String(e.message) : String(e);
    return JSON.stringify({ ok: false, error: __msg, logs: __logs, storage: __store });
  }
})()
");
        return sb.ToString();
    }
}