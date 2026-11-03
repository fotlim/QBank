using System.Reflection;

namespace QBank.App.Services;

/// <summary>
/// 程序身份信息：版本、开发者、仓库、以及「这东西是谁写的」。
///
/// 放在一处是为了让「关于」卡片、彩蛋、启动日志、备份文件都用同一份数据 ——
/// 发版时只改 csproj 里的 Version，这里全跟着走。
/// </summary>
public static class AppInfo
{
    /// <summary>显示名。</summary>
    public const string ProductName = "题库工坊";

    /// <summary>英文名 / exe 名。</summary>
    public const string ProductNameEn = "QBank";

    /// <summary>开发者（GitHub 账号同此）。</summary>
    public const string Developer = "fotlim";

    /// <summary>开发者邮箱（也是 git 提交里的 author）。</summary>
    public const string DeveloperEmail = "fotlim@users.noreply.github.com";

    /// <summary>项目仓库。</summary>
    public const string Repository = "https://github.com/fotlim/QBank";

    /// <summary>问题反馈。</summary>
    public const string IssuesUrl = Repository + "/issues";

    /// <summary>许可证。</summary>
    public const string License = "MIT";

    // ─── 创作工具（彩蛋里会摊开讲） ───────────────────────────────────

    /// <summary>写这个程序的工具。</summary>
    public const string BuiltWith = "DeepSeek Harness（DSH）";

    /// <summary>驱动那个工具的模型。</summary>
    public const string BuiltWithModel = "deepseek-v4.1-flash";

    /// <summary>本项目第一次动工的时间。</summary>
    public const string StartedAt = "2026-11-02";

    /// <summary>
    /// 彩蛋正文。用 ASCII 鱼是因为这个程序的图标本来就是条鱼。
    /// </summary>
    public const string EasterEggFish =
        "        ,gggg,\n" +
        "      ,88\"\"\"\"Y8b,\n" +
        "     d8\"      \"8b\n" +
        "    d8'    _   `8b\n" +
        "    88    (o)   88\n" +
        "    88    _|_   88\n" +
        "    Y8b        d8P\n" +
        "     `Y8b____d8P'\n" +
        "       `\"Y888P\"'";

    /// <summary>
    /// 彩蛋里那句随机的碎碎念。每次点开都不一样，纯属好玩。
    /// </summary>
    public static readonly string[] EasterEggLines =
    {
        "这条鱼为了少花你的 token，把每个搜索引擎都试了一遍，然后自己被限流了。",
        "它写代码的时候会一边写一边唱歌，歌里唱的就是正在写的那段代码。",
        "自测 25 项全绿是它最开心的事，比吃白饭还开心（这句它自己不会承认）。",
        "它把 Umi-OCR 的 296MB 引擎砍到 242MB 的时候，手抖了三次。",
        "「网页搜索没被调用」那次，它查了四十分钟，最后发现是自己只看了前 20000 个字符。",
        "它从不写死版本号 —— 因为改起来会忘（其实是有一次真的忘了）。",
        "这个程序里的中文提示语，全部是它一个字一个字敲的，包括「哼」。",
    };

    /// <summary>当前版本（取程序集版本，发版只改 csproj）。</summary>
    public static string Version
    {
        get
        {
            try
            {
                var asm = typeof(AppInfo).Assembly;
                var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(info))
                {
                    // 带 +commit 后缀的只留前面那段
                    var plus = info.IndexOf('+');
                    return plus > 0 ? info[..plus] : info;
                }

                var v = asm.GetName().Version;
                if (v is not null) return $"{v.Major}.{v.Minor}.{v.Build}";
            }
            catch
            {
                // 读不到就算了
            }
            return "1.0.x";
        }
    }

    /// <summary>「题库工坊 v1.0.9」这种一句话版本。</summary>
    public static string VersionLine => $"{ProductName} v{Version}";

    /// <summary>这个安装包带不带内置离线识别引擎（完整版才有 ocr-engine 目录）。</summary>
    public static bool HasBundledEngine
    {
        get
        {
            try
            {
                return QBank.Core.Ocr.UmiOcrEngine.HasBundledEngine;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>标准版 / 完整版。</summary>
    public static string Edition => HasBundledEngine ? "完整版（内置离线识别引擎）" : "标准版";

    /// <summary>彩蛋里那句随机碎碎念。</summary>
    public static string RandomEasterEggLine()
    {
        var lines = EasterEggLines;
        if (lines.Length == 0) return string.Empty;
        return lines[Random.Shared.Next(lines.Length)];
    }
}
